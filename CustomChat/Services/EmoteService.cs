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
using CustomChat.Models;
using CustomChat.Services.Emotes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CustomChat.Services;

/// <summary>
/// Fetches the BTTV and 7TV *global* emote sets (v1 scope - no per-channel emotes yet), caches
/// both the manifest and the raw emote images on disk (TTL-based refresh), and lazily decodes
/// images into ImGui textures on first use. Dalamud's texture loader has no built-in WebP support,
/// so images are decoded with ImageSharp into raw RGBA32 and handed to
/// <see cref="ITextureProvider.CreateFromRawAsync(RawImageSpecification, ReadOnlyMemory{byte}, string?, CancellationToken)"/>
/// rather than relying on any built-in image format support.
/// </summary>
public sealed class EmoteService : IDisposable
{
    private const string BttvGlobalUrl = "https://api.betterttv.net/3/cached/emotes/global";
    private const string SevenTvGlobalUrl = "https://7tv.io/v3/emote-sets/global";

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ITextureProvider textureProvider;
    private readonly IPluginLog log;
    private readonly string manifestPath;
    private readonly string imageCacheDir;
    private readonly CancellationTokenSource cts = new();

    private readonly ConcurrentDictionary<string, EmoteDefinition> byCode = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IDalamudTextureWrap> textureCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> loading = new(StringComparer.Ordinal);

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
    public async Task EnsureLoadedAsync(bool bttvEnabled, bool sevenTvEnabled, TimeSpan ttl)
    {
        if (TryLoadManifestFromDisk(ttl))
        {
            IsReady = true;
            return;
        }

        await RefreshAsync(bttvEnabled, sevenTvEnabled).ConfigureAwait(false);
    }

    public async Task RefreshAsync(bool bttvEnabled, bool sevenTvEnabled)
    {
        var definitions = new List<EmoteDefinition>();

        if (bttvEnabled)
        {
            try
            {
                definitions.AddRange(await FetchBttvGlobalAsync().ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                log.Warning(ex, "CustomChat: failed to fetch BTTV global emotes");
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
                log.Warning(ex, "CustomChat: failed to fetch 7TV global emotes");
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

        if (!byCode.TryGetValue(code, out var def))
            return null;

        if (!loading.TryAdd(code, 0))
            return null;

        _ = LoadTextureAsync(def);
        return null;
    }

    public bool IsKnownEmote(string code) => byCode.ContainsKey(code);

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
                .CreateFromRawAsync(RawImageSpecification.Rgba32(image.Width, image.Height), pixels, $"CustomChat emote {def.Code}", cts.Token)
                .ConfigureAwait(false);

            textureCache[def.Code] = wrap;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to load emote texture for {Code}", def.Code);
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

        var bytes = await http.GetByteArrayAsync(def.ImageUrl, cts.Token).ConfigureAwait(false);
        try
        {
            await File.WriteAllBytesAsync(cachePath, bytes, cts.Token).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            log.Warning(ex, "CustomChat: failed to write emote image cache for {Code}", def.Code);
        }

        return bytes;
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
            log.Warning(ex, "CustomChat: failed to load cached emote manifest");
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
            log.Warning(ex, "CustomChat: failed to save emote manifest cache");
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
