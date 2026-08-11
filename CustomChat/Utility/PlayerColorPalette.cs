using System.Collections.Concurrent;
using System.Numerics;
using System.Text;

namespace CustomChat.Utility;

/// <summary>
/// Assigns each chat participant a stable, distinct-looking colour for their nickname (message
/// bodies keep using the normal per-channel colour - only the nick itself is coloured per-player).
/// The colour is derived from a hash of the player's key rather than actually randomised per call,
/// so the same person gets the same colour every time instead of a new one on every message.
/// </summary>
public static class PlayerColorPalette
{
    private static readonly ConcurrentDictionary<string, Vector4> Cache = new();

    public static Vector4 GetColor(string key) => Cache.GetOrAdd(key, BuildColor);

    private static Vector4 BuildColor(string key)
    {
        var hash = Fnv1A(key);

        // Golden-angle-ish spread across hue plus the hash keeps adjacent players visually distinct
        // rather than clustering; fixed saturation/value keeps every colour readable on a dark background.
        var hue = (hash % 360u) / 360f;
        return HsvToRgb(hue, 0.55f, 0.95f);
    }

    private static uint Fnv1A(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }

    private static Vector4 HsvToRgb(float h, float s, float v)
    {
        var i = (int)(h * 6f);
        var f = h * 6f - i;
        var p = v * (1f - s);
        var q = v * (1f - f * s);
        var t = v * (1f - (1f - f) * s);

        var (r, g, b) = (i % 6) switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };

        return new Vector4(r, g, b, 1f);
    }
}
