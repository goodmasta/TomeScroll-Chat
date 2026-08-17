using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using CustomChat.Models;
using CustomChat.Services;

namespace CustomChat.Windows;

/// <summary>
/// Draws whatever's currently queued in <see cref="NotificationService"/> as a small stack of toasts
/// anchored to the top-right of the game window - fixed width (so its X position is knowable without
/// first drawing its content, avoiding the "need the size to compute the position, need the position
/// drawn to know the size" trap other windows in this project have hit before), height grows freely
/// per-card via <see cref="ImGuiWindowFlags.AlwaysAutoResize"/> as notifications stack up. Always
/// registered but only actually drawn while there's at least one notification live (see
/// <see cref="DrawConditions"/>), same "always open, closing is refused" shape as <c>MainWindow</c>
/// (nothing about this window is meant to be user-closable - it just has nothing to draw most of the
/// time).
/// </summary>
public sealed class NotificationOverlay : Window
{
    private const float Width = 320f;
    private const float Margin = 16f;
    private const float FadeSeconds = 0.6f;

    private static readonly Vector4 InfoColor = new(0.55f, 0.8f, 1f, 1f);
    private static readonly Vector4 SuccessColor = new(0.5f, 1f, 0.6f, 1f);
    private static readonly Vector4 WarningColor = new(1f, 0.75f, 0.3f, 1f);
    private static readonly Vector4 ErrorColor = new(1f, 0.4f, 0.4f, 1f);

    private readonly NotificationService notifications;

    public NotificationOverlay(NotificationService notifications)
        : base("###CustomChatNotificationOverlay")
    {
        this.notifications = notifications;

        IsOpen = true;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        DisableWindowSounds = true; // this "window" never really opens/closes in the usual sense
        IsTopMost = true;

        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.AlwaysAutoResize;

        // Width fixed, height free - see the class doc comment for why this specific split matters.
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(Width, 10),
            MaximumSize = new Vector2(Width, float.MaxValue),
        };
    }

    public override void OnClose() => IsOpen = true;

    public override bool DrawConditions() => notifications.Active.Count > 0;

    public override void PreDraw()
    {
        var viewport = ImGuiHelpers.MainViewport;
        Position = new Vector2(viewport.Pos.X + viewport.Size.X - Width - Margin, viewport.Pos.Y + Margin);
        PositionCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        var toShow = notifications.Active;
        List<NotificationService.Notification>? toDismiss = null;

        for (var i = 0; i < toShow.Count; i++)
        {
            var notification = toShow[i];
            var remainingSeconds = (notification.ExpiresAt - DateTime.UtcNow).TotalSeconds;
            var alpha = remainingSeconds < FadeSeconds ? Math.Clamp((float)(remainingSeconds / FadeSeconds), 0f, 1f) : 1f;

            var (accent, icon) = notification.Severity switch
            {
                NotificationSeverity.Success => (SuccessColor, FontAwesomeIcon.CheckCircle),
                NotificationSeverity.Warning => (WarningColor, FontAwesomeIcon.ExclamationTriangle),
                NotificationSeverity.Error => (ErrorColor, FontAwesomeIcon.TimesCircle),
                _ => (InfoColor, FontAwesomeIcon.InfoCircle),
            };

            // One shared alpha for the whole card (background, border, icon, text) rather than tinting
            // each colour individually - simpler, and nothing here needs independent per-element alpha.
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, alpha);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(accent.X * 0.15f, accent.Y * 0.15f, accent.Z * 0.15f, 0.9f));
            ImGui.PushStyleColor(ImGuiCol.Border, accent);

            using (ImRaii.PushId(i))
            using (var child = ImRaii.Child($"notif_{i}", new Vector2(-1, 0), true, ImGuiWindowFlags.AlwaysAutoResize))
            {
                if (child.Success)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, accent);
                    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
                        ImGui.TextUnformatted(icon.ToIconString());
                    ImGui.PopStyleColor();

                    ImGui.SameLine();
                    ImGui.PushTextWrapPos(ImGui.GetWindowContentRegionMax().X);
                    ImGui.TextUnformatted(notification.Message);
                    ImGui.PopTextWrapPos();

                    if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        (toDismiss ??= new List<NotificationService.Notification>()).Add(notification);
                }
            }

            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar();
            ImGui.Spacing();
        }

        if (toDismiss != null)
            notifications.Dismiss(toDismiss);
    }
}
