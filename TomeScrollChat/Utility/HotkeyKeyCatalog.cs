using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace TomeScrollChat.Utility;

/// <summary>Curated, typeable subset of <see cref="ImGuiKey"/> for the "cycle outgoing channel"
/// hotkey's key picker (Settings > Tabs) - not the full enum (gamepad buttons, the modifier keys
/// themselves, etc. wouldn't make sense as the "main" key of this hotkey). Member names verified
/// against the actual <c>Dalamud.Bindings.ImGui.dll</c> via the project's metadata tool rather than
/// guessed (e.g. digits are <c>Key0</c>-<c>Key9</c>, not <c>_0</c>-<c>_9</c>/<c>D0</c>-<c>D9</c>).</summary>
public static class HotkeyKeyCatalog
{
    public static readonly IReadOnlyList<(ImGuiKey Key, string Label)> Entries = new List<(ImGuiKey, string)>
    {
        (ImGuiKey.Space, "Space"),
        (ImGuiKey.Tab, "Tab"),
        (ImGuiKey.GraveAccent, "` (grave)"),
        (ImGuiKey.Key1, "1"), (ImGuiKey.Key2, "2"), (ImGuiKey.Key3, "3"), (ImGuiKey.Key4, "4"), (ImGuiKey.Key5, "5"),
        (ImGuiKey.Key6, "6"), (ImGuiKey.Key7, "7"), (ImGuiKey.Key8, "8"), (ImGuiKey.Key9, "9"), (ImGuiKey.Key0, "0"),
        (ImGuiKey.Minus, "-"), (ImGuiKey.Equal, "="),
        (ImGuiKey.A, "A"), (ImGuiKey.B, "B"), (ImGuiKey.C, "C"), (ImGuiKey.D, "D"), (ImGuiKey.E, "E"),
        (ImGuiKey.F, "F"), (ImGuiKey.G, "G"), (ImGuiKey.H, "H"), (ImGuiKey.I, "I"), (ImGuiKey.J, "J"),
        (ImGuiKey.K, "K"), (ImGuiKey.L, "L"), (ImGuiKey.M, "M"), (ImGuiKey.N, "N"), (ImGuiKey.O, "O"),
        (ImGuiKey.P, "P"), (ImGuiKey.Q, "Q"), (ImGuiKey.R, "R"), (ImGuiKey.S, "S"), (ImGuiKey.T, "T"),
        (ImGuiKey.U, "U"), (ImGuiKey.V, "V"), (ImGuiKey.W, "W"), (ImGuiKey.X, "X"), (ImGuiKey.Y, "Y"),
        (ImGuiKey.Z, "Z"),
        (ImGuiKey.LeftBracket, "["), (ImGuiKey.RightBracket, "]"), (ImGuiKey.Backslash, "\\"),
        (ImGuiKey.Semicolon, ";"), (ImGuiKey.Apostrophe, "'"),
        (ImGuiKey.Comma, ","), (ImGuiKey.Period, "."), (ImGuiKey.Slash, "/"),
        (ImGuiKey.F1, "F1"), (ImGuiKey.F2, "F2"), (ImGuiKey.F3, "F3"), (ImGuiKey.F4, "F4"),
        (ImGuiKey.F5, "F5"), (ImGuiKey.F6, "F6"), (ImGuiKey.F7, "F7"), (ImGuiKey.F8, "F8"),
        (ImGuiKey.F9, "F9"), (ImGuiKey.F10, "F10"), (ImGuiKey.F11, "F11"), (ImGuiKey.F12, "F12"),
    };

    public static string Label(ImGuiKey key)
    {
        foreach (var (k, label) in Entries)
        {
            if (k == key)
                return label;
        }

        return key.ToString();
    }
}
