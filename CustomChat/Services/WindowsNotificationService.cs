using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CustomChat.Services;

/// <summary>
/// Shows a native Windows notification (via <see cref="NotifyIcon.ShowBalloonTip"/> - the simplest
/// way to get an OS-level notification without the extra Windows Runtime toast plumbing) when a
/// tell is received *and the game window is minimized* - the whole point is to catch tells you'd
/// otherwise miss because you're not looking at the game at all, not to also throw a Windows
/// balloon on top of the in-game chat while you're actively playing.
///
/// <see cref="NotifyIcon"/> internally creates a hidden window and relies on a running Windows
/// message pump to actually deliver tray-icon/balloon-related messages - a Dalamud plugin runs
/// in-process inside the game, which pumps its own (DirectX/game-loop) messages, not a standard
/// WinForms one, so creating/using a NotifyIcon directly on whatever thread Dalamud calls this from
/// is unreliable (the icon can appear while the balloon silently never shows). Fixed by running a
/// real WinForms message loop (<see cref="Application.Run()"/>) on a dedicated background STA
/// thread that this service owns end to end, with a hidden <see cref="Form"/> on that same thread
/// used purely as an <see cref="Control.Invoke"/> target to marshal calls onto it safely.
/// </summary>
public sealed class WindowsNotificationService : IDisposable
{
    private const int MaxMessageLength = 200;

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new(false);
    private Form? messageForm;
    private NotifyIcon? notifyIcon;
    private bool enabledOnStart;

    public WindowsNotificationService()
    {
        thread = new Thread(RunMessageLoop) { IsBackground = true, Name = "CustomChatTrayIcon" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
    }

    private void RunMessageLoop()
    {
        // Never shown - exists purely so this thread has a window whose message loop keeps the
        // NotifyIcon's own hidden window (and therefore its balloon tip) actually working, and to
        // give Invoke() somewhere to marshal calls onto this thread.
        messageForm = new Form
        {
            ShowInTaskbar = false,
            WindowState = FormWindowState.Minimized,
            Opacity = 0,
        };
        _ = messageForm.Handle; // forces the native window to actually be created now, not lazily

        notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Custom Chat",
            Visible = enabledOnStart,
        };

        ready.Set();
        Application.Run();
    }

    /// <summary>Whether the tray icon (and therefore the ability to show a balloon at all) is
    /// present - tied to <see cref="Configuration.NotifyWhisperInWindows"/>.</summary>
    public bool Enabled
    {
        set
        {
            if (messageForm == null || messageForm.IsDisposed)
            {
                enabledOnStart = value; // set before the message loop thread has finished starting up
                return;
            }

            messageForm.BeginInvoke(() =>
            {
                if (notifyIcon != null)
                    notifyIcon.Visible = value;
            });
        }
    }

    public void ShowTell(string senderName, string message)
    {
        if (messageForm == null || messageForm.IsDisposed)
            return;

        messageForm.BeginInvoke(() =>
        {
            if (notifyIcon is not { Visible: true } || !IsGameWindowMinimized())
                return;

            var text = message.Length > MaxMessageLength ? message[..MaxMessageLength] + "..." : message;
            notifyIcon.BalloonTipTitle = $"Tell from {senderName}";
            notifyIcon.BalloonTipText = text;
            notifyIcon.ShowBalloonTip(5000);
        });
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
        ready.Dispose();

        if (messageForm is { IsDisposed: false })
        {
            messageForm.Invoke(() =>
            {
                notifyIcon?.Dispose();
                Application.ExitThread();
            });
        }

        thread.Join(TimeSpan.FromSeconds(2));
    }
}
