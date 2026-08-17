using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using CustomChat.Models;
using CustomChat.Services;

namespace CustomChat.Windows;

/// <summary>
/// Draws whatever's currently queued in <see cref="NotificationService"/> as a small stack of toasts
/// anchored just to the right of <c>MainWindow</c>'s own title bar (see <see cref="MainWindow.ScreenPosition"/>/
/// <see cref="MainWindow.ScreenSize"/>, captured there for exactly this) - not a fixed screen corner,
/// so it moves/hides along with the chat window itself rather than sitting in some unrelated part of
/// the screen. Always registered but only actually drawn while there's at least one notification live
/// (see <see cref="DrawConditions"/>), same "always open, closing is refused" shape as <c>MainWindow</c>
/// - nothing about this window is meant to be user-closable, it just has nothing to draw most of the
/// time.
///
/// <para><b>Sizing is fully manual</b> (<see cref="PreDraw"/> measures every card's wrapped-text height
/// via <see cref="ImGui.CalcTextSize(string, bool, float)"/> and forces an exact <c>Size</c> every
/// frame) rather than <c>ImGuiWindowFlags.AlwaysAutoResize</c>, which was tried first and produced a
/// badly oversized window in practice (reported - filled almost the whole screen). Root cause not
/// pinned down, but this window sharing <c>IsTopMost</c> with the *other* thing in this project that
/// hit an unexplained sizing/state quirk (<c>MainWindow</c>'s <c>Collapsed</c> handling, see memory) is
/// the prime suspect - rather than debug auto-resize further, this just measures content up front and
/// forces the result directly, the same "don't trust automatic sizing here, compute and force it"
/// approach that fixed that earlier issue too.</para>
///
/// <para>Each card's actual on-screen rectangle is captured via <c>GetItemRectMin/Max</c> right after
/// drawing its real content (icon + wrapped text), then painted behind it via
/// <see cref="ImDrawListPtr.ChannelsSplit"/> - the exact same "measure what was actually drawn, paint
/// the background behind it" technique <c>ChatMessageRenderer.DrawMessage</c>'s own row highlight
/// already uses successfully in this project, reused here instead of inventing a new approach.</para>
/// </summary>
public sealed class NotificationOverlay : Window
{
    private const float Width = 320f;
    private const float Margin = 16f;
    private const float FadeSeconds = 0.6f;
    private const float CardPadding = 8f;
    private const float CardSpacing = 6f;
    private const float IconColumnWidth = 22f;

    private static readonly Vector4 InfoColor = new(0.55f, 0.8f, 1f, 1f);
    private static readonly Vector4 SuccessColor = new(0.5f, 1f, 0.6f, 1f);
    private static readonly Vector4 WarningColor = new(1f, 0.75f, 0.3f, 1f);
    private static readonly Vector4 ErrorColor = new(1f, 0.4f, 0.4f, 1f);

    private readonly NotificationService notifications;
    private readonly MainWindow mainWindow;

    public NotificationOverlay(NotificationService notifications, MainWindow mainWindow)
        : base("###CustomChatNotificationOverlay")
    {
        this.notifications = notifications;
        this.mainWindow = mainWindow;

        IsOpen = true;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        DisableWindowSounds = true; // this "window" never really opens/closes in the usual sense
        IsTopMost = true;

        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBackground;
    }

    public override void OnClose() => IsOpen = true;

    public override bool DrawConditions() => notifications.Active.Count > 0;

    public override void PreDraw()
    {
        var wrapWidth = Width - (CardPadding * 2) - IconColumnWidth;
        var totalHeight = CardPadding;
        foreach (var n in notifications.Active)
        {
            var textHeight = ImGui.CalcTextSize(n.Message, false, wrapWidth).Y;
            var cardHeight = Math.Max(textHeight, ImGui.GetTextLineHeight()) + (CardPadding * 2);
            totalHeight += cardHeight + CardSpacing;
        }

        Size = new Vector2(Width, Math.Max(totalHeight, 1f));
        SizeCondition = ImGuiCond.Always;

        // Anchored just right of MainWindow's own title bar - roughly level with its "Custom Chat"
        // title text - rather than a fixed screen corner, so this moves with the chat window instead
        // of sitting in some unrelated spot. Clamped to the viewport's right edge so it can't be
        // pushed off-screen if MainWindow itself is already flush against it.
        var viewport = ImGuiHelpers.MainViewport;
        var desiredX = mainWindow.ScreenPosition.X + mainWindow.ScreenSize.X + Margin;
        var maxX = viewport.Pos.X + viewport.Size.X - Width - Margin;
        Position = new Vector2(Math.Min(desiredX, maxX), mainWindow.ScreenPosition.Y);
        PositionCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        var toShow = notifications.Active;
        List<NotificationService.Notification>? toDismiss = null;
        var drawList = ImGui.GetWindowDrawList();
        var wrapWidth = Width - (CardPadding * 2) - IconColumnWidth;

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

            drawList.ChannelsSplit(2);
            drawList.ChannelsSetCurrent(1); // foreground: real content, drawn first so its rect is known

            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, alpha);
            ImGui.Indent(CardPadding);
            ImGui.Dummy(new Vector2(0, 1)); // top inset before the content, tiny since ItemSpacing already adds most of it
            ImGui.BeginGroup();

            ImGui.PushStyleColor(ImGuiCol.Text, accent);
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
                ImGui.TextUnformatted(icon.ToIconString());
            ImGui.PopStyleColor();
            ImGui.SameLine();

            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
            ImGui.TextUnformatted(notification.Message);
            ImGui.PopTextWrapPos();

            ImGui.EndGroup();
            ImGui.Unindent(CardPadding);

            var cardMin = ImGui.GetItemRectMin() - new Vector2(CardPadding, CardPadding);
            var cardMax = ImGui.GetItemRectMax() + new Vector2(CardPadding, CardPadding);

            if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(cardMin, cardMax) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                (toDismiss ??= new List<NotificationService.Notification>()).Add(notification);

            drawList.ChannelsSetCurrent(0); // background: painted after, but rendered behind the content
            drawList.AddRectFilled(cardMin, cardMax, ImGui.GetColorU32(new Vector4(accent.X * 0.15f, accent.Y * 0.15f, accent.Z * 0.15f, 0.9f)), 4f);
            drawList.AddRect(cardMin, cardMax, ImGui.GetColorU32(accent), 4f);
            drawList.ChannelsMerge();

            ImGui.PopStyleVar();
            ImGui.Spacing();
        }

        if (toDismiss != null)
            notifications.Dismiss(toDismiss);
    }
}
