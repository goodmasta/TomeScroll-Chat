using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using CustomChat.Services;

namespace CustomChat.Windows;

/// <summary>Popup listing every loaded BTTV/7TV emote with a search box - opened from a button next
/// to the chat input. Shared between the main window and detached tab windows.</summary>
public static class EmotePicker
{
    public static void Draw(string popupId, EmoteService emotes, ref string search, Action<string> onPicked)
    {
        if (!ImGui.BeginPopup(popupId))
            return;

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint($"##{popupId}_search", "Search emotes...", ref search, 64);

        using (var child = ImRaii.Child($"{popupId}_list", new Vector2(240, 300), true))
        {
            if (child.Success)
            {
                foreach (var emote in emotes.GetLoadedEmotes())
                {
                    if (!string.IsNullOrEmpty(search) && emote.Code.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var texture = emotes.TryGetTexture(emote.Code);
                    if (texture != null)
                    {
                        ImGui.Image(texture.Handle, new Vector2(20, 20));
                        ImGui.SameLine();
                    }

                    if (ImGui.Selectable($"{emote.Code}##{popupId}_{emote.Code}"))
                    {
                        onPicked(emote.Code);
                        ImGui.CloseCurrentPopup();
                    }
                }
            }
        }

        ImGui.EndPopup();
    }
}
