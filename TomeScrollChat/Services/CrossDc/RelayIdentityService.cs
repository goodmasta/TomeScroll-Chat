using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;
using NSec.Cryptography;

namespace TomeScrollChat.Services.CrossDc;

/// <summary>
/// This installation's cross-DC relay identity - an Ed25519 keypair (the identity itself; a relay
/// derives the user ID from its public key, see TomeScrollRelay's own <c>RelayIdentity</c>) and an
/// X25519 keypair (for the message-encryption key exchange, unused until a later increment builds that
/// layer - generated and persisted now so it doesn't need its own migration then). Also remembers which
/// relay server URLs this identity has successfully claimed admin rights on (see
/// <see cref="IsKnownAdmin"/>/<see cref="MarkAdmin"/>) - the relay itself grants admin durably (survives
/// its own restarts, see TomeScrollRelay's <c>AdminRegistry</c>), so without this the player would need
/// to re-claim with a fresh bootstrap key on every single reconnect, even though the relay never forgot.
///
/// <para>All of this is generated once and persisted to <c>cross-dc-identity.json</c> under the plugin's
/// config directory - deliberately its own file, not a field on <see cref="Configuration"/>: the private
/// keys have no business going through the same path as human-editable settings (same reasoning
/// <see cref="Configuration.GeminiApiKey"/>'s doc comment gives for being excluded from
/// <c>ResetToDefaults</c>, taken one step further here - it isn't even in that file at all), and the
/// admin-URLs list travels with the identity it belongs to for the same reason pairings/groups don't
/// carry over between relay servers - it's per (identity, server), not a global preference. Instantiated
/// lazily by <see cref="CrossDcRelayService"/> only once <see cref="Configuration.CrossDcRelayMode"/> is
/// actually non-Disabled, per the explicit "nothing happens at all while the feature is off"
/// requirement - not even key generation.</para>
/// </summary>
public sealed class RelayIdentityService : IDisposable
{
    private static readonly SignatureAlgorithm SigningAlgorithm = SignatureAlgorithm.Ed25519;
    private static readonly KeyAgreementAlgorithm EncryptionAlgorithm = KeyAgreementAlgorithm.X25519;

    // KeyCreationParameters is a ref struct (NSec's choice, not this codebase's) - it can't be a static
    // field, only ever a fresh local/inline value.
    private static KeyCreationParameters ExportableKeyParams => new() { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };

    private readonly string path;
    private readonly IPluginLog log;
    private readonly Key signingKey;
    private readonly Key encryptionKey;
    private readonly HashSet<string> adminUrls;

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
            return;
        }

        signingKey = Key.Create(SigningAlgorithm, ExportableKeyParams);
        encryptionKey = Key.Create(EncryptionAlgorithm, ExportableKeyParams);
        adminUrls = new HashSet<string>();
        Save();
    }

    /// <summary>Standard (not URL-safe) base64 Ed25519 public key - matches
    /// <c>Convert.FromBase64String</c> on the relay's side of the handshake. This is the identity
    /// itself; the relay derives this installation's user ID by hashing it.</summary>
    public string SigningPublicKeyBase64 => Convert.ToBase64String(signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey));

    /// <summary>Standard base64 X25519 public key - for the message-encryption key exchange, not yet
    /// consumed by anything (see the type doc comment).</summary>
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

    private static (Key Signing, Key Encryption, HashSet<string> AdminUrls)? TryLoad(string path, IPluginLog log)
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
            // AdminUrls didn't exist in identity files written before this field was added - null here
            // just means "none recorded yet", not a corrupt/unreadable file.
            var adminUrls = new HashSet<string>(stored.AdminUrls ?? Enumerable.Empty<string>());
            return (signing, encryption, adminUrls);
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
                adminUrls.ToList());
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

    private sealed record StoredIdentity(string SigningKeySeed, string EncryptionKeySeed, List<string>? AdminUrls = null);
}
