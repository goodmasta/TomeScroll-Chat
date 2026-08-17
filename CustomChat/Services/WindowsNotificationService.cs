using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Dalamud.Plugin.Services;

namespace CustomChat.Services;

/// <summary>
/// Shows a native Windows notification (via <see cref="NotifyIcon.ShowBalloonTip"/> - the simplest
/// way to get an OS-level notification without the extra Windows Runtime toast plumbing) when a
/// tell is received *and the game isn't what's currently in front of the player* - the whole point
/// is to catch tells you'd otherwise miss because you're not looking at the game at all, not to also
/// throw a Windows balloon on top of the in-game chat while you're actively playing.
///
/// <see cref="NotifyIcon"/> internally creates a hidden window and relies on a running Windows
/// message pump to actually deliver tray-icon/balloon-related messages - a Dalamud plugin runs
/// in-process inside the game, which pumps its own (DirectX/game-loop) messages, not a standard
/// WinForms one, so creating/using a NotifyIcon directly on whatever thread Dalamud calls this from
/// is unreliable (the icon can appear while the balloon silently never shows). Fixed by running a
/// real WinForms message loop (<see cref="Application.Run()"/>) on a dedicated background STA
/// thread that this service owns end to end, with a hidden <see cref="Form"/> on that same thread
/// used purely as an <see cref="Control.Invoke"/> target to marshal calls onto it safely.
///
/// Every marshaled call is wrapped in try/catch + logging - <see cref="Control.BeginInvoke(Delegate)"/>
/// swallows exceptions from the delegate entirely unless something calls <c>EndInvoke</c> (which
/// nothing here does, by design - these are fire-and-forget), so without this any failure here would
/// otherwise be completely silent.
/// </summary>
public sealed class WindowsNotificationService : IDisposable
{
    private const int MaxMessageLength = 200;

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly IPluginLog log;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new(false);
    private Form? messageForm;
    private NotifyIcon? notifyIcon;
    private bool enabledOnStart;

    public WindowsNotificationService(IPluginLog log)
    {
        this.log = log;
        thread = new Thread(RunMessageLoop) { IsBackground = true, Name = "CustomChatTrayIcon" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
    }

    private void RunMessageLoop()
    {
        try
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
        }
        catch (Exception ex)
        {
            log.Error(ex, "CustomChat: failed to set up the Windows notification tray icon");
        }
        finally
        {
            // Unblocks the constructor either way - if setup failed, messageForm/notifyIcon stay
            // null and every call below becomes a safe no-op instead of hanging the plugin forever.
            ready.Set();
        }

        if (messageForm != null)
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
                try
                {
                    if (notifyIcon != null)
                        notifyIcon.Visible = value;
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "CustomChat: failed to toggle the Windows notification tray icon");
                }
            });
        }
    }

    public void ShowTell(string senderName, string message)
    {
        if (messageForm == null || messageForm.IsDisposed)
        {
            log.Warning("CustomChat: can't show a Windows notification - the tray icon never finished setting up");
            return;
        }

        messageForm.BeginInvoke(() =>
        {
            try
            {
                if (notifyIcon is not { Visible: true })
                    return;

                if (!ShouldNotify())
                {
                    log.Debug("CustomChat: skipping Windows notification - the game window is focused and not minimized");
                    return;
                }

                var text = message.Length > MaxMessageLength ? message[..MaxMessageLength] + "..." : message;
                notifyIcon.BalloonTipTitle = $"Tell from {senderName}";
                notifyIcon.BalloonTipText = text;
                notifyIcon.ShowBalloonTip(5000);
                log.Debug("CustomChat: showed a Windows notification for a tell from {Sender}", senderName);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "CustomChat: failed to show a Windows notification");
            }
        });
    }

    /// <summary>"Test Windows notification" button's handler (Settings > Notifications) - shows a
    /// balloon immediately, bypassing <see cref="ShowTell"/>'s <see cref="ShouldNotify"/> gate (which
    /// normally suppresses the balloon while the game window is actually focused - true almost by
    /// definition while clicking a button in this plugin's own settings window, which would otherwise
    /// make the test silently do nothing). Still requires the tray icon to actually be visible
    /// (<see cref="Enabled"/>/<see cref="Configuration.NotifyWhisperInWindows"/>) - the caller is
    /// expected to gate the button on that rather than this method forcing it, since toggling
    /// <see cref="NotifyIcon.Visible"/> just for a test risks the balloon disappearing again before
    /// Windows has actually shown it.</summary>
    public void ShowTest()
    {
        if (messageForm == null || messageForm.IsDisposed)
        {
            log.Warning("CustomChat: can't show a Windows notification - the tray icon never finished setting up");
            return;
        }

        messageForm.BeginInvoke(() =>
        {
            try
            {
                if (notifyIcon is not { Visible: true })
                    return;

                notifyIcon.BalloonTipTitle = "Custom Chat";
                notifyIcon.BalloonTipText = "This is a test Windows notification.";
                notifyIcon.ShowBalloonTip(5000);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "CustomChat: failed to show a test Windows notification");
            }
        });
    }

    /// <summary>A Dalamud plugin runs in-process with the game, so the current process's own main
    /// window *is* the game window - no need to find/match it by title or anything else. Checks two
    /// things, either of which counts as "not what the player is looking at": the window is actually
    /// minimized (<c>IsIconic</c>), or some *other* window currently has focus - a game running in
    /// exclusive fullscreen doesn't necessarily become "iconic" in the strict Win32 sense when you
    /// alt-tab away from it, so relying on <c>IsIconic</c> alone would miss that case entirely. If the
    /// window handle can't be determined at all, this errs on the side of notifying anyway rather than
    /// the feature silently never firing.</summary>
    private static bool ShouldNotify()
    {
        var handle = Process.GetCurrentProcess().MainWindowHandle;
        return handle == IntPtr.Zero || IsIconic(handle) || GetForegroundWindow() != handle;
    }

    public void Dispose()
    {
        ready.Dispose();

        if (messageForm is { IsDisposed: false })
        {
            try
            {
                messageForm.Invoke(() =>
                {
                    notifyIcon?.Dispose();
                    Application.ExitThread();
                });
            }
            catch (Exception ex)
            {
                log.Warning(ex, "CustomChat: failed to clean up the Windows notification tray icon");
            }
        }

        thread.Join(TimeSpan.FromSeconds(2));
    }
}
