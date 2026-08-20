using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using TomeScrollChat.Models;

namespace TomeScrollChat.Services.CrossDc;

/// <summary>
/// Owns the cross-DC relay connection lifecycle - resolves which server to use from
/// <see cref="Configuration.CrossDcRelayMode"/>, connects, completes the Ed25519 challenge/response
/// handshake (matching TomeScrollRelay's own <c>RelayHandshake</c>), and keeps a background receive
/// loop running while connected. Also the client side of the relay's admin tooling - claiming admin
/// rights, pulling server logs, and reading the connected-client count - since each is just a
/// request/response pair over the same socket, not a whole feature area of its own.
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
/// this increment is identity + the handshake working at all, not yet anything that depends on staying
/// connected unattended - proper reconnect-with-backoff belongs with whatever increment actually needs
/// it (pairing/messaging).</para>
/// </summary>
public sealed class CrossDcRelayService : IDisposable
{
    private readonly string configDirectory;
    private readonly Configuration configuration;
    private readonly IPluginLog log;
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

    private enum PendingAction { None, ClaimAdmin, GetLogs, GetStats }
    private PendingAction pendingAction = PendingAction.None;
    private TaskCompletionSource<bool>? pendingCompletion;

    public CrossDcRelayService(string configDirectory, Configuration configuration, IPluginLog log)
    {
        this.configDirectory = configDirectory;
        this.configuration = configuration;
        this.log = log;
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

    /// <summary>Handles <c>adminGranted</c>/<c>logs</c>/<c>stats</c>/<c>error</c> (the only requests this
    /// client ever sends right now); everything else is just logged by type - a placeholder for the next
    /// increment (pairing/messaging), proving the receive loop works end to end without yet having
    /// anywhere real to route those frames to.</summary>
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
