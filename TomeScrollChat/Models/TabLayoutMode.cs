namespace TomeScrollChat.Models;

/// <summary>How <see cref="Windows.MainWindow"/> lists its tabs - see <see cref="Configuration.TabLayout"/>.</summary>
public enum TabLayoutMode
{
    /// <summary>The original layout: a fixed-width vertical list on the left, messages on the right.</summary>
    Sidebar,

    /// <summary>Browser-style: a horizontal, wrapping strip of tab buttons across the top, messages
    /// filling the rest of the window below.</summary>
    Tabs,
}
