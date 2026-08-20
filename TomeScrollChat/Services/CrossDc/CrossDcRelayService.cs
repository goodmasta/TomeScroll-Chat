using System;
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
/// loop running while connected.
///
/// <para>Entirely inert while <see cref="Configuration.CrossDcRelayMode"/> is
/// <see cref="RelayMode.Disabled"/> - <see cref="RelayIdentityService"/> (which is what actually
/// generates/persists the identity keypair) is only ever constructed the first time it's actually
/// needed, not eagerly in this type's own constructor, per the explicit "nothing happens at all while
/// the feature is off" requirement.</para>
///
/// <para><see cref="Reconcile"/> is the only entry point - call it once at plugin startup and again
/// every time Settings &gt; Cross-DC changes anything (mode, self-hosted URL). It figures out on its own
/// whether that means connecting, disconnecting, or switching servers, and is a no-op if the live
/// connection already matches what the config now says.</para>
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

    private RelayIdentityService? identity;
    private CancellationTokenSource? runCts;
    private Task? runTask;
    private string? connectedUrl;

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

    /// <summary>Placeholder for the next increment (pairing/messaging) - for now, every incoming frame
    /// is just logged by type, proving the receive loop actually works end to end without yet having
    /// anywhere real to route frames to.</summary>
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

        log.Debug("TomeScrollChat: cross-DC relay frame received (type={Type})", type ?? "(none)");
    }

    private void SetDisconnected(string? error)
    {
        lock (gate)
        {
            runTask = null;
            connectedUrl = null;
        }

        IsConnected = false;
        UserId = null;
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
    }

    public void Dispose()
    {
        lock (gate)
            StopLocked();

        identity?.Dispose();
    }
}
