using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using NSec.Cryptography;

namespace TomeScrollChat.Services.CrossDc;

/// <summary>
/// This installation's cross-DC relay identity - an Ed25519 keypair (the identity itself; a relay
/// derives the user ID from its public key, see TomeScrollRelay's own <c>RelayIdentity</c>) and an
/// X25519 keypair for per-contact message encryption (see <see cref="EncryptFor"/>/
/// <see cref="DecryptFrom"/>): each contact's messages use a key derived via X25519 ECDH + HKDF-SHA256
/// from this identity's private key and that contact's public key (announced once per pairing - see
/// <see cref="SetPeerPublicKey"/>), then XChaCha20-Poly1305 for the actual authenticated encryption.
/// Also remembers which relay server URLs this identity has successfully claimed admin rights on (see
/// <see cref="IsKnownAdmin"/>/<see cref="MarkAdmin"/>) - the relay itself grants admin durably (survives
/// its own restarts, see TomeScrollRelay's <c>AdminRegistry</c>), so without this the player would need
/// to re-claim with a fresh bootstrap key on every single reconnect, even though the relay never forgot.
///
/// <para>All of this is generated once and persisted to <c>cross-dc-identity.json</c> under the plugin's
/// config directory - deliberately its own file, not a field on <see cref="Configuration"/>: the private
/// keys have no business going through the same path as human-editable settings (same reasoning
/// <see cref="Configuration.GeminiApiKey"/>'s doc comment gives for being excluded from
/// <c>ResetToDefaults</c>, taken one step further here - it isn't even in that file at all), and the
/// admin-URLs/contacts/peer-keys all travel with the identity they belong to for the same reason
/// pairings/groups don't carry over between relay servers - it's per (identity, server), not a global
/// preference. Same reasoning covers the paired-contact list (see <see cref="GetContacts"/>/
/// <see cref="AddContact"/>): the relay has no "list my pairs" query, but this client is always present
/// for every pairing it's ever part of (either directly redeeming a code, or receiving the resulting
/// notification), so tracking it locally as pairings happen is enough - no new relay query needed for
/// this. Instantiated lazily by <see cref="CrossDcRelayService"/> only once
/// <see cref="Configuration.CrossDcRelayMode"/> is actually non-Disabled, per the explicit "nothing
/// happens at all while the feature is off" requirement - not even key generation.</para>
/// </summary>
public sealed class RelayIdentityService : IDisposable
{
    private static readonly SignatureAlgorithm SigningAlgorithm = SignatureAlgorithm.Ed25519;
    private static readonly KeyAgreementAlgorithm EncryptionAlgorithm = KeyAgreementAlgorithm.X25519;
    private static readonly KeyDerivationAlgorithm ChatKeyDerivation = KeyDerivationAlgorithm.HkdfSha256;
    private static readonly AeadAlgorithm ChatCipher = AeadAlgorithm.XChaCha20Poly1305;

    // Fixed, public, non-secret inputs to HKDF - not a secret in their own right, just domain-separating
    // this specific derivation (chat message keys) from any other thing that might ever derive from the
    // same raw X25519 shared secret.
    private static readonly byte[] ChatKeySalt = Encoding.UTF8.GetBytes("TomeScrollChat/cross-dc-chat/v1");
    private static readonly byte[] ChatKeyInfo = Encoding.UTF8.GetBytes("chat-key");

    // KeyCreationParameters/SharedSecretCreationParameters are ref structs (NSec's choice, not this
    // codebase's) - can't be static fields, only ever fresh local/inline values.
    private static KeyCreationParameters ExportableKeyParams => new() { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };
    private static SharedSecretCreationParameters ExportableSecretParams => new() { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };

    private readonly string path;
    private readonly IPluginLog log;
    private readonly Key signingKey;
    private readonly Key encryptionKey;
    private readonly HashSet<string> adminUrls;
    private readonly Dictionary<string, HashSet<string>> contactsByUrl;
    private readonly Dictionary<string, Dictionary<string, string>> peerKeysByUrl;

    public RelayIdentityService(string configDirectory, IPluginLog log)
    {
        this.log = log;
        Directory.CreateDirectory(configDirectory);
        path = Path.Combine(configDirectory, "cross-dc-identity.json");

        var loaded = TryLoad(path, log);
        if (loaded is { } state)
        {
            signingKey = state.Signing;
            encryptionKey = state.Encryption;
            adminUrls = state.AdminUrls;
            contactsByUrl = state.ContactsByUrl;
            peerKeysByUrl = state.PeerKeysByUrl;
            return;
        }

        signingKey = Key.Create(SigningAlgorithm, ExportableKeyParams);
        encryptionKey = Key.Create(EncryptionAlgorithm, ExportableKeyParams);
        adminUrls = new HashSet<string>();
        contactsByUrl = new Dictionary<string, HashSet<string>>();
        peerKeysByUrl = new Dictionary<string, Dictionary<string, string>>();
        Save();
    }

    /// <summary>Standard (not URL-safe) base64 Ed25519 public key - matches
    /// <c>Convert.FromBase64String</c> on the relay's side of the handshake. This is the identity
    /// itself; the relay derives this installation's user ID by hashing it.</summary>
    public string SigningPublicKeyBase64 => Convert.ToBase64String(signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey));

    /// <summary>Standard base64 X25519 public key - announced to a contact once per pairing (see
    /// <see cref="CrossDcRelayService"/>'s <c>keyAnnounce</c> handling) so they can derive the same
    /// per-contact chat key this identity does.</summary>
    public string EncryptionPublicKeyBase64 => Convert.ToBase64String(encryptionKey.PublicKey.Export(KeyBlobFormat.RawPublicKey));

    /// <summary>Signs arbitrary bytes with the Ed25519 identity key - used to answer the relay's
    /// connect-time nonce challenge (see <see cref="CrossDcRelayService"/>).</summary>
    public byte[] Sign(byte[] data) => SigningAlgorithm.Sign(signingKey, data);

    /// <summary>True if a previous <see cref="MarkAdmin"/> already recorded a successful admin claim on
    /// this exact relay URL - lets <see cref="CrossDcRelayService"/> skip straight to
    /// <c>IsAdmin = true</c> on connect instead of requiring the bootstrap key again.</summary>
    public bool IsKnownAdmin(string url) => adminUrls.Contains(url);

    /// <summary>Records a successful <c>claimAdmin</c> against <paramref name="url"/>, persisted
    /// immediately so it survives the next reload/reconnect. A no-op (no needless disk write) if this
    /// URL was already recorded.</summary>
    public void MarkAdmin(string url)
    {
        if (adminUrls.Add(url))
            Save();
    }

    /// <summary>Clears the locally-cached "I'm admin on this URL" fact, so the next connection no longer
    /// assumes it and a fresh <c>claimAdmin</c> is needed. For when the two sides genuinely disagree -
    /// e.g. the relay's own admin record was wiped independently of this client (a Redis flush on the
    /// relay side, not anything this client did) - rather than the client just being stubbornly wrong.
    /// A no-op if this URL wasn't recorded as admin in the first place.</summary>
    public void ForgetAdmin(string url)
    {
        if (adminUrls.Remove(url))
            Save();
    }

    /// <summary>Every userId this identity has ever successfully paired with on <paramref name="url"/> -
    /// empty if none yet. Never includes anyone unpaired later (there's no unpair feature yet to test
    /// that against, but <see cref="AddContact"/> is the only way anything gets added here, so removal
    /// support can be added later without a migration).</summary>
    public IReadOnlyCollection<string> GetContacts(string url) =>
        contactsByUrl.TryGetValue(url, out var contacts) ? contacts : Array.Empty<string>();

    /// <summary>Records a successful pairing with <paramref name="userId"/> on <paramref name="url"/>,
    /// persisted immediately. A no-op if already recorded.</summary>
    public void AddContact(string url, string userId)
    {
        if (!contactsByUrl.TryGetValue(url, out var contacts))
            contactsByUrl[url] = contacts = new HashSet<string>();

        if (contacts.Add(userId))
            Save();
    }

    /// <summary>The X25519 public key <paramref name="contactUserId"/> announced on <paramref name="url"/>
    /// (see <see cref="SetPeerPublicKey"/>), or null if no announcement has been received yet - callers
    /// can't encrypt/decrypt for this contact until this returns non-null.</summary>
    public string? GetPeerPublicKey(string url, string contactUserId) =>
        peerKeysByUrl.TryGetValue(url, out var keys) && keys.TryGetValue(contactUserId, out var key) ? key : null;

    /// <summary>Records the X25519 public key a contact announced on <paramref name="url"/>, persisted
    /// immediately. Overwrites any previous value for that contact (a contact re-announcing, e.g. after
    /// generating a new identity of their own, should take effect rather than being ignored).</summary>
    public void SetPeerPublicKey(string url, string contactUserId, string publicKeyBase64)
    {
        if (!peerKeysByUrl.TryGetValue(url, out var keys))
            peerKeysByUrl[url] = keys = new Dictionary<string, string>();

        if (keys.TryGetValue(contactUserId, out var existing) && existing == publicKeyBase64)
            return;

        keys[contactUserId] = publicKeyBase64;
        Save();
    }

    /// <summary>Encrypts <paramref name="plaintext"/> for <paramref name="peerUserId"/> (whose X25519
    /// public key is <paramref name="peerPublicKeyBase64"/> - see <see cref="GetPeerPublicKey"/>), binding
    /// <paramref name="fromUserId"/>/<paramref name="peerUserId"/> as associated data so a ciphertext
    /// can't silently be replayed as if it came from/to someone else. Null if the peer's public key is
    /// malformed or the underlying X25519 agreement fails - callers already require a peer key to exist
    /// before calling this, so either would mean corrupted state, not a normal "not ready yet" case.</summary>
    public byte[]? EncryptFor(string peerUserId, string peerPublicKeyBase64, string fromUserId, byte[] nonce, byte[] plaintext)
    {
        try
        {
            using var chatKey = DeriveChatKey(peerPublicKeyBase64);
            if (chatKey == null)
                return null;

            return ChatCipher.Encrypt(chatKey, nonce, BuildAad(fromUserId, peerUserId), plaintext);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to encrypt a cross-DC message for {PeerUserId}", peerUserId);
            return null;
        }
    }

    /// <summary>Decrypts a message from <paramref name="peerUserId"/> (whose X25519 public key is
    /// <paramref name="peerPublicKeyBase64"/>) addressed to <paramref name="toUserId"/> (this identity's
    /// own user ID). Null if decryption/authentication fails - a tampered ciphertext, wrong AAD (e.g. it
    /// was actually addressed to someone else), or a stale/wrong peer key.</summary>
    public byte[]? DecryptFrom(string peerUserId, string peerPublicKeyBase64, string toUserId, byte[] nonce, byte[] ciphertext)
    {
        try
        {
            using var chatKey = DeriveChatKey(peerPublicKeyBase64);
            if (chatKey == null)
                return null;

            return ChatCipher.Decrypt(chatKey, nonce, BuildAad(peerUserId, toUserId), ciphertext);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to decrypt a cross-DC message from {PeerUserId}", peerUserId);
            return null;
        }
    }

    /// <summary>"{from}->{to}" - the same two user IDs in the same order regardless of which side (sender
    /// or receiver) is doing the computing, so both always land on the identical associated-data bytes.</summary>
    private static byte[] BuildAad(string fromUserId, string toUserId) => Encoding.UTF8.GetBytes($"{fromUserId}->{toUserId}");

    /// <summary>X25519 ECDH between this identity's private key and the peer's public key, then
    /// HKDF-SHA256 into an XChaCha20-Poly1305-sized symmetric key. Caller owns disposing the result.</summary>
    private Key? DeriveChatKey(string peerPublicKeyBase64)
    {
        var peerPublicKeyBytes = Convert.FromBase64String(peerPublicKeyBase64);
        var peerPublicKey = PublicKey.Import(EncryptionAlgorithm, peerPublicKeyBytes, KeyBlobFormat.RawPublicKey);

        using var shared = EncryptionAlgorithm.Agree(encryptionKey, peerPublicKey, ExportableSecretParams);
        if (shared == null)
            return null;

        return ChatKeyDerivation.DeriveKey(shared, ChatKeySalt, ChatKeyInfo, ChatCipher, ExportableKeyParams);
    }

    private static (Key Signing, Key Encryption, HashSet<string> AdminUrls, Dictionary<string, HashSet<string>> ContactsByUrl, Dictionary<string, Dictionary<string, string>> PeerKeysByUrl)? TryLoad(string path, IPluginLog log)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var stored = JsonSerializer.Deserialize<StoredIdentity>(File.ReadAllText(path));
            if (stored is not { SigningKeySeed.Length: > 0, EncryptionKeySeed.Length: > 0 })
                return null;

            var signing = Key.Import(SigningAlgorithm, Convert.FromBase64String(stored.SigningKeySeed), KeyBlobFormat.RawPrivateKey, ExportableKeyParams);
            var encryption = Key.Import(EncryptionAlgorithm, Convert.FromBase64String(stored.EncryptionKeySeed), KeyBlobFormat.RawPrivateKey, ExportableKeyParams);
            // AdminUrls/ContactsByUrl/PeerKeysByUrl didn't exist in identity files written before those
            // fields were added - null here just means "none recorded yet", not a corrupt/unreadable file.
            var adminUrls = new HashSet<string>(stored.AdminUrls ?? Enumerable.Empty<string>());
            var contactsByUrl = (stored.ContactsByUrl ?? new Dictionary<string, List<string>>())
                .ToDictionary(kvp => kvp.Key, kvp => new HashSet<string>(kvp.Value));
            var peerKeysByUrl = stored.PeerKeysByUrl ?? new Dictionary<string, Dictionary<string, string>>();
            return (signing, encryption, adminUrls, contactsByUrl, peerKeysByUrl);
        }
        catch (Exception ex)
        {
            // A fresh identity means a fresh user ID - every existing pairing/group on whichever relay
            // was in use is orphaned (the relay has no record of this "new" ID). Rare in practice (would
            // need the file to go missing/corrupt), but worth a warning that says so rather than just
            // "failed to load," since the symptom the player would otherwise see is confusing ("all my
            // contacts are gone") with no obvious cause.
            log.Warning(ex, "TomeScrollChat: failed to load the cross-DC identity - generating a new one (any existing relay pairings/groups will need to be redone)");
            return null;
        }
    }

    private void Save()
    {
        try
        {
            var stored = new StoredIdentity(
                Convert.ToBase64String(signingKey.Export(KeyBlobFormat.RawPrivateKey)),
                Convert.ToBase64String(encryptionKey.Export(KeyBlobFormat.RawPrivateKey)),
                adminUrls.ToList(),
                contactsByUrl.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToList()),
                peerKeysByUrl);
            File.WriteAllText(path, JsonSerializer.Serialize(stored));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to persist the cross-DC identity");
        }
    }

    public void Dispose()
    {
        signingKey.Dispose();
        encryptionKey.Dispose();
    }

    private sealed record StoredIdentity(
        string SigningKeySeed,
        string EncryptionKeySeed,
        List<string>? AdminUrls = null,
        Dictionary<string, List<string>>? ContactsByUrl = null,
        Dictionary<string, Dictionary<string, string>>? PeerKeysByUrl = null);
}
