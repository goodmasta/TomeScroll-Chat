using System;
using System.IO;
using System.Text.Json;
using Dalamud.Plugin.Services;
using NSec.Cryptography;

namespace TomeScrollChat.Services.CrossDc;

/// <summary>
/// This installation's cross-DC relay identity - an Ed25519 keypair (the identity itself; a relay
/// derives the user ID from its public key, see TomeScrollRelay's own <c>RelayIdentity</c>) and an
/// X25519 keypair (for the message-encryption key exchange, unused until a later increment builds that
/// layer - generated and persisted now so it doesn't need its own migration then).
///
/// <para>Both key pairs are generated once and persisted to <c>cross-dc-identity.json</c> under the
/// plugin's config directory - deliberately its own file, not a field on <see cref="Configuration"/>:
/// this is a private key, not a preference, so it has no business going through the same path as
/// human-editable settings (same reasoning <see cref="Configuration.GeminiApiKey"/>'s doc comment gives
/// for being excluded from <c>ResetToDefaults</c>, taken one step further here - it isn't even in that
/// file at all). Instantiated lazily by <see cref="CrossDcRelayService"/> only once
/// <see cref="Configuration.CrossDcRelayMode"/> is actually non-Disabled, per the explicit "nothing
/// happens at all while the feature is off" requirement - not even key generation.</para>
/// </summary>
public sealed class RelayIdentityService : IDisposable
{
    private static readonly SignatureAlgorithm SigningAlgorithm = SignatureAlgorithm.Ed25519;
    private static readonly KeyAgreementAlgorithm EncryptionAlgorithm = KeyAgreementAlgorithm.X25519;

    // KeyCreationParameters is a ref struct (NSec's choice, not this codebase's) - it can't be a static
    // field, only ever a fresh local/inline value.
    private static KeyCreationParameters ExportableKeyParams => new() { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };

    private readonly Key signingKey;
    private readonly Key encryptionKey;

    public RelayIdentityService(string configDirectory, IPluginLog log)
    {
        Directory.CreateDirectory(configDirectory);
        var path = Path.Combine(configDirectory, "cross-dc-identity.json");

        var loaded = TryLoad(path, log);
        if (loaded is { } pair)
        {
            signingKey = pair.Signing;
            encryptionKey = pair.Encryption;
            return;
        }

        signingKey = Key.Create(SigningAlgorithm, ExportableKeyParams);
        encryptionKey = Key.Create(EncryptionAlgorithm, ExportableKeyParams);
        Save(path, signingKey, encryptionKey, log);
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

    private static (Key Signing, Key Encryption)? TryLoad(string path, IPluginLog log)
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
            return (signing, encryption);
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

    private static void Save(string path, Key signing, Key encryption, IPluginLog log)
    {
        try
        {
            var stored = new StoredIdentity(
                Convert.ToBase64String(signing.Export(KeyBlobFormat.RawPrivateKey)),
                Convert.ToBase64String(encryption.Export(KeyBlobFormat.RawPrivateKey)));
            File.WriteAllText(path, JsonSerializer.Serialize(stored));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to persist the cross-DC identity - a new one will be generated next launch");
        }
    }

    public void Dispose()
    {
        signingKey.Dispose();
        encryptionKey.Dispose();
    }

    private sealed record StoredIdentity(string SigningKeySeed, string EncryptionKeySeed);
}
