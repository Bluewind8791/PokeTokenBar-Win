using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using PokeTokenBar.Core;
using Drawing = System.Drawing;
using Imaging = System.Drawing.Imaging;

namespace PokeTokenBar.App;

/// <summary>
/// Port of <c>SpriteStore</c> (SpriteLoader.swift): PokéAPI sprite bytes, cached on disk under
/// <c>StateDirectory\sprites</c> with the same file names as macOS, and in memory (LRU 64).
/// </summary>
public sealed class SpriteStore
{
    public static readonly SpriteStore Shared = new();

    private const string Base = "https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon";
    private const string ItemBase = "https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/items";
    private const int MemLimit = 64;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _mem = new();
    private readonly LinkedList<string> _order = new();

    public string Directory { get; }

    public SpriteStore(string? directory = null)
    {
        Directory = directory ?? Path.Combine(AppEnv.StateDirectory(), "sprites");
        System.IO.Directory.CreateDirectory(Directory);
    }

    public static string CacheKey(int speciesID, bool animated, bool shiny, PokemonForm? form = null) =>
        $"{PokemonForm.AssetName(speciesID, form)}-{(shiny ? "sh" : "")}{(animated ? "a" : "s")}";

    public static string SpriteUrl(int speciesID, bool animated, bool shiny, PokemonForm? form = null)
    {
        var name = animated ? PokemonForm.AssetName(speciesID, form) : PokemonForm.StaticAssetName(speciesID, form);
        var path = animated ? "versions/generation-v/black-white/animated/" : "";
        var color = shiny ? "shiny/" : "";
        return $"{Base}/{path}{color}{name}.{(animated ? "gif" : "png")}";
    }

    public Task<byte[]?> Data(int speciesID, bool animated, bool shiny = false, PokemonForm? form = null)
    {
        if (animated && !PokemonAssets.HasAnimatedSprite(speciesID)) return Task.FromResult<byte[]?>(null);
        var key = CacheKey(speciesID, animated, shiny, form);
        return Fetch(key, $"{key}.{(animated ? "gif" : "png")}", SpriteUrl(speciesID, animated, shiny, form));
    }

    public Task<byte[]?> ItemData(string itemName) =>
        Fetch($"item-{itemName}", $"item-{itemName}.png", $"{ItemBase}/{itemName}.png");

    public Task<byte[]?> EggData() => Fetch("egg", "egg.png", $"{Base}/egg.png");

    public string FilePath(string fileName) => Path.Combine(Directory, fileName);

    private async Task<byte[]?> Fetch(string key, string fileName, string url)
    {
        lock (_gate)
            if (_mem.TryGetValue(key, out var hit)) { Touch(key); return hit; }
        var file = FilePath(fileName);
        try
        {
            if (File.Exists(file)) { var d = await File.ReadAllBytesAsync(file); Remember(key, d); return d; }
        }
        catch (IOException) { }
        try
        {
            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0) return null;
            // Atomic write: a crash mid-write must not leave a corrupt cached sprite behind.
            // Unique temp name: two concurrent fetches of the same key must not write one temp file.
            var tmp = $"{file}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, file, overwrite: true);
            Remember(key, bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    private void Remember(string key, byte[] data)
    {
        lock (_gate)
        {
            _mem[key] = data;
            Touch(key);
            while (_order.Count > MemLimit)
            {
                _mem.Remove(_order.First!.Value);
                _order.RemoveFirst();
            }
        }
    }

    private void Touch(string key)
    {
        _order.Remove(key);
        _order.AddLast(key);
    }
}

/// <summary>One decoded animation frame.</summary>
public sealed record SpriteFrame(Drawing.Bitmap Image, TimeSpan Delay);

/// <summary>Port of <c>GIFDecoder</c> (SpriteAnimation.swift) on GDI+, which composites GIF frames.</summary>
public static class GifDecoder
{
    private const int FrameDelayProperty = 0x5100;

    public static List<SpriteFrame> Frames(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var gif = Drawing.Image.FromStream(ms);
            var dim = new Imaging.FrameDimension(gif.FrameDimensionsList[0]);
            var count = gif.GetFrameCount(dim);
            if (count <= 1) return [];
            var delays = gif.PropertyIdList.Contains(FrameDelayProperty) ? gif.GetPropertyItem(FrameDelayProperty)?.Value : null;
            var output = new List<SpriteFrame>(count);
            for (var i = 0; i < count; i++)
            {
                gif.SelectActiveFrame(dim, i);
                var frame = new Drawing.Bitmap(gif.Width, gif.Height, Imaging.PixelFormat.Format32bppArgb);
                using (var g = Drawing.Graphics.FromImage(frame)) g.DrawImage(gif, 0, 0, gif.Width, gif.Height);
                // Delay is in 1/100 s; Swift treats < 0.02 s as 0.1 s (browsers do the same).
                var centis = delays != null && delays.Length >= (i + 1) * 4 ? BitConverter.ToInt32(delays, i * 4) : 10;
                var seconds = centis / 100.0;
                output.Add(new SpriteFrame(frame, TimeSpan.FromSeconds(seconds < 0.02 ? 0.1 : seconds)));
            }
            return output;
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            return [];
        }
    }

    /// <summary>
    /// Merges consecutive frames until each lasts at least <paramref name="floor"/>, keeping total
    /// duration (playback speed). Same rule and epsilon as Swift's <c>capFrameRate</c>.
    /// </summary>
    public static List<SpriteFrame> CapFrameRate(IReadOnlyList<SpriteFrame> frames, TimeSpan floor)
    {
        if (floor <= TimeSpan.Zero || frames.Count <= 1) return frames.ToList();
        var output = new List<SpriteFrame>();
        Drawing.Bitmap? held = null;
        double acc = 0;
        foreach (var f in frames)
        {
            held ??= f.Image;
            acc += f.Delay.TotalSeconds;
            if (acc + 1e-9 >= floor.TotalSeconds)
            {
                output.Add(new SpriteFrame(held, TimeSpan.FromSeconds(acc)));
                held = null;
                acc = 0;
            }
        }
        if (held != null && acc > 0)
        {
            if (output.Count == 0) output.Add(new SpriteFrame(held, TimeSpan.FromSeconds(acc)));
            else output[^1] = output[^1] with { Delay = output[^1].Delay + TimeSpan.FromSeconds(acc) };
        }
        return output;
    }
}

/// <summary>Bitmap helpers shared by the tray and the WPF views.</summary>
public static class SpriteImages
{
    public static Drawing.Bitmap? Decode(byte[]? data)
    {
        if (data == null) return null;
        try
        {
            using var ms = new MemoryStream(data);
            using var img = Drawing.Image.FromStream(ms);
            return new Drawing.Bitmap(img);
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Crops to the non-transparent bounding box, squared on the long side (Swift <c>cropToContent</c>).</summary>
    public static Drawing.Bitmap CropToContent(Drawing.Bitmap image)
    {
        int w = image.Width, h = image.Height, minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (image.GetPixel(x, y).A > 2)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
        if (maxX < minX || maxY < minY) return image;
        int bw = maxX - minX + 1, bh = maxY - minY + 1;
        var side = Math.Min(Math.Max(bw, bh), Math.Min(w, h));
        var sx = Math.Max(0, Math.Min(minX - (side - bw) / 2, w - side));
        var sy = Math.Max(0, Math.Min(minY - (side - bh) / 2, h - side));
        return image.Clone(new Drawing.Rectangle(sx, sy, side, side), image.PixelFormat);
    }

    public static BitmapSource ToBitmapSource(Drawing.Bitmap bmp)
    {
        var data = bmp.LockBits(new Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
            Imaging.ImageLockMode.ReadOnly, Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, data.Scan0, data.Stride * bmp.Height, data.Stride);
            src.Freeze();
            return src;
        }
        finally { bmp.UnlockBits(data); }
    }
}
