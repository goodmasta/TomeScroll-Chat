using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using TomeScrollChat.Services;

namespace TomeScrollChat.Windows;

/// <summary>Popup listing the game's own auto-translate dictionary phrases (see
/// <see cref="AutoTranslatePhraseService"/>), searchable by text or category - opened by pressing Tab
/// in the chat input box (see <c>MainWindow.DrawInputRow</c>/<c>DetachedTabWindow.DrawInputRow</c>),
/// mirroring the native chat's own Tab-key dictionary browser in spirit (search-driven here rather
/// than the native numbered/paginated category tree, which isn't practical to replicate exactly in
/// ImGui - picking a phrase achieves the same outcome either way).</summary>
public static class AutoTranslatePicker
{
    private const float PopupWidth = 340f;
    private const float ListHeight = 320f;

    public static void Draw(string popupId, AutoTranslatePhraseService phraseService, ref string search, Action<AutoTranslatePhrase> onPicked)
    {
        if (!ImGui.BeginPopup(popupId))
            return;

        ImGui.SetNextItemWidth(PopupWidth);
        ImGui.InputTextWithHint($"##{popupId}_search", "Search auto-translate phrases...", ref search, 64);

        using (var child = ImRaii.Child($"{popupId}_list", new Vector2(PopupWidth, ListHeight), true))
        {
            if (child.Success)
            {
                string? lastGroupTitle = null;
                foreach (var phrase in phraseService.Phrases)
                {
                    if (!string.IsNullOrEmpty(search) &&
                        phrase.Text.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                        phrase.GroupTitle.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    if (phrase.GroupTitle != lastGroupTitle)
                    {
                        lastGroupTitle = phrase.GroupTitle;
                        ImGui.TextDisabled(string.IsNullOrEmpty(phrase.GroupTitle) ? "(uncategorised)" : phrase.GroupTitle);
                    }

                    if (ImGui.Selectable($"{phrase.Text}##{phrase.Group}_{phrase.RowId}"))
                    {
                        onPicked(phrase);
                        ImGui.CloseCurrentPopup();
                    }
                }
            }
        }

        ImGui.EndPopup();
    }
}
