using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TomeScrollChat.Services.CrossDc;

/// <summary>JSON wire format shared with TomeScrollRelay's own <c>WebSocketJson</c> - camelCase,
/// case-insensitive on read. Matching the relay's <c>JsonSerializerDefaults.Web</c> convention
/// deliberately, not left at System.Text.Json's PascalCase-sensitive default: a client/server casing
/// mismatch was a real bug already hit once on the relay side of this project (see its own
/// <c>WebSocketJson.cs</c> history) - applying the same fix here proactively instead of rediscovering it
/// from this end.</summary>
internal static class RelayProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

internal sealed record RelayTypeOnly(string? Type);
internal sealed record RelayChallengeMessage(string? Type, string? Nonce);
internal sealed record RelayHelloMessage(string PublicKey, string Signature);
internal sealed record RelayConnectedMessage(string? Type, string? Id);
internal sealed record RelayErrorMessage(string? Type, string? Reason);

internal sealed record RelayClaimAdminRequest(string Type, string Key);

internal sealed record RelayGetLogsRequest(string Type, int? Lines);
internal sealed record RelayLogsMessage(string? Type, string[]? Lines);

internal sealed record RelayGetStatsRequest(string Type);
internal sealed record RelayStatsMessage(string? Type, int? ConnectedClients);

internal sealed record RelayCreateInviteRequest(string Type);
internal sealed record RelayInviteMessage(string? Type, string? Code);

internal sealed record RelayRedeemInviteRequest(string Type, string Code);
internal sealed record RelayPairedMessage(string? Type, string? With);

internal sealed record RelaySendRequest(string Type, string To, string Payload);
internal sealed record RelayMessageEnvelope(string? Type, string? From, string? Payload);

/// <summary>Client-level envelope carried as the opaque <c>payload</c> of a relay <c>send</c>/
/// <c>message</c> - the relay never looks inside this, it's purely between the two clients. Two kinds:
/// <c>keyAnnounce</c> (announce this identity's X25519 public key to a newly-paired contact) and
/// <c>chat</c> (an encrypted message). Both share the same "type" field so the receiver can tell them
/// apart the same way the outer relay protocol does.</summary>
internal sealed record ChatKeyAnnounceEnvelope(string Type, string PublicKey);
internal sealed record ChatMessageEnvelope(string Type, string Nonce, string Ciphertext);

/// <summary>Send/receive helpers for JSON text frames over a <see cref="WebSocket"/> - the client-side
/// mirror of the relay's own <c>WebSocketJson</c>, so both ends agree on framing as well as casing.</summary>
internal static class RelaySocketIo
{
    // Generous relative to the relay's own 8 KiB *inbound* cap - this is only ever receiving what the
    // relay itself already bounded on its side, this ceiling is just a sanity backstop against buffering
    // an unbounded amount of memory if something ever went wrong. Sized well above the relay's own
    // getLogs cap (500 lines) - a full log dump is comfortably the largest single frame this client
    // ever receives.
    private const int MaxMessageBytes = 256 * 1024;

    public static Task SendAsync<T>(ClientWebSocket socket, T message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, RelayProtocolJson.Options);
        return socket.SendAsync(json, WebSocketMessageType.Text, true, cancellationToken);
    }

    /// <summary>Null on a closed/faulted connection or an oversized frame - the caller treats either the
    /// same way, as "the connection is over."</summary>
    public static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var stream = new MemoryStream();

        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            stream.Write(buffer, 0, result.Count);
            if (stream.Length > MaxMessageBytes)
                return null;

            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
