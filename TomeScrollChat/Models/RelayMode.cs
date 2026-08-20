namespace TomeScrollChat.Models;

/// <summary>Which cross-DC relay server (if any) <see cref="Configuration.CrossDcRelayMode"/> uses - see
/// <see cref="Services.ManagedRelayEndpoint"/> for how <see cref="Managed"/>'s actual address stays out
/// of both the settings UI and this (public) repository's source.</summary>
public enum RelayMode
{
    /// <summary>The whole feature is off - no connection is ever attempted, no identity keys are even
    /// generated. The default for a fresh install.</summary>
    Disabled,

    /// <summary>TomeScroll's own hosted relay. Its address is never shown anywhere in Settings.</summary>
    Managed,

    /// <summary>A relay server the player runs themselves - address is
    /// <see cref="Configuration.CrossDcRelaySelfHostedUrl"/>, plainly visible and editable in Settings,
    /// since it's the player's own infrastructure, not TomeScroll's.</summary>
    SelfHosted,
}
