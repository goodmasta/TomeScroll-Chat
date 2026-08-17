using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using TomeScrollChat.Services;

namespace TomeScrollChat.Windows;

/// <summary>Popup listing every loaded emote (standard emoji first, then BTTV/7TV) as a searchable
/// image grid - opened from a button next to the chat input, and from the friend-marker picker in
/// settings. Fixed cell size, so wrapping to the next row is just "does the next cell fit in what's
/// left of this line", no dynamic text-wrap measurement involved.</summary>
public static class EmotePicker
{
    private const float CellSize = 32f;
    private const float CellSpacing = 6f;
    private const float PopupWidth = 260f;

    public static void Draw(string popupId, EmoteService emotes, ref string search, Action<string> onPicked)
    {
        if (!ImGui.BeginPopup(popupId))
            return;

        ImGui.SetNextItemWidth(PopupWidth);
        ImGui.InputTextWithHint($"##{popupId}_search", "Search emotes...", ref search, 64);

        using (var child = ImRaii.Child($"{popupId}_list", new Vector2(PopupWidth, 300), true))
        {
            if (child.Success)
            {
                var rightEdge = ImGui.GetWindowContentRegionMax().X;
                var isFirst = true;

                foreach (var emote in emotes.GetLoadedEmotes())
                {
                    if (!string.IsNullOrEmpty(search) && emote.Code.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    // GetCursorPosX() here (before deciding whether to SameLine) always reads the
                    // "fresh next line" position, not "continuing after the previous item" - the exact
                    // same mistake already fixed once in ChatMessageRenderer. That trivially satisfies
                    // the fit-check every time, so SameLine() got called unconditionally and every cell
                    // piled onto one endless row instead of ever wrapping. GetItemRectMax() of the
                    // *previous* item is the reliable reference (safe here specifically because every
                    // cell is a fixed-size, single-line Button/ImageButton, never wrapped internally).
                    if (!isFirst)
                    {
                        var prevRightX = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;
                        if (prevRightX + CellSpacing + CellSize <= rightEdge)
                            ImGui.SameLine(0, CellSpacing);
                    }

                    ImGui.PushID(emote.Code);
                    var texture = emotes.TryGetTexture(emote.Code);
                    bool clicked;
                    if (texture != null)
                    {
                        clicked = ImGui.ImageButton(texture.Handle, new Vector2(CellSize, CellSize));
                    }
                    else
                    {
                        // Still downloading/decoding - a reserved placeholder, not skipped, so the
                        // grid doesn't reflow once the image finishes loading a frame or two later.
                        clicked = ImGui.Button("...", new Vector2(CellSize, CellSize));
                    }

                    ImGui.PopID();

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(emote.Code);

                    if (clicked)
                    {
                        onPicked(emote.Code);
                        ImGui.CloseCurrentPopup();
                    }

                    isFirst = false;
                }
            }
        }

        ImGui.EndPopup();
    }
}
