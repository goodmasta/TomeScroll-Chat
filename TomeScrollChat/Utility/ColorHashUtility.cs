using System.Numerics;
using System.Text;

namespace TomeScrollChat.Utility;

/// <summary>
/// Shared FNV-1a hash + HSV-&gt;RGB conversion behind <see cref="PlayerColorPalette"/> and
/// <see cref="NpcColorPalette"/> - both derive a colour purely from a hash of a string key (never
/// randomised, never stored), so the same key always maps to the same colour, permanently, with
/// nothing to persist.
/// </summary>
internal static class ColorHashUtility
{
    public static uint Fnv1A(string value)
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

    public static Vector4 HsvToRgb(float h, float s, float v)
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
