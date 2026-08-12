using System;
using System.Drawing;
using System.Windows.Forms;

namespace CustomChat.Services;

/// <summary>
/// Shows a native Windows notification (via <see cref="NotifyIcon.ShowBalloonTip"/> - the simplest
/// way to get an OS-level notification without the extra Windows Runtime toast plumbing) when a
/// tell is received, so it's noticeable even with the game window minimized/unfocused, unlike
/// Dalamud's own in-overlay notifications, which only show up while actually looking at the game.
/// Windows requires the tray icon backing a balloon tip to actually be visible for the balloon to
/// show at all, so this keeps a small tray icon around for as long as the feature is enabled in
/// Settings - the same tradeoff most other notification-capable background apps (Discord, Steam,
/// etc.) make.
/// </summary>
public sealed class WindowsNotificationService : IDisposable
{
    private const int MaxMessageLength = 200;

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
        if (!notifyIcon.Visible)
            return;

        var text = message.Length > MaxMessageLength ? message[..MaxMessageLength] + "..." : message;
        notifyIcon.BalloonTipTitle = $"Tell from {senderName}";
        notifyIcon.BalloonTipText = text;
        notifyIcon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
    }
}
