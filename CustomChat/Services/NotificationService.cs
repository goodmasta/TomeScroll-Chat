using System;
using System.Collections.Generic;
using CustomChat.Models;

namespace CustomChat.Services;

/// <summary>
/// General-purpose in-plugin popup notifications, rendered by <see cref="Windows.NotificationOverlay"/>
/// as a small stack of auto-dismissing toasts - not tied to chat/translation/any one feature, meant to
/// be called from anywhere in the plugin (including future features) via <see cref="Plugin.NotificationService"/>,
/// same reasoning as <see cref="GeminiService"/> being exposed generically rather than baked into one
/// caller. Distinct from the two notification paths that already existed before this: <c>IToastGui</c>
/// (Dalamud's native, game-styled toast - plain text only, no colour/icon/click control) and
/// <see cref="WindowsNotificationService"/> (an OS-level tray balloon, only used for incoming tells
/// while the game window is minimized). This one is for anything worth telling the player about while
/// they're actually looking at the game, with more control than a native toast allows.
///
/// <para>Thread-safe (<see cref="Show"/> can be called from a background thread - most callers this
/// was built for, e.g. a translation failure or a linkshell being auto-joined, happen off the main
/// thread) via a simple lock, matching <see cref="TabMessageBuffer"/>'s own pattern for a small,
/// infrequently-touched shared list.</para>
/// </summary>
public sealed class NotificationService
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(5);

    public sealed record Notification(string Message, NotificationSeverity Severity, DateTime ExpiresAt);

    private readonly List<Notification> active = new();
    private readonly object gate = new();

    /// <summary>Queues a popup - shows almost immediately (drawn the next frame) and disappears on
    /// its own after <paramref name="duration"/> (or <see cref="DefaultDuration"/>), fading out over
    /// its last moment rather than vanishing abruptly. Also dismissible early by clicking it.</summary>
    public void Show(string message, NotificationSeverity severity = NotificationSeverity.Info, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        lock (gate)
            active.Add(new Notification(message, severity, DateTime.UtcNow + (duration ?? DefaultDuration)));
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
