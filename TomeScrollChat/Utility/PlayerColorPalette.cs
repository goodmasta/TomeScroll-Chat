using System.Collections.Concurrent;
using System.Numerics;

namespace TomeScrollChat.Utility;

/// <summary>
/// Assigns each chat participant a stable, distinct-looking colour for their nickname (message
/// bodies keep using the normal per-channel colour - only the nick itself is coloured per-player).
/// The colour is derived from a hash of the player's key (see <see cref="ColorHashUtility"/>) rather
/// than actually randomised per call, so the same person gets the same colour every time instead of a
/// new one on every message.
/// </summary>
public static class PlayerColorPalette
{
    private static readonly ConcurrentDictionary<string, Vector4> Cache = new();

    public static Vector4 GetColor(string key) => Cache.GetOrAdd(key, BuildColor);

    private static Vector4 BuildColor(string key)
    {
        var hash = ColorHashUtility.Fnv1A(key);

        // Golden-angle-ish spread across hue plus the hash keeps adjacent players visually distinct
        // rather than clustering; fixed saturation/value keeps every colour readable on a dark background.
        var hue = (hash % 360u) / 360f;
        return ColorHashUtility.HsvToRgb(hue, 0.55f, 0.95f);
    }
}
