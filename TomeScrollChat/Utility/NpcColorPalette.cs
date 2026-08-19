using System.Collections.Concurrent;
using System.Numerics;

namespace TomeScrollChat.Utility;

/// <summary>
/// Assigns each NPC speaker name in <see cref="Windows.DialogueTranslationWindow"/> a stable, distinct
/// colour - same permanent-by-construction idea as <see cref="PlayerColorPalette"/> (a pure function of
/// the name via <see cref="ColorHashUtility"/>, so the same NPC always gets the same colour, forever,
/// with nothing to persist to disk), but with a much wider spread: FFXIV's story alone has thousands of
/// named NPCs, and unlike player nicknames (which sit inline with other UI chrome that needs to stay
/// muted) there's no reason to keep this palette as conservative, so hue is bucketed far more finely and
/// saturation/value are varied too - still clamped to a readable-on-dark-background range, just a wider
/// one, for as many practically-distinguishable colours as reasonable rather than 360 hues alone.
/// </summary>
public static class NpcColorPalette
{
    private const uint HueBuckets = 3600;
    private const uint SaturationBuckets = 5;
    private const uint ValueBuckets = 3;

    private static readonly ConcurrentDictionary<string, Vector4> Cache = new();

    public static Vector4 GetColor(string name) => Cache.GetOrAdd(name, BuildColor);

    private static Vector4 BuildColor(string name)
    {
        var hash = ColorHashUtility.Fnv1A(name);

        var hue = (hash % HueBuckets) / (float)HueBuckets;
        var saturationStep = (hash / HueBuckets) % SaturationBuckets;
        var valueStep = (hash / (HueBuckets * SaturationBuckets)) % ValueBuckets;

        var saturation = 0.5f + saturationStep * 0.05f; // 0.50 - 0.70
        var value = 0.85f + valueStep * 0.05f; // 0.85 - 0.95

        return ColorHashUtility.HsvToRgb(hue, saturation, value);
    }
}
