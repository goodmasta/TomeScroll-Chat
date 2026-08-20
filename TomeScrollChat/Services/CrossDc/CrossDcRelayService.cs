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
/// rights and pulling server logs - since that only needs a request/response pair over the same socket,
/// not a whole feature area of its own.
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
    private readonly SemaphoreSlim sendLock = new(1, 1);

    private RelayIdentityService? identity;
    private CancellationTokenSource? runCts;
    private Task? runTask;
    private string? connectedUrl;
    private ClientWebSocket? socket;

    /// <summary>Which of the two admin-tooling requests (there's no correlation ID in the relay's own
    /// protocol) is currently awaiting a reply, so a generic <c>error</c> frame gets routed to the right
    /// one of <see cref="AdminError"/>/<see cref="LogsError"/>. Only meaningful because exactly these two
    /// fire-and-forget-then-correlate-by-type requests exist right now - if a third one is ever added,
    /// this stops being enough and needs a real request ID instead.</summary>
    private enum PendingAction { None, ClaimAdmin, GetLogs }
    private PendingAction pendingAction = PendingAction.None;

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

    /// <summary>True once <c>claimAdmin</c> has succeeded on the *current* connection. Not persisted or
    /// re-checked on reconnect - the relay itself remembers admin status durably, but this client has no
    /// "am I already admin" query, only claimAdmin (which consumes a single-use key, so re-sending an
    /// already-used one after a reload just errors). Re-run "/tomescrollc" reconnect and claim again with
    /// a fresh key if that ever matters, or check server-side directly.</summary>
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
    /// every startup. Result arrives asynchronously as <see cref="IsAdmin"/>/<see cref="AdminError"/>
    /// updating on a later frame, not a direct return value - same as every other relay round trip.</summary>
    public Task<bool> ClaimAdminAsync(string key, CancellationToken cancellationToken = default) =>
        SendPendingAsync(PendingAction.ClaimAdmin, new RelayClaimAdminRequest("claimAdmin", key), cancellationToken);

    /// <summary>Sends <c>getLogs</c> - admin-only server-side, so this only ever succeeds after
    /// <see cref="IsAdmin"/> is true. Result arrives as <see cref="Logs"/>/<see cref="LogsError"/>
    /// updating on a later frame.</summary>
    public Task<bool> RequestLogsAsync(int lines, CancellationToken cancellationToken = default) =>
        SendPendingAsync(PendingAction.GetLogs, new RelayGetLogsRequest("getLogs", lines), cancellationToken);

    /// <summary>False if there's no live connection to send on right now - the caller (Settings) is
    /// expected to only offer these actions while <see cref="IsConnected"/> anyway, this is just the
    /// non-racy guard against the connection dropping between the button being drawn and clicked.</summary>
    private async Task<bool> SendPendingAsync<T>(PendingAction action, T message, CancellationToken cancellationToken)
    {
        var ws = socket;
        if (ws is not { State: WebSocketState.Open })
            return false;

        // One send at a time - ClientWebSocket supports one concurrent send alongside the receive loop's
        // one concurrent receive, but not two overlapping sends (e.g. a double-clicked button).
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            pendingAction = action;
            await RelaySocketIo.SendAsync(ws, message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to send a cross-DC relay {Action} request", action);
            pendingAction = PendingAction.None;
            return false;
        }
        finally
        {
            sendLock.Release();
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

    /// <summary>Handles <c>adminGranted</c>/<c>logs</c>/<c>error</c> (the only requests this client ever
    /// sends right now); everything else is just logged by type - a placeholder for the next increment
    /// (pairing/messaging), proving the receive loop works end to end without yet having anywhere real
    /// to route those frames to.</summary>
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
                pendingAction = PendingAction.None;
                log.Info("TomeScrollChat: cross-DC relay admin claim succeeded");
                break;

            case "logs":
                var logsMessage = TryDeserialize<RelayLogsMessage>(rawJson);
                Logs = logsMessage?.Lines ?? Array.Empty<string>();
                LogsError = null;
                pendingAction = PendingAction.None;
                break;

            case "error":
                var reason = TryDeserialize<RelayErrorMessage>(rawJson)?.Reason ?? "Unknown error";
                if (pendingAction == PendingAction.ClaimAdmin)
                    AdminError = reason;
                else if (pendingAction == PendingAction.GetLogs)
                    LogsError = reason;
                else
                    log.Debug("TomeScrollChat: cross-DC relay error with no pending request to attribute it to: {Reason}", reason);
                pendingAction = PendingAction.None;
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
        pendingAction = PendingAction.None;
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
        sendLock.Dispose();
    }
}
