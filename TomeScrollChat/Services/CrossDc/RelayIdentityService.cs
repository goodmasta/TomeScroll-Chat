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
    private readonly Dictionary<string, Dictionary<string, string>> peerNamesByUrl;
    private readonly Dictionary<string, Dictionary<string, GroupState>> groupsByUrl;

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
            peerNamesByUrl = state.PeerNamesByUrl;
            groupsByUrl = state.GroupsByUrl;
            return;
        }

        signingKey = Key.Create(SigningAlgorithm, ExportableKeyParams);
        encryptionKey = Key.Create(EncryptionAlgorithm, ExportableKeyParams);
        adminUrls = new HashSet<string>();
        contactsByUrl = new Dictionary<string, HashSet<string>>();
        peerKeysByUrl = new Dictionary<string, Dictionary<string, string>>();
        peerNamesByUrl = new Dictionary<string, Dictionary<string, string>>();
        groupsByUrl = new Dictionary<string, Dictionary<string, GroupState>>();
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

    /// <summary>The character name/world <paramref name="contactUserId"/> announced on <paramref name="url"/>
    /// (see <see cref="SetPeerDisplayName"/>), or null if none has arrived yet - callers fall back to the
    /// raw relay userId in that case.</summary>
    public string? GetPeerDisplayName(string url, string contactUserId) =>
        peerNamesByUrl.TryGetValue(url, out var names) && names.TryGetValue(contactUserId, out var name) ? name : null;

    /// <summary>Records the display name a contact announced on <paramref name="url"/> (piggybacked on
    /// the same <c>keyAnnounce</c> as their X25519 public key - see <see cref="CrossDcRelayService"/>),
    /// persisted immediately. Overwrites any previous value - a character rename, or the contact simply
    /// logging in on a different character, should take effect.</summary>
    public void SetPeerDisplayName(string url, string contactUserId, string displayName)
    {
        if (!peerNamesByUrl.TryGetValue(url, out var names))
            peerNamesByUrl[url] = names = new Dictionary<string, string>();

        if (names.TryGetValue(contactUserId, out var existing) && existing == displayName)
            return;

        names[contactUserId] = displayName;
        Save();
    }

    /// <summary>Every group this identity currently belongs to on <paramref name="url"/>, keyed by group
    /// id - empty if none. Membership here just means "this client still thinks it's in the group";
    /// <see cref="RemoveGroup"/> is how <see cref="CrossDcRelayService"/> clears an entry once it's told
    /// otherwise (kicked, left, or the group was deleted).</summary>
    public IReadOnlyDictionary<string, GroupState> GetGroups(string url) =>
        groupsByUrl.TryGetValue(url, out var groups) ? groups : EmptyGroups;

    public GroupState? GetGroup(string url, string groupId) =>
        groupsByUrl.TryGetValue(url, out var groups) && groups.TryGetValue(groupId, out var group) ? group : null;

    /// <summary>Inserts or wholesale-replaces <paramref name="state"/> for <paramref name="groupId"/> on
    /// <paramref name="url"/>, persisted immediately. Callers that instead mutate a <see cref="GroupState"/>
    /// already obtained from <see cref="GetGroup"/> in place (it's a live reference into the same backing
    /// dictionary, not a copy) should call <see cref="PersistGroups"/> afterward instead of this.</summary>
    public void UpsertGroup(string url, string groupId, GroupState state)
    {
        if (!groupsByUrl.TryGetValue(url, out var groups))
            groupsByUrl[url] = groups = new Dictionary<string, GroupState>();

        groups[groupId] = state;
        Save();
    }

    /// <summary>Drops <paramref name="groupId"/> entirely (kicked, left, or the owner deleted it by
    /// leaving as its last member) - a no-op if it wasn't tracked in the first place.</summary>
    public void RemoveGroup(string url, string groupId)
    {
        if (groupsByUrl.TryGetValue(url, out var groups) && groups.Remove(groupId))
            Save();
    }

    /// <summary>Persists after mutating a <see cref="GroupState"/> obtained from <see cref="GetGroup"/>
    /// directly (member list, roles, key material) - see <see cref="UpsertGroup"/>'s doc comment.</summary>
    public void PersistGroups() => Save();

    private static readonly Dictionary<string, GroupState> EmptyGroups = new();

    /// <summary>A fresh random 32-byte symmetric key, sized for <see cref="AeadAlgorithm.XChaCha20Poly1305"/> -
    /// what a group's owner generates once at creation, and whoever kicks/whoever reacts to a member
    /// leaving generates again for the next epoch (see <see cref="CrossDcRelayService"/>'s rekey flow).</summary>
    public static byte[] GenerateGroupKey() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    // Fixed, public HKDF inputs for the group-key-sealing derivation specifically - domain-separated
    // from ChatKeySalt/ChatKeyInfo (1:1 chat) and from the group *message* key (which isn't HKDF-derived
    // at all, see EncryptGroupMessage/DecryptGroupMessage - the group key itself already *is* the AEAD
    // key, sealing is the only place HKDF is involved for groups).
    private static readonly byte[] GroupSealSalt = Encoding.UTF8.GetBytes("TomeScrollChat/cross-dc-group/seal/v1");
    private static readonly byte[] GroupSealInfo = Encoding.UTF8.GetBytes("group-key-seal");
    private const int X25519PublicKeyLength = 32;
    private const int SealNonceLength = 24;

    /// <summary>"Seals" <paramref name="groupKey"/> for one specific member as an opaque blob the relay
    /// stores/hands back without understanding (<c>SealedKey</c> in TomeScrollRelay's own
    /// <c>GroupProtocolMessages.cs</c>) - a NaCl-style anonymous sealed box: a fresh ephemeral X25519
    /// keypair is ECDH'd against the recipient's public key, HKDF'd into a wrapping key, and used to
    /// AEAD-encrypt <paramref name="groupKey"/>; the ephemeral public key travels alongside the
    /// ciphertext so unsealing only ever needs the recipient's own private key, never the sealer's
    /// identity. That's deliberate: the relay's own protocol carries no "sealed by" field at all (any
    /// owner or moderator can perform a seal - see <see cref="CrossDcRelayService"/>'s rekey/admit flow),
    /// so the recipient has no way to know who sealed it even if they wanted to - and doesn't need to,
    /// since the ephemeral key makes each seal self-contained and independently verifiable via the AEAD
    /// tag regardless of who produced it. Null if the recipient's public key is malformed.</summary>
    public string? SealGroupKeyFor(string recipientPublicKeyBase64, string groupId, long epoch, string recipientUserId, byte[] groupKey)
    {
        try
        {
            using var ephemeral = Key.Create(EncryptionAlgorithm, ExportableKeyParams);
            var recipientPublicKey = PublicKey.Import(EncryptionAlgorithm, Convert.FromBase64String(recipientPublicKeyBase64), KeyBlobFormat.RawPublicKey);

            using var shared = EncryptionAlgorithm.Agree(ephemeral, recipientPublicKey, ExportableSecretParams);
            if (shared == null)
                return null;

            using var wrapKey = ChatKeyDerivation.DeriveKey(shared, GroupSealSalt, GroupSealInfo, ChatCipher, ExportableKeyParams);
            var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(SealNonceLength);
            var ciphertext = ChatCipher.Encrypt(wrapKey, nonce, BuildGroupAad(groupId, epoch, recipientUserId), groupKey);
            var ephemeralPublicBytes = ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey);

            var packed = new byte[ephemeralPublicBytes.Length + nonce.Length + ciphertext.Length];
            Buffer.BlockCopy(ephemeralPublicBytes, 0, packed, 0, ephemeralPublicBytes.Length);
            Buffer.BlockCopy(nonce, 0, packed, ephemeralPublicBytes.Length, nonce.Length);
            Buffer.BlockCopy(ciphertext, 0, packed, ephemeralPublicBytes.Length + nonce.Length, ciphertext.Length);
            return Convert.ToBase64String(packed);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to seal a cross-DC group key for {RecipientUserId}", recipientUserId);
            return null;
        }
    }

    /// <summary>Unseals this identity's own copy of a group key (see <see cref="SealGroupKeyFor"/>) using
    /// its private X25519 key against the ephemeral public key embedded in <paramref name="sealedBase64"/>.
    /// Null if malformed, or if authentication fails (wrong groupId/epoch/recipient, tampered, or sealed
    /// for someone else's public key entirely).</summary>
    public byte[]? UnsealGroupKey(string sealedBase64, string groupId, long epoch, string myUserId)
    {
        try
        {
            var packed = Convert.FromBase64String(sealedBase64);
            if (packed.Length <= X25519PublicKeyLength + SealNonceLength)
                return null;

            var ephemeralPublicBytes = packed.AsSpan(0, X25519PublicKeyLength).ToArray();
            var nonce = packed.AsSpan(X25519PublicKeyLength, SealNonceLength).ToArray();
            var ciphertext = packed.AsSpan(X25519PublicKeyLength + SealNonceLength).ToArray();

            var ephemeralPublicKey = PublicKey.Import(EncryptionAlgorithm, ephemeralPublicBytes, KeyBlobFormat.RawPublicKey);
            using var shared = EncryptionAlgorithm.Agree(encryptionKey, ephemeralPublicKey, ExportableSecretParams);
            if (shared == null)
                return null;

            using var wrapKey = ChatKeyDerivation.DeriveKey(shared, GroupSealSalt, GroupSealInfo, ChatCipher, ExportableKeyParams);
            return ChatCipher.Decrypt(wrapKey, nonce, BuildGroupAad(groupId, epoch, myUserId), ciphertext);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to unseal a cross-DC group key for group {GroupId}", groupId);
            return null;
        }
    }

    /// <summary>Encrypts a group chat message directly with the group's own raw symmetric key (no HKDF -
    /// unlike 1:1 chat, this key doesn't come from an ECDH agreement, it's a random value generated once
    /// and distributed via <see cref="SealGroupKeyFor"/>, so it's already exactly the right size/shape for
    /// <see cref="AeadAlgorithm.XChaCha20Poly1305"/> to use as-is). Binds group/epoch/sender as associated
    /// data so a ciphertext can't be replayed into a different group, under a different epoch's key, or as
    /// if sent by someone else.</summary>
    public byte[]? EncryptGroupMessage(byte[] groupKey, string groupId, long epoch, string fromUserId, byte[] nonce, byte[] plaintext)
    {
        try
        {
            using var key = Key.Import(ChatCipher, groupKey, KeyBlobFormat.RawSymmetricKey, ExportableKeyParams);
            return ChatCipher.Encrypt(key, nonce, BuildGroupAad(groupId, epoch, fromUserId), plaintext);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to encrypt a cross-DC group message for {GroupId}", groupId);
            return null;
        }
    }

    /// <summary>Decrypts a group chat message - see <see cref="EncryptGroupMessage"/>. Null (message
    /// dropped) if authentication fails, most commonly because the group's key has since rotated (a kick
    /// or someone leaving - see <see cref="CrossDcRelayService"/>) past the epoch this message was
    /// encrypted under; this client only ever keeps the *current* epoch's key, not a rotation history.</summary>
    public byte[]? DecryptGroupMessage(byte[] groupKey, string groupId, long epoch, string fromUserId, byte[] nonce, byte[] ciphertext)
    {
        try
        {
            using var key = Key.Import(ChatCipher, groupKey, KeyBlobFormat.RawSymmetricKey, ExportableKeyParams);
            return ChatCipher.Decrypt(key, nonce, BuildGroupAad(groupId, epoch, fromUserId), ciphertext);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to decrypt a cross-DC group message from {FromUserId}", fromUserId);
            return null;
        }
    }

    private static byte[] BuildGroupAad(string groupId, long epoch, string userId) => Encoding.UTF8.GetBytes($"{groupId}:{epoch}:{userId}");

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

    private static (Key Signing, Key Encryption, HashSet<string> AdminUrls, Dictionary<string, HashSet<string>> ContactsByUrl, Dictionary<string, Dictionary<string, string>> PeerKeysByUrl, Dictionary<string, Dictionary<string, string>> PeerNamesByUrl, Dictionary<string, Dictionary<string, GroupState>> GroupsByUrl)? TryLoad(string path, IPluginLog log)
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
            // AdminUrls/ContactsByUrl/PeerKeysByUrl/PeerNamesByUrl/GroupsByUrl didn't exist in identity
            // files written before those fields were added - null here just means "none recorded yet",
            // not a corrupt/unreadable file.
            var adminUrls = new HashSet<string>(stored.AdminUrls ?? Enumerable.Empty<string>());
            var contactsByUrl = (stored.ContactsByUrl ?? new Dictionary<string, List<string>>())
                .ToDictionary(kvp => kvp.Key, kvp => new HashSet<string>(kvp.Value));
            var peerKeysByUrl = stored.PeerKeysByUrl ?? new Dictionary<string, Dictionary<string, string>>();
            var peerNamesByUrl = stored.PeerNamesByUrl ?? new Dictionary<string, Dictionary<string, string>>();
            var groupsByUrl = stored.GroupsByUrl ?? new Dictionary<string, Dictionary<string, GroupState>>();
            return (signing, encryption, adminUrls, contactsByUrl, peerKeysByUrl, peerNamesByUrl, groupsByUrl);
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
                peerKeysByUrl,
                peerNamesByUrl,
                groupsByUrl);
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
        Dictionary<string, Dictionary<string, string>>? PeerKeysByUrl = null,
        Dictionary<string, Dictionary<string, string>>? PeerNamesByUrl = null,
        Dictionary<string, Dictionary<string, GroupState>>? GroupsByUrl = null);
}

/// <summary>One group ("relay linkshell") this identity belongs to on some relay URL - the client-side
/// mirror of what TomeScrollRelay's own <c>GroupRegistry</c> tracks server-side, plus the one thing the
/// relay never sees: <see cref="KeyBase64"/>/<see cref="Epoch"/>, this member's current plaintext copy of
/// the group's symmetric chat key (unsealed once via <see cref="RelayIdentityService.UnsealGroupKey"/>,
/// then just kept here so every subsequent message doesn't need a fresh unseal). A mutable class, not a
/// record - <see cref="CrossDcRelayService"/> mutates a live instance obtained from
/// <see cref="RelayIdentityService.GetGroup"/> in place (member list changes, role changes, key rotation)
/// rather than reconstructing and replacing the whole thing on every single field update.</summary>
public sealed class GroupState
{
    public string Name { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public List<string> Members { get; set; } = new();
    public List<string> ModeratorIds { get; set; } = new();

    /// <summary>This member's own current plaintext group key, base64 - null until unsealed at least
    /// once (e.g. right after joining, before an owner/moderator has gotten around to sealing one for
    /// this member - see <see cref="CrossDcRelayService"/>'s <c>groupMemberJoined</c> handling).</summary>
    public string? KeyBase64 { get; set; }

    /// <summary>Which rotation <see cref="KeyBase64"/> corresponds to - a message encrypted under a
    /// different epoch than this can't be decrypted with it (see
    /// <see cref="RelayIdentityService.DecryptGroupMessage"/>), most commonly because a kick/leave since
    /// then rotated the key forward and this member hasn't received the new one yet.</summary>
    public long Epoch { get; set; }
}
