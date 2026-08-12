using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CustomChat.Services;

/// <summary>
/// Shows a native Windows notification (via <see cref="NotifyIcon.ShowBalloonTip"/> - the simplest
/// way to get an OS-level notification without the extra Windows Runtime toast plumbing) when a
/// tell is received *and the game window is minimized* - the whole point is to catch tells you'd
/// otherwise miss because you're not looking at the game at all, not to also throw a Windows
/// balloon on top of the in-game chat while you're actively playing. Windows requires the tray icon
/// backing a balloon tip to actually be visible for the balloon to show at all, so this keeps a
/// small tray icon around for as long as the feature is enabled in Settings - the same tradeoff most
/// other notification-capable background apps (Discord, Steam, etc.) make.
/// </summary>
public sealed class WindowsNotificationService : IDisposable
{
    private const int MaxMessageLength = 200;

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private readonly NotifyIcon notifyIcon;

    public WindowsNotificationService()
    {
        notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Custom Chat",
            Visible = false,
        };
    }

    /// <summary>Whether the tray icon (and therefore the ability to show a balloon at all) is
    /// present - tied to <see cref="Configuration.NotifyWhisperInWindows"/>.</summary>
    public bool Enabled
    {
        set => notifyIcon.Visible = value;
    }

    public void ShowTell(string senderName, string message)
    {
        if (!notifyIcon.Visible || !IsGameWindowMinimized())
            return;

        var text = message.Length > MaxMessageLength ? message[..MaxMessageLength] + "..." : message;
        notifyIcon.BalloonTipTitle = $"Tell from {senderName}";
        notifyIcon.BalloonTipText = text;
        notifyIcon.ShowBalloonTip(5000);
    }

    /// <summary>A Dalamud plugin runs in-process with the game, so the current process's own main
    /// window *is* the game window - no need to find/match it by title or anything else.</summary>
    private static bool IsGameWindowMinimized()
    {
        var handle = Process.GetCurrentProcess().MainWindowHandle;
        return handle != IntPtr.Zero && IsIconic(handle);
    }

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
    }
}
