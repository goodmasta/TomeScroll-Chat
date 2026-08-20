namespace TomeScrollChat.Services;

/// <summary>
/// Resolves the address of TomeScroll's own hosted relay (<see cref="Models.RelayMode.Managed"/>) -
/// deliberately split into this committed file and a <c>ManagedRelayEndpoint.Secret.cs</c> that is
/// <b>not</b> committed (see <c>.gitignore</c>), since this repository is public on GitHub: a plain
/// constant here would be visible to anyone browsing the source, regardless of the fact that the
/// Settings UI never displays it.
///
/// <para><c>ManagedRelayEndpoint.Secret.cs</c> only needs to exist on whatever machine actually produces
/// a release build - see <c>ManagedRelayEndpoint.Secret.cs.example</c> for the file to copy and fill in.
/// <see cref="TryGetSecretUrl"/> is a <i>void</i> partial method with no explicit access modifier
/// specifically so it's the one kind of partial method C# allows to have no implementing part at all -
/// if the secret file is missing (e.g. someone building this plugin from a fresh clone of the public
/// repo), the compiler silently erases the call to it instead of failing the build, <c>url</c> stays
/// null, and <see cref="GetUrl"/> returns null - which is what makes Settings disable the Managed option
/// rather than the plugin crashing or connecting to nowhere. (An earlier version of this used a
/// non-void, publicly-accessible partial method for <c>GetUrl</c> itself - that turned out to *require*
/// an implementing part once it has an access modifier or non-void return, so a plain clone without the
/// secret file failed to build entirely; caught by actually deleting the secret file and rebuilding, not
/// assumed.)</para>
/// </summary>
public static partial class ManagedRelayEndpoint
{
    /// <summary>The <c>wss://</c> URL of the managed relay, or <c>null</c> if this build has no
    /// <c>ManagedRelayEndpoint.Secret.cs</c>.</summary>
    public static string? GetUrl()
    {
        string? url = null;
        TryGetSecretUrl(ref url);
        return url;
    }

    static partial void TryGetSecretUrl(ref string? url);
}
