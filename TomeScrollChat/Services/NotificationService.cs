using System;
using System.Collections.Generic;
using TomeScrollChat.Models;

namespace TomeScrollChat.Services;

/// <summary>
/// General-purpose in-plugin popup notifications, rendered by <see cref="Windows.NotificationOverlay"/>
/// as a small stack of auto-dismissing toasts - not tied to chat/translation/any one feature, meant to
/// be called from anywhere in the plugin (including future features) via <see cref="Plugin.NotificationService"/>,
/// same reasoning as <see cref="GeminiService"/> being exposed generically rather than baked into one
/// caller. Distinct from <c>IToastGui</c> (Dalamud's native, game-styled toast - plain text only, no
/// colour/icon/click control, already used for a few one-off confirmations) - this one is for anything
/// worth telling the player about with more control than a native toast allows. An OS-level Windows
/// tray-balloon notification (for incoming tells while minimized) was also tried and removed
/// (2026-08-17) - <c>NotifyIcon.ShowBalloonTip</c> turned out to be silently suppressed by Windows
/// itself in practice, and the actually-reliable modern replacement needs a runtime dependency most
/// players won't have installed; this in-game popup is the one general-purpose option going forward.
///
/// <para>Thread-safe (<see cref="Show"/> can be called from a background thread - most callers this
/// was built for, e.g. a translation failure or a linkshell being auto-joined, happen off the main
/// thread) via a simple lock, matching <see cref="TabMessageBuffer"/>'s own pattern for a small,
/// infrequently-touched shared list.</para>
///
/// <para>Every <see cref="Show"/> call also plays a short sound via <see cref="NotificationSoundService"/>
/// (Settings > Notifications, on by default) - see that class's own doc comment for what sound and why.</para>
/// </summary>
public sealed class NotificationService
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(5);

    public sealed record Notification(string Message, NotificationSeverity Severity, DateTime ExpiresAt);

    private readonly List<Notification> active = new();
    private readonly object gate = new();
    private readonly NotificationSoundService soundService;

    public NotificationService(NotificationSoundService soundService)
    {
        this.soundService = soundService;
    }

    /// <summary>Queues a popup - shows almost immediately (drawn the next frame) and disappears on
    /// its own after <paramref name="duration"/> (or <see cref="DefaultDuration"/>), fading out over
    /// its last moment rather than vanishing abruptly. Also dismissible early by clicking it. Plays a
    /// sound alongside it via <see cref="NotificationSoundService"/> unless
    /// <see cref="Configuration.NotificationSoundEnabled"/> is off.</summary>
    public void Show(string message, NotificationSeverity severity = NotificationSeverity.Info, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        lock (gate)
            active.Add(new Notification(message, severity, DateTime.UtcNow + (duration ?? DefaultDuration)));

        soundService.PlayIfEnabled();
    }

    /// <summary>Currently-visible notifications, oldest first - already pruned of anything expired.
    /// A fresh snapshot array each call (safe to enumerate without holding any lock).</summary>
    public IReadOnlyList<Notification> Active
    {
        get
        {
            lock (gate)
            {
                active.RemoveAll(n => n.ExpiresAt <= DateTime.UtcNow);
                return active.ToArray();
            }
        }
    }

    /// <summary>Removes specific notifications immediately - clicking one in <see cref="Windows.NotificationOverlay"/>.</summary>
    public void Dismiss(IEnumerable<Notification> notifications)
    {
        lock (gate)
        {
            foreach (var n in notifications)
                active.Remove(n);
        }
    }
}
