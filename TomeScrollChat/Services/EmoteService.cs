using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using TomeScrollChat.Models;
using TomeScrollChat.Services.Emotes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace TomeScrollChat.Services;

/// <summary>
/// Fetches the BTTV and 7TV *global* emote sets, plus each configured channel's own emotes (see
/// <see cref="Models.EmoteChannelConfig"/> - Settings &gt; Emotes' "Add channel" list), caches both the
/// manifest and the raw emote images on disk (TTL-based refresh), and lazily decodes images into ImGui
/// textures on first use. Dalamud's texture loader has no built-in WebP support, so images are decoded
/// with ImageSharp into raw RGBA32 and handed to
/// <see cref="ITextureProvider.CreateFromRawAsync(RawImageSpecification, ReadOnlyMemory{byte}, string?, CancellationToken)"/>
/// rather than relying on any built-in image format support.
/// </summary>
public sealed class EmoteService : IDisposable
{
    private const string BttvGlobalUrl = "https://api.betterttv.net/3/cached/emotes/global";
    private const string SevenTvGlobalUrl = "https://7tv.io/v3/emote-sets/global";
    private const string BttvChannelUrlFormat = "https://api.betterttv.net/3/cached/users/twitch/{0}";
    private const string SevenTvChannelUrlFormat = "https://7tv.io/v3/users/twitch/{0}";

    // jsdelivr (the primary Twemoji host) is unreachable from some regions, which otherwise leaves
    // every standard-emoji image stuck permanently unloaded - and ChatMessageRenderer.DrawEmote falls
    // back to printing the raw code as plain text when a texture never finishes loading, which looks
    // indistinguishable from "the emote feature doesn't work" even though everything else is fine.
    // Mirrors are tried in order and the first one that succeeds wins.
    //
    // Fixed 2026-08-17: the first mirror used to be "cdn.jsdelivr.net/npm/twemoji@14.0.2/assets/...",
    // which always 404s - confirmed directly (data.jsdelivr.com's own package listing for the npm
    // "twemoji" package) that it only ships the dist/*.js parsing library, no assets/ directory at
    // all, so every single emoji silently fell through to the next mirror. jsdelivr's "/gh/" mode
    // (serve straight from the GitHub repo instead of the npm package) is what actually has the PNGs.
    private static readonly string[] TwemojiMirrors =
    {
        "https://cdn.jsdelivr.net/gh/twitter/twemoji@14.0.2/assets/72x72/{0}.png",
        "https://raw.githubusercontent.com/twitter/twemoji/v14.0.2/assets/72x72/{0}.png",
        "https://cdn.statically.io/gh/twitter/twemoji/v14.0.2/assets/72x72/{0}.png",
    };

    // Same stale-pooled-connection mitigation as GeminiService.http - see its own doc comment
    // (this field lives just as long, so it's exposed to the exact same failure mode).
    private readonly HttpClient http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private readonly ITextureProvider textureProvider;
    private readonly IPluginLog log;
    private readonly string manifestPath;
    private readonly string imageCacheDir;
    private readonly CancellationTokenSource cts = new();

    private readonly ConcurrentDictionary<string, EmoteDefinition> byCode = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IDalamudTextureWrap> textureCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> loading = new(StringComparer.Ordinal);

    /// <summary>Codes whose download failed - <see cref="TryGetTexture"/> stops retrying them for the
    /// rest of this session once added. Added 2026-08-17: <see cref="LoadTextureAsync"/>'s <c>finally</c>
    /// always clears <see cref="loading"/>, success or failure, so without this a permanently-failing
    /// download (e.g. all <see cref="TwemojiMirrors"/> rejecting the same emoji) gets retried - and
    /// re-logged - every single time <see cref="TryGetTexture"/> is called for it, which for something
    /// drawn every ImGui frame (a message marker, a repeatedly-rendered emote) floods <c>/xllog</c>
    /// with the same warning dozens of times a second. Session-scoped, not persisted - a plugin reload
    /// gives every code a fresh chance (mirrors/network conditions can change).</summary>
    private readonly ConcurrentDictionary<string, byte> failed = new(StringComparer.Ordinal);

    public EmoteService(string configDirectory, ITextureProvider textureProvider, IPluginLog log)
    {
        this.textureProvider = textureProvider;
        this.log = log;

        var cacheRoot = Path.Combine(configDirectory, "emote-cache");
        imageCacheDir = Path.Combine(cacheRoot, "images");
        manifestPath = Path.Combine(cacheRoot, "manifest.json");
        Directory.CreateDirectory(imageCacheDir);
    }

    /// <summary>True once at least one successful refresh (from disk cache or network) has populated the emote table.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Loads from disk cache if fresh, otherwise refetches from BTTV/7TV. Safe to call repeatedly - a call while a refresh is already running is ignored.</summary>
    public async Task EnsureLoadedAsync(bool bttvEnabled, bool sevenTvEnabled, IReadOnlyList<EmoteChannelConfig> customChannels, TimeSpan ttl)
    {
        if (TryLoadManifestFromDisk(ttl))
        {
            IsReady = true;
            return;
        }

        await RefreshAsync(bttvEnabled, sevenTvEnabled, customChannels).ConfigureAwait(false);
    }

    public async Task RefreshAsync(bool bttvEnabled, bool sevenTvEnabled, IReadOnlyList<EmoteChannelConfig> customChannels)
    {
        var definitions = new List<EmoteDefinition>();

        // Always included, first - a static list with no manifest fetch of its own (only the images
        // are downloaded on demand, same as every other emote).
        definitions.AddRange(BuildStandardEmojiList());

        if (bttvEnabled)
        {
            try
            {
                definitions.AddRange(await FetchBttvGlobalAsync().ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                log.Warning(ex, "TomeScrollChat: failed to fetch BTTV global emotes");
            }
        }

        if (sevenTvEnabled)
        {
            try
            {
                definitions.AddRange(await FetchSevenTvGlobalAsync().ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                log.Warning(ex, "TomeScrollChat: failed to fetch 7TV global emotes");
            }
        }

        foreach (var channel in customChannels)
        {
            if (string.IsNullOrWhiteSpace(channel.TwitchId))
                continue;

            if (bttvEnabled)
            {
                try
                {
                    definitions.AddRange(await FetchBttvChannelAsync(channel.TwitchId).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "TomeScrollChat: failed to fetch BTTV channel emotes for Twitch id {TwitchId}", channel.TwitchId);
                }
            }

            if (sevenTvEnabled)
            {
                try
                {
                    definitions.AddRange(await FetchSevenTvChannelAsync(channel.TwitchId).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "TomeScrollChat: failed to fetch 7TV channel emotes for Twitch id {TwitchId}", channel.TwitchId);
                }
            }
        }

        if (definitions.Count == 0)
            return;

        byCode.Clear();
        foreach (var def in definitions)
            byCode[def.Code] = def;

        // Invalidate any textures created under a now-stale definition (id/url may have changed).
        foreach (var tex in textureCache.Values)
            tex.Dispose();
        textureCache.Clear();

        IsReady = true;
        SaveManifestToDisk(definitions);
    }

    private async Task<List<EmoteDefinition>> FetchBttvGlobalAsync()
    {
        var json = await http.GetStringAsync(BttvGlobalUrl, cts.Token).ConfigureAwait(false);
        var entries = JsonSerializer.Deserialize<List<BttvEmoteDto>>(json) ?? new();
        return entries
            .Where(e => !string.IsNullOrEmpty(e.Code) && !string.IsNullOrEmpty(e.Id))
            .Select(e => new EmoteDefinition
            {
                Code = e.Code,
                Id = e.Id,
                // Documented BTTV CDN convention (their API response has no URL field to read this from).
                ImageUrl = $"https://cdn.betterttv.net/emote/{e.Id}/2x.{e.ImageType}",
                Provider = EmoteProvider.Bttv,
            })
            .ToList();
    }

    /// <summary>BTTV's per-channel emotes (both the channel's own uploads and ones its owner shared in
    /// from elsewhere - see <see cref="BttvChannelResponseDto"/>'s own doc comment), keyed by numeric
    /// Twitch id per <see cref="EmoteChannelConfig"/>'s own doc comment on why not a channel name.</summary>
    private async Task<List<EmoteDefinition>> FetchBttvChannelAsync(string twitchId)
    {
        var json = await http.GetStringAsync(string.Format(BttvChannelUrlFormat, Uri.EscapeDataString(twitchId)), cts.Token).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<BttvChannelResponseDto>(json);
        if (response == null)
            return new List<EmoteDefinition>();

        return response.ChannelEmotes.Concat(response.SharedEmotes)
            .Where(e => !string.IsNullOrEmpty(e.Code) && !string.IsNullOrEmpty(e.Id))
            .Select(e => new EmoteDefinition
            {
                Code = e.Code,
                Id = e.Id,
                ImageUrl = $"https://cdn.betterttv.net/emote/{e.Id}/2x.{e.ImageType}",
                Provider = EmoteProvider.Bttv,
            })
            .ToList();
    }

    /// <summary>7TV's per-channel emote set, keyed by numeric Twitch id - see
    /// <see cref="SevenTvUserConnectionDto"/>'s own doc comment.</summary>
    private async Task<List<EmoteDefinition>> FetchSevenTvChannelAsync(string twitchId)
    {
        var json = await http.GetStringAsync(string.Format(SevenTvChannelUrlFormat, Uri.EscapeDataString(twitchId)), cts.Token).ConfigureAwait(false);
        var connection = JsonSerializer.Deserialize<SevenTvUserConnectionDto>(json);
        var emotes = connection?.EmoteSet?.Emotes;
        if (emotes == null)
            return new List<EmoteDefinition>();

        var result = new List<EmoteDefinition>();
        foreach (var emote in emotes)
        {
            var host = emote.Data?.Host;
            if (host == null || host.Files.Count == 0 || string.IsNullOrEmpty(emote.Name) || string.IsNullOrEmpty(emote.Id))
                continue;

            var file = host.Files.FirstOrDefault(f => f.Name.StartsWith("2x", StringComparison.OrdinalIgnoreCase))
                       ?? host.Files.First();

            var baseUrl = host.Url.StartsWith("//") ? $"https:{host.Url}" : host.Url;
            result.Add(new EmoteDefinition
            {
                Code = emote.Name,
                Id = emote.Id,
                ImageUrl = $"{baseUrl}/{file.Name}",
                Provider = EmoteProvider.SevenTv,
            });
        }

        return result;
    }

    /// <summary><see cref="EmoteDefinition.ImageUrl"/> is never actually read for
    /// <see cref="EmoteProvider.Standard"/> entries - <see cref="GetImageBytesAsync"/> routes those
    /// through <see cref="DownloadFromMirrorsAsync"/>/<see cref="TwemojiMirrors"/> instead, same as
    /// every other standard emoji. Still filled in (pointing at the first mirror, now that it's fixed
    /// to actually work - see <see cref="TwemojiMirrors"/>'s own doc comment) rather than left at
    /// whatever placeholder, purely so the persisted manifest doesn't carry a misleading/dead URL for
    /// no reason (found during a full-codebase cleanup audit, 2026-08-17 - it previously pointed at the
    /// npm-package path confirmed dead for every emoji).</summary>
    private static List<EmoteDefinition> BuildStandardEmojiList() =>
        StandardEmojiCatalog.Entries
            .Select(e => new EmoteDefinition
            {
                Code = e.Code,
                Id = e.Codepoint,
                ImageUrl = string.Format(TwemojiMirrors[0], e.Codepoint),
                Provider = EmoteProvider.Standard,
            })
            .ToList();

    private async Task<List<EmoteDefinition>> FetchSevenTvGlobalAsync()
    {
        var json = await http.GetStringAsync(SevenTvGlobalUrl, cts.Token).ConfigureAwait(false);
        var set = JsonSerializer.Deserialize<SevenTvEmoteSetDto>(json);
        var result = new List<EmoteDefinition>();
        if (set == null)
            return result;

        foreach (var emote in set.Emotes)
        {
            var host = emote.Data?.Host;
            if (host == null || host.Files.Count == 0 || string.IsNullOrEmpty(emote.Name) || string.IsNullOrEmpty(emote.Id))
                continue;

            // Prefer a "2x" webp for a reasonable balance of quality vs. download size; fall back to whatever's first.
            var file = host.Files.FirstOrDefault(f => f.Name.StartsWith("2x", StringComparison.OrdinalIgnoreCase))
                       ?? host.Files.First();

            var baseUrl = host.Url.StartsWith("//") ? $"https:{host.Url}" : host.Url;
            result.Add(new EmoteDefinition
            {
                Code = emote.Name,
                Id = emote.Id,
                ImageUrl = $"{baseUrl}/{file.Name}",
                Provider = EmoteProvider.SevenTv,
            });
        }

        return result;
    }

    /// <summary>Non-blocking: returns the texture if already decoded, otherwise kicks off a background
    /// download+decode (from disk cache or network) and returns null for this call.</summary>
    public IDalamudTextureWrap? TryGetTexture(string code)
    {
        if (textureCache.TryGetValue(code, out var texture))
            return texture;

        if (failed.ContainsKey(code))
            return null;

        if (!byCode.TryGetValue(code, out var def))
            return null;

        if (!loading.TryAdd(code, 0))
            return null;

        _ = LoadTextureAsync(def);
        return null;
    }

    public bool IsKnownEmote(string code) => byCode.ContainsKey(code);

    /// <summary>Every currently-loaded emote, standard/Windows-style emoji first, then alphabetical
    /// within each provider - used by both the settings "loaded emotes" list and the emote pickers.</summary>
    public IReadOnlyList<EmoteDefinition> GetLoadedEmotes() =>
        byCode.Values
            .OrderBy(e => e.Provider == EmoteProvider.Standard ? 0 : 1)
            .ThenBy(e => e.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task LoadTextureAsync(EmoteDefinition def)
    {
        try
        {
            var bytes = await GetImageBytesAsync(def).ConfigureAwait(false);
            if (bytes == null)
                return;

            using var image = Image.Load<Rgba32>(bytes);
            var pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);

            var wrap = await textureProvider
                .CreateFromRawAsync(RawImageSpecification.Rgba32(image.Width, image.Height), pixels, $"TomeScrollChat emote {def.Code}", cts.Token)
                .ConfigureAwait(false);

            textureCache[def.Code] = wrap;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to load emote texture for {Code}", def.Code);
            failed.TryAdd(def.Code, 0);
        }
        finally
        {
            loading.TryRemove(def.Code, out _);
        }
    }

    private async Task<byte[]?> GetImageBytesAsync(EmoteDefinition def)
    {
        var cachePath = Path.Combine(imageCacheDir, $"{def.Provider}_{def.Id}");
        if (File.Exists(cachePath))
        {
            try
            {
                return await File.ReadAllBytesAsync(cachePath, cts.Token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Fall through to re-download.
            }
        }

        var bytes = def.Provider == EmoteProvider.Standard
            ? await DownloadFromMirrorsAsync(def).ConfigureAwait(false)
            : await http.GetByteArrayAsync(def.ImageUrl, cts.Token).ConfigureAwait(false);

        try
        {
            await File.WriteAllBytesAsync(cachePath, bytes, cts.Token).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to write emote image cache for {Code}", def.Code);
        }

        return bytes;
    }

    /// <summary>Tries every Twemoji mirror in order (def.Id is the raw hex codepoint for standard
    /// emoji) and returns the first successful download - see <see cref="TwemojiMirrors"/> for why.</summary>
    private async Task<byte[]> DownloadFromMirrorsAsync(EmoteDefinition def)
    {
        Exception? lastError = null;
        foreach (var mirror in TwemojiMirrors)
        {
            try
            {
                return await http.GetByteArrayAsync(string.Format(mirror, def.Id), cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException($"No Twemoji mirrors configured for {def.Code}");
    }

    private bool TryLoadManifestFromDisk(TimeSpan ttl)
    {
        try
        {
            if (!File.Exists(manifestPath))
                return false;

            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(manifestPath) > ttl)
                return false;

            var json = File.ReadAllText(manifestPath);
            var entries = JsonSerializer.Deserialize<List<ManifestEntry>>(json);
            if (entries == null || entries.Count == 0)
                return false;

            byCode.Clear();
            foreach (var e in entries)
                byCode[e.Code] = new EmoteDefinition { Code = e.Code, Id = e.Id, ImageUrl = e.ImageUrl, Provider = e.Provider };

            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to load cached emote manifest");
            return false;
        }
    }

    private void SaveManifestToDisk(List<EmoteDefinition> definitions)
    {
        try
        {
            var entries = definitions.Select(d => new ManifestEntry { Code = d.Code, Id = d.Id, ImageUrl = d.ImageUrl, Provider = d.Provider });
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(entries));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to save emote manifest cache");
        }
    }

    private sealed class ManifestEntry
    {
        public string Code { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
        public EmoteProvider Provider { get; set; }
    }

    public void Dispose()
    {
        cts.Cancel();
        foreach (var tex in textureCache.Values)
            tex.Dispose();
        textureCache.Clear();
        http.Dispose();
        cts.Dispose();
    }
}
