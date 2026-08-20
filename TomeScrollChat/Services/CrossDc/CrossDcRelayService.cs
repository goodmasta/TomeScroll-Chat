using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using TomeScrollChat.Models;

namespace TomeScrollChat.Services.CrossDc;

/// <summary>One decrypted 1:1 cross-DC chat message, in-memory only for now (not persisted to disk the
/// way native chat history is - a later increment can fold this into <c>ChatHistoryService</c> if this
/// feature graduates beyond Settings into an actual chat surface). <see cref="SenderUserId"/> is whoever
/// actually sent it (this identity's own <see cref="CrossDcRelayService.UserId"/> for an outgoing one).</summary>
public sealed record CrossDcChatMessage(string SenderUserId, string Text, DateTimeOffset Timestamp, bool IsOutgoing);

/// <summary>
/// Owns the cross-DC relay connection lifecycle - resolves which server to use from
/// <see cref="Configuration.CrossDcRelayMode"/>, connects, completes the Ed25519 challenge/response
/// handshake (matching TomeScrollRelay's own <c>RelayHandshake</c>), and keeps a background receive
/// loop running while connected. Also the client side of the relay's admin tooling (claiming admin
/// rights, pulling server logs, reading the connected-client count) and 1:1 pairing (creating/redeeming
/// invite codes, tracking who this identity has paired with) - each just a request/response pair (or, for
/// pairing, an occasional unsolicited push - see <see cref="DispatchFrame"/>'s <c>paired</c> case) over
/// the same socket, not a whole feature area of its own. End-to-end encrypted 1:1 messaging (see
/// <see cref="SendChatMessageAsync"/>) is also owned here; <c>Plugin</c> bridges it into an actual tab via
/// <see cref="ContactAdded"/>/<see cref="MessageAppended"/>.
///
/// <para>Entirely inert while <see cref="Configuration.CrossDcRelayMode"/> is
/// <see cref="RelayMode.Disabled"/> - <see cref="RelayIdentityService"/> (which is what actually
/// generates/persists the identity keypair) is only ever constructed the first time it's actually
/// needed, not eagerly in this type's own constructor, per the explicit "nothing happens at all while
/// the feature is off" requirement.</para>
///
/// <para><see cref="Reconcile"/> is the only connection entry point - call it once at plugin startup and
/// again every time Settings &gt; Cross-DC changes anything (mode, self-hosted URL). It figures out on
/// its own whether that means connecting, disconnecting, or switching servers, and is a no-op if the
/// live connection already matches what the config now says.</para>
///
/// <para><b>No automatic reconnect yet</b> - if the connection drops (network blip, relay restart),
/// <see cref="IsConnected"/> goes false and stays false until something calls <see cref="Reconcile"/>
/// again (e.g. the player revisiting Settings). Deliberately deferred rather than built speculatively:
/// this is still identity/connection-level scope, not yet anything that depends on staying connected
/// unattended - proper reconnect-with-backoff belongs with whatever increment actually needs it
/// (message send/receive).</para>
/// </summary>
public sealed class CrossDcRelayService : IDisposable
{
    private readonly string configDirectory;
    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private readonly Func<string?> getLocalPlayerName;
    private readonly object gate = new();

    // Serializes entire admin-tooling request/response *cycles*, not just the send - a second call
    // blocks until the first one's response (success or error) actually arrives. The relay's error
    // frames carry no correlation ID, so this is what makes attributing one to the right request
    // (AdminError vs LogsError vs StatsError) unambiguous: with this in place, at most one of these
    // requests is ever outstanding at a time, so there's nothing to disambiguate in the first place -
    // simpler and more robust than trying to track/queue multiple in-flight requests.
    private readonly SemaphoreSlim requestLock = new(1, 1);

    private RelayIdentityService? identity;
    private CancellationTokenSource? runCts;
    private Task? runTask;
    private string? connectedUrl;
    private ClientWebSocket? socket;

    private enum PendingAction { None, ClaimAdmin, GetLogs, GetStats, CreateInvite, RedeemInvite }
    private PendingAction pendingAction = PendingAction.None;
    private TaskCompletionSource<bool>? pendingCompletion;

    // In-memory only, see CrossDcChatMessage's own doc comment. Keyed by the *other* party's userId.
    private readonly Dictionary<string, List<CrossDcChatMessage>> messagesByContact = new();

    /// <summary>Fired for every contact this identity has ever paired with on the current relay, once on
    /// each fresh connect (so a tab exists for each of them even if the player never opens Settings) and
    /// again the moment a brand-new pairing completes live. <c>Plugin</c> subscribes to this to create/
    /// find the matching tab (<see cref="Services.TabManager.GetOrCreateCrossDcTab"/>) - deliberately
    /// fires for *already-known* contacts too on every reconnect rather than only new ones, so a tab the
    /// player closed comes back the same self-healing way a whisper tab does.</summary>
    public event Action<string>? ContactAdded;

    /// <summary>Fired every time a message (incoming or outgoing) is added to <see cref="GetMessages"/>'s
    /// backing store - <c>Plugin</c> subscribes to this to push it into <c>TabMessageBuffer</c>/
    /// <c>ChatHistoryService</c> the same way <c>ChatCaptureService.MessageRouted</c> does for native
    /// chat, so the cross-DC tab actually shows it live instead of only on next open.</summary>
    public event Action<string, CrossDcChatMessage>? MessageAppended;

    public CrossDcRelayService(string configDirectory, Configuration configuration, IPluginLog log, Func<string?> getLocalPlayerName)
    {
        this.configDirectory = configDirectory;
        this.configuration = configuration;
        this.log = log;
        this.getLocalPlayerName = getLocalPlayerName;
    }

    public bool IsConnected { get; private set; }

    /// <summary>This installation's relay user ID, once the handshake completes - null while
    /// disconnected/connecting.</summary>
    public string? UserId { get; private set; }

    /// <summary>Set on the most recent connection failure, for Settings to surface - cleared the moment
    /// a connection actually succeeds.</summary>
    public string? LastError { get; private set; }

    /// <summary>True once admin rights are established on the *current* connection - either freshly via
    /// <c>claimAdmin</c> this session, or recalled from a previous successful claim on this same relay
    /// URL (see <see cref="RelayIdentityService.IsKnownAdmin"/>; the relay's own grant is durable, so a
    /// fresh reconnect doesn't need the bootstrap key again once it's already been used once).</summary>
    public bool IsAdmin { get; private set; }

    /// <summary>Reason the most recent <c>claimAdmin</c> attempt failed - null if none yet, or the last
    /// one succeeded.</summary>
    public string? AdminError { get; private set; }

    /// <summary>Most recent <c>getLogs</c> result, oldest first (matches the relay's own ordering) -
    /// empty until the first successful fetch.</summary>
    public IReadOnlyList<string> Logs { get; private set; } = Array.Empty<string>();

    /// <summary>Reason the most recent <c>getLogs</c> attempt failed (e.g. not an admin) - null if none
    /// yet, or the last one succeeded.</summary>
    public string? LogsError { get; private set; }

    /// <summary>Most recent <c>getStats</c> result - how many clients are connected to *this* relay
    /// instance (see TomeScrollRelay's own caveat: per-instance, not a global total once there's more
    /// than one). Null until the first successful fetch.</summary>
    public int? ConnectedClients { get; private set; }

    /// <summary>Reason the most recent <c>getStats</c> attempt failed - null if none yet, or the last
    /// one succeeded.</summary>
    public string? StatsError { get; private set; }

    /// <summary>Most recently created invite code, good for 10 minutes and single-use (see
    /// TomeScrollRelay's own <c>PairingCodeRegistry</c>) - null until the first successful
    /// <see cref="CreateInviteAsync"/>.</summary>
    public string? InviteCode { get; private set; }

    /// <summary>Reason the most recent <c>createInvite</c> attempt failed - e.g. the per-user pending-
    /// invite cap. Null if none yet, or the last one succeeded.</summary>
    public string? InviteError { get; private set; }

    /// <summary>Every user ID this identity has ever successfully paired with on the *current* relay
    /// (empty while disconnected) - refreshed on connect and every time a new pairing completes, whether
    /// this client redeemed a code itself or someone else redeemed one of its invites.</summary>
    public IReadOnlyList<string> Contacts { get; private set; } = Array.Empty<string>();

    /// <summary>Reason the most recent <c>redeemInvite</c> attempt failed (invalid/expired code, blocked,
    /// etc.) - null if none yet, or the last one succeeded.</summary>
    public string? PairError { get; private set; }

    /// <summary>Reconciles the live connection against the current config. Safe to call any time,
    /// including repeatedly with nothing changed (a no-op in that case).</summary>
    public void Reconcile()
    {
        var targetUrl = ResolveUrl();

        lock (gate)
        {
            if (targetUrl == connectedUrl && (targetUrl == null || runTask != null))
                return;

            StopLocked();

            if (targetUrl == null)
                return;

            identity ??= new RelayIdentityService(configDirectory, log);
            connectedUrl = targetUrl;
            runCts = new CancellationTokenSource();
            runTask = Task.Run(() => RunAsync(targetUrl, identity, runCts.Token));
        }
    }

    /// <summary>Sends <c>claimAdmin</c> with the given bootstrap key - the relay's own admin tooling
    /// (see TomeScrollRelay's <c>AdminBootstrap</c>) prints a fresh single-use key to its own log on
    /// every startup. Result also surfaces as <see cref="IsAdmin"/>/<see cref="AdminError"/> for Settings
    /// to poll across frames, since it's typically called fire-and-forget from a button click.</summary>
    public Task<bool> ClaimAdminAsync(string key, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.ClaimAdmin, new RelayClaimAdminRequest("claimAdmin", key), cancellationToken);

    /// <summary>Sends <c>getLogs</c> - admin-only server-side, so this only ever succeeds after
    /// <see cref="IsAdmin"/> is true. Result also surfaces as <see cref="Logs"/>/<see cref="LogsError"/>.</summary>
    public Task<bool> RequestLogsAsync(int lines, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.GetLogs, new RelayGetLogsRequest("getLogs", lines), cancellationToken);

    /// <summary>Sends <c>getStats</c> - admin-only server-side. Result also surfaces as
    /// <see cref="ConnectedClients"/>/<see cref="StatsError"/>.</summary>
    public Task<bool> RequestStatsAsync(CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.GetStats, new RelayGetStatsRequest("getStats"), cancellationToken);

    /// <summary>Sends <c>createInvite</c> - result also surfaces as <see cref="InviteCode"/>/
    /// <see cref="InviteError"/>. The code is meant to be shared entirely out-of-band (voice, Discord,
    /// whatever) with whoever should redeem it - never sent through this relay or native game chat, per
    /// the design decision that excluded in-game chat from the invite flow entirely.</summary>
    public Task<bool> CreateInviteAsync(CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.CreateInvite, new RelayCreateInviteRequest("createInvite"), cancellationToken);

    /// <summary>Sends <c>redeemInvite</c> with a code someone else created and shared out-of-band.
    /// Result also surfaces as <see cref="Contacts"/> (gains the new pairing)/<see cref="PairError"/>.</summary>
    public Task<bool> RedeemInviteAsync(string code, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.RedeemInvite, new RelayRedeemInviteRequest("redeemInvite", code), cancellationToken);

    /// <summary>Message history with <paramref name="contactUserId"/>, oldest first - empty if there's
    /// been no exchange with them yet this session (not persisted, see <see cref="CrossDcChatMessage"/>'s
    /// doc comment).</summary>
    public IReadOnlyList<CrossDcChatMessage> GetMessages(string contactUserId) =>
        messagesByContact.TryGetValue(contactUserId, out var messages) ? messages : Array.Empty<CrossDcChatMessage>();

    /// <summary>True once <paramref name="contactUserId"/>'s X25519 public key has been received (an
    /// automatic <c>keyAnnounce</c> sent right after pairing - see the <c>paired</c> case in
    /// <see cref="DispatchFrame"/>), so <see cref="SendChatMessageAsync"/> can actually encrypt something
    /// for them. False doesn't mean anything's wrong - the announcement may simply not have arrived yet
    /// (it's an ordinary relay message, so it's offline-queued same as anything else if they were
    /// offline when this identity first paired with them).</summary>
    public bool HasKeyFor(string contactUserId) =>
        connectedUrl != null && identity?.GetPeerPublicKey(connectedUrl, contactUserId) != null;

    /// <summary>The contact's character name (as announced via <c>keyAnnounce</c> - see
    /// <see cref="SendKeyAnnounceAsync"/>/the <c>keyAnnounce</c> case in <see cref="HandleIncomingPayload"/>),
    /// or the raw relay userId if no announcement carrying a name has arrived yet - always something
    /// non-empty either way, so callers never need their own fallback.</summary>
    public string GetDisplayName(string contactUserId) =>
        (connectedUrl != null ? identity?.GetPeerDisplayName(connectedUrl, contactUserId) : null) ?? contactUserId;

    /// <summary>Encrypts and sends a chat message to an already-paired contact - false (and no local
    /// history entry added) if there's no live connection, no key for them yet (see
    /// <see cref="HasKeyFor"/>), or the send itself fails. Unlike the admin-tooling requests, there's no
    /// relay-level response to a <c>send</c> to wait for, so this returns as soon as the frame is on the
    /// wire, not once the peer has received it.</summary>
    public async Task<bool> SendChatMessageAsync(string contactUserId, string text, CancellationToken cancellationToken = default)
    {
        if (connectedUrl == null || identity == null || UserId == null)
            return false;

        var peerKey = identity.GetPeerPublicKey(connectedUrl, contactUserId);
        if (peerKey == null)
        {
            // No key yet - re-announce ours in case the original announcement never arrived (e.g. their
            // queue was full at the time), rather than leaving both sides stuck with no way to recover.
            _ = SendKeyAnnounceAsync(contactUserId, cancellationToken);
            return false;
        }

        var nonce = RandomNumberGenerator.GetBytes(24); // XChaCha20-Poly1305's nonce size
        var plaintext = Encoding.UTF8.GetBytes(text);
        var ciphertext = identity.EncryptFor(contactUserId, peerKey, UserId, nonce, plaintext);
        if (ciphertext == null)
            return false;

        var envelope = new ChatMessageEnvelope("chat", Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext));
        var payload = JsonSerializer.Serialize(envelope, RelayProtocolJson.Options);
        if (!await SendRawAsync(new RelaySendRequest("send", contactUserId, payload), cancellationToken).ConfigureAwait(false))
            return false;

        AppendMessage(contactUserId, new CrossDcChatMessage(UserId, text, DateTimeOffset.UtcNow, IsOutgoing: true));
        return true;
    }

    /// <summary>Best-effort, fire-and-forget - failure just means the contact won't get this identity's
    /// key yet; <see cref="SendChatMessageAsync"/> retries it automatically the next time it's needed.</summary>
    private async Task SendKeyAnnounceAsync(string contactUserId, CancellationToken cancellationToken)
    {
        if (identity == null)
            return;

        var envelope = new ChatKeyAnnounceEnvelope("keyAnnounce", identity.EncryptionPublicKeyBase64, getLocalPlayerName());
        var payload = JsonSerializer.Serialize(envelope, RelayProtocolJson.Options);
        await SendRawAsync(new RelaySendRequest("send", contactUserId, payload), cancellationToken).ConfigureAwait(false);
    }

    private void AppendMessage(string contactUserId, CrossDcChatMessage message)
    {
        if (!messagesByContact.TryGetValue(contactUserId, out var messages))
            messagesByContact[contactUserId] = messages = new List<CrossDcChatMessage>();

        messages.Add(message);
        MessageAppended?.Invoke(contactUserId, message);
    }

    /// <summary>Clears <see cref="IsAdmin"/> and the locally-cached "admin on this URL" fact (see
    /// <see cref="RelayIdentityService.ForgetAdmin"/>), for when the relay's own admin record no longer
    /// agrees with what this client remembers (e.g. the relay's Redis was flushed independently of
    /// anything this client did) - lets the player re-claim with a fresh bootstrap key instead of being
    /// stuck with a client that insists it's admin while the server keeps saying otherwise. A no-op
    /// while disconnected, since there's no URL to forget.</summary>
    public void ForgetAdminStatus()
    {
        if (connectedUrl != null)
            identity?.ForgetAdmin(connectedUrl);

        IsAdmin = false;
        AdminError = null;
    }

    /// <summary>Sends one request and waits for its matching response to actually arrive (success or
    /// error) before returning - see the <see cref="requestLock"/> field doc comment for why. False if
    /// there's no live connection to send on, the send itself failed, or the connection dropped before a
    /// response arrived.</summary>
    private async Task<bool> SendAndAwaitAsync<T>(PendingAction action, T message, CancellationToken cancellationToken)
    {
        var ws = socket;
        if (ws is not { State: WebSocketState.Open })
            return false;

        await requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            pendingAction = action;
            pendingCompletion = completion;
            await RelaySocketIo.SendAsync(ws, message, cancellationToken).ConfigureAwait(false);

            await using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
                return await completion.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to send a cross-DC relay {Action} request", action);
            return false;
        }
        finally
        {
            pendingAction = PendingAction.None;
            pendingCompletion = null;
            requestLock.Release();
        }
    }

    /// <summary>Sends a frame with no relay-level response to wait for (<c>send</c> is fire-and-forget
    /// at the protocol level - see TomeScrollRelay's own <c>RouteAsync</c>, which never replies to the
    /// sender). Still goes through <see cref="requestLock"/>, just for the duration of the write itself
    /// rather than a full cycle - <see cref="ClientWebSocket"/> only tolerates one send in flight at a
    /// time, and this lock is already what serializes every other send against that same constraint.</summary>
    private async Task<bool> SendRawAsync<T>(T message, CancellationToken cancellationToken)
    {
        var ws = socket;
        if (ws is not { State: WebSocketState.Open })
            return false;

        await requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RelaySocketIo.SendAsync(ws, message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to send a cross-DC relay message");
            return false;
        }
        finally
        {
            requestLock.Release();
        }
    }

    private string? ResolveUrl() => configuration.CrossDcRelayMode switch
    {
        RelayMode.Managed => ManagedRelayEndpoint.GetUrl(),
        RelayMode.SelfHosted when IsValidRelayUrl(configuration.CrossDcRelaySelfHostedUrl) => configuration.CrossDcRelaySelfHostedUrl,
        _ => null,
    };

    private static bool IsValidRelayUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "wss" || uri.Scheme == "ws");

    private async Task RunAsync(string url, RelayIdentityService identity, CancellationToken cancellationToken)
    {
        using var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

            var userId = await HandshakeAsync(ws, identity, cancellationToken).ConfigureAwait(false);
            if (userId == null)
            {
                log.Warning("TomeScrollChat: cross-DC relay handshake failed ({Url})", url);
                SetDisconnected("Handshake failed");
                return;
            }

            socket = ws;
            IsConnected = true;
            UserId = userId;
            LastError = null;
            if (identity.IsKnownAdmin(url))
            {
                IsAdmin = true;
                log.Info("TomeScrollChat: cross-DC relay admin rights recalled for {UserId} ({Url})", userId, url);
            }
            Contacts = identity.GetContacts(url).ToArray();
            foreach (var contactUserId in Contacts)
                ContactAdded?.Invoke(contactUserId);
            log.Info("TomeScrollChat: connected to cross-DC relay as {UserId} ({Url})", userId, url);

            while (!cancellationToken.IsCancellationRequested)
            {
                var raw = await RelaySocketIo.ReceiveTextAsync(ws, cancellationToken).ConfigureAwait(false);
                if (raw == null)
                    break;

                DispatchFrame(raw);
            }
        }
        catch (OperationCanceledException)
        {
            // Reconcile() (or Dispose) tearing this connection down on purpose - not a failure.
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: cross-DC relay connection failed ({Url})", url);
            SetDisconnected(ex.Message);
            return;
        }

        SetDisconnected(null);
    }

    /// <summary>Sends the Ed25519 public key + a signature over the relay's nonce, matching
    /// TomeScrollRelay's <c>RelayHandshake.TryAuthenticateAsync</c> exactly (standard base64 both
    /// directions, same as that server expects). Returns the assigned user ID, or null on any
    /// malformed/unexpected response.</summary>
    private static async Task<string?> HandshakeAsync(ClientWebSocket ws, RelayIdentityService identity, CancellationToken cancellationToken)
    {
        var challengeRaw = await RelaySocketIo.ReceiveTextAsync(ws, cancellationToken).ConfigureAwait(false);
        if (challengeRaw == null)
            return null;

        var challenge = JsonSerializer.Deserialize<RelayChallengeMessage>(challengeRaw, RelayProtocolJson.Options);
        if (challenge is not { Type: "challenge", Nonce.Length: > 0 })
            return null;

        var nonce = Convert.FromBase64String(challenge.Nonce);
        var signature = identity.Sign(nonce);
        var hello = new RelayHelloMessage(identity.SigningPublicKeyBase64, Convert.ToBase64String(signature));
        await RelaySocketIo.SendAsync(ws, hello, cancellationToken).ConfigureAwait(false);

        var connectedRaw = await RelaySocketIo.ReceiveTextAsync(ws, cancellationToken).ConfigureAwait(false);
        if (connectedRaw == null)
            return null;

        var connected = JsonSerializer.Deserialize<RelayConnectedMessage>(connectedRaw, RelayProtocolJson.Options);
        return connected is { Type: "connected", Id.Length: > 0 } ? connected.Id : null;
    }

    /// <summary>Handles <c>adminGranted</c>/<c>logs</c>/<c>stats</c>/<c>invite</c>/<c>paired</c>/
    /// <c>error</c>; everything else (actual pairwise messages, groups - not built yet) is just logged
    /// by type for now, proving the receive loop works end to end without anywhere real to route those
    /// frames to yet.</summary>
    private void DispatchFrame(string rawJson)
    {
        string? type;
        try
        {
            type = JsonSerializer.Deserialize<RelayTypeOnly>(rawJson, RelayProtocolJson.Options)?.Type;
        }
        catch (JsonException)
        {
            log.Warning("TomeScrollChat: cross-DC relay sent an unparseable frame");
            return;
        }

        switch (type)
        {
            case "adminGranted":
                IsAdmin = true;
                AdminError = null;
                if (connectedUrl != null)
                    identity?.MarkAdmin(connectedUrl);
                log.Info("TomeScrollChat: cross-DC relay admin claim succeeded");
                pendingCompletion?.TrySetResult(true);
                break;

            case "logs":
                var logsMessage = TryDeserialize<RelayLogsMessage>(rawJson);
                Logs = logsMessage?.Lines ?? Array.Empty<string>();
                LogsError = null;
                pendingCompletion?.TrySetResult(true);
                break;

            case "stats":
                var statsMessage = TryDeserialize<RelayStatsMessage>(rawJson);
                ConnectedClients = statsMessage?.ConnectedClients;
                StatsError = null;
                pendingCompletion?.TrySetResult(true);
                break;

            case "invite":
                var inviteMessage = TryDeserialize<RelayInviteMessage>(rawJson);
                InviteCode = inviteMessage?.Code;
                InviteError = null;
                pendingCompletion?.TrySetResult(true);
                break;

            case "paired":
                // Unlike the other cases above, this can arrive *unsolicited* - the inviter side of a
                // pairing gets this pushed to them (live, or queued for when they next connect) the
                // moment someone else redeems their code, without having sent anything themselves. Only
                // touch pendingCompletion when we're the one who actually sent redeemInvite and is still
                // waiting on it; otherwise just record the new contact and leave whatever else might
                // genuinely be pending (e.g. an unrelated getStats) alone.
                var pairedMessage = TryDeserialize<RelayPairedMessage>(rawJson);
                if (pairedMessage?.With is { Length: > 0 } withId)
                {
                    if (connectedUrl != null)
                        identity?.AddContact(connectedUrl, withId);
                    Contacts = connectedUrl != null ? identity?.GetContacts(connectedUrl).ToArray() ?? Contacts : Contacts;
                    ContactAdded?.Invoke(withId);
                    log.Info("TomeScrollChat: cross-DC relay paired with {With}", withId);
                    // Announce this identity's encryption key to the new contact right away, so a
                    // message can actually be sent to them without the player needing to do anything -
                    // best-effort/fire-and-forget, same as the notification that triggered this.
                    _ = SendKeyAnnounceAsync(withId, CancellationToken.None);
                }
                PairError = null;
                if (pendingAction == PendingAction.RedeemInvite)
                    pendingCompletion?.TrySetResult(true);
                break;

            case "message":
                var incoming = TryDeserialize<RelayMessageEnvelope>(rawJson);
                if (incoming is { From.Length: > 0, Payload.Length: > 0 })
                    HandleIncomingPayload(incoming.From, incoming.Payload);
                break;

            case "error":
                var reason = TryDeserialize<RelayErrorMessage>(rawJson)?.Reason ?? "Unknown error";
                switch (pendingAction)
                {
                    case PendingAction.ClaimAdmin:
                        AdminError = reason;
                        break;
                    case PendingAction.GetLogs:
                        LogsError = reason;
                        break;
                    case PendingAction.GetStats:
                        StatsError = reason;
                        break;
                    case PendingAction.CreateInvite:
                        InviteError = reason;
                        break;
                    case PendingAction.RedeemInvite:
                        PairError = reason;
                        break;
                    default:
                        log.Debug("TomeScrollChat: cross-DC relay error with no pending request to attribute it to: {Reason}", reason);
                        break;
                }
                pendingCompletion?.TrySetResult(false);
                break;

            default:
                log.Debug("TomeScrollChat: cross-DC relay frame received (type={Type})", type ?? "(none)");
                break;
        }
    }

    /// <summary>Parses the client-level envelope carried inside a relay <c>message</c>'s opaque
    /// <c>payload</c> (see <see cref="ChatKeyAnnounceEnvelope"/>/<see cref="ChatMessageEnvelope"/>'s doc
    /// comment) and handles the two kinds this client understands so far. Anything else is logged and
    /// dropped - not an error, just not something built yet (groups).</summary>
    private void HandleIncomingPayload(string fromUserId, string payloadJson)
    {
        string? innerType;
        try
        {
            innerType = JsonSerializer.Deserialize<RelayTypeOnly>(payloadJson, RelayProtocolJson.Options)?.Type;
        }
        catch (JsonException)
        {
            log.Warning("TomeScrollChat: cross-DC relay message from {From} had an unparseable payload", fromUserId);
            return;
        }

        switch (innerType)
        {
            case "keyAnnounce":
                var announce = TryDeserialize<ChatKeyAnnounceEnvelope>(payloadJson);
                if (announce is { PublicKey.Length: > 0 } && connectedUrl != null)
                {
                    identity?.SetPeerPublicKey(connectedUrl, fromUserId, announce.PublicKey);
                    log.Info("TomeScrollChat: received a cross-DC key announcement from {From}", fromUserId);

                    // A display name riding along with this announcement (see SendKeyAnnounceAsync) is
                    // new information about a contact that may already have a tab named after their raw
                    // userId (created before this arrived) - re-raise ContactAdded so Plugin's
                    // GetOrCreateCrossDcTab gets a chance to upgrade that placeholder name now.
                    if (!string.IsNullOrEmpty(announce.DisplayName))
                    {
                        identity?.SetPeerDisplayName(connectedUrl, fromUserId, announce.DisplayName);
                        ContactAdded?.Invoke(fromUserId);
                    }
                }
                break;

            case "chat":
                var chat = TryDeserialize<ChatMessageEnvelope>(payloadJson);
                if (chat is not { Nonce.Length: > 0, Ciphertext.Length: > 0 } || connectedUrl == null || identity == null || UserId == null)
                    break;

                var peerKey = identity.GetPeerPublicKey(connectedUrl, fromUserId);
                if (peerKey == null)
                {
                    log.Warning("TomeScrollChat: received a chat message from {From} but have no key for them yet - dropping", fromUserId);
                    break;
                }

                var plaintext = identity.DecryptFrom(fromUserId, peerKey, UserId, Convert.FromBase64String(chat.Nonce), Convert.FromBase64String(chat.Ciphertext));
                if (plaintext == null)
                {
                    log.Warning("TomeScrollChat: failed to decrypt a cross-DC message from {From} - dropping", fromUserId);
                    break;
                }

                AppendMessage(fromUserId, new CrossDcChatMessage(fromUserId, Encoding.UTF8.GetString(plaintext), DateTimeOffset.UtcNow, IsOutgoing: false));
                break;

            default:
                log.Debug("TomeScrollChat: cross-DC relay message from {From} had an unrecognized payload type {Type}", fromUserId, innerType ?? "(none)");
                break;
        }
    }

    private T? TryDeserialize<T>(string rawJson) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(rawJson, RelayProtocolJson.Options);
        }
        catch (JsonException ex)
        {
            log.Warning(ex, "TomeScrollChat: cross-DC relay sent a malformed {Type} frame", typeof(T).Name);
            return null;
        }
    }

    private void SetDisconnected(string? error)
    {
        lock (gate)
        {
            socket = null;
            runTask = null;
            connectedUrl = null;
        }

        IsConnected = false;
        UserId = null;
        IsAdmin = false;
        // Unblocks a SendAndAwaitAsync call that was waiting on a reply that will now never come - it
        // returns false, same as an explicit error would.
        pendingCompletion?.TrySetResult(false);
        if (error != null)
            LastError = error;
    }

    /// <summary>Caller must already hold <see cref="gate"/>.</summary>
    private void StopLocked()
    {
        runCts?.Cancel();
        runCts?.Dispose();
        runCts = null;
        runTask = null;
        connectedUrl = null;
        socket = null;
    }

    public void Dispose()
    {
        lock (gate)
            StopLocked();

        identity?.Dispose();
        requestLock.Dispose();
    }
}
