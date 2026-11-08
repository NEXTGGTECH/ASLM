// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Storage;
using System.Runtime.CompilerServices;
using SkiaSharp;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Builds tinted PNG streams from packaged files, paths, or in-memory module icons.
    /// </summary>
    internal static class PackagedIconTintCache
    {
        // Cache state

        /// <summary>
        /// Guards both caches; all reads, writes and image creation happen under this lock.
        /// </summary>
        private static readonly object Gate = new();

        /// <summary>
        /// Tinted images for file-based icons, keyed by file name or path together with the tint color.
        /// </summary>
        private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.Ordinal);

        /// <summary>
        /// Tinted images for in-memory icons: per icon byte array, one image per tint color key.
        /// Entries disappear with the byte array, so the cache never keeps released icon data alive.
        /// </summary>
        private static readonly ConditionalWeakTable<byte[], Dictionary<string, ImageSource>> DataCache = new();


        // Cache lifecycle

        /// <summary>
        /// Clears every cached tinted image so palette changes can rebuild icons.
        /// </summary>
        internal static void Clear()
        {
            lock (Gate)
            {
                Cache.Clear();
                DataCache.Clear();
            }
        }


        // Cache access

        /// <summary>
        /// Returns a cached tinted image for the requested file and color, creating it when needed.
        /// </summary>
        /// <param name="fileNameOrPath">Packaged icon file name or absolute path of a PNG on disk.</param>
        /// <param name="tint">Color multiplied into the icon pixels.</param>
        internal static ImageSource Get(string fileNameOrPath, Color tint)
        {
            // The unit separator character keeps file name and color from running together in the key.
            var key = $"{fileNameOrPath}\u001f{ColorToKey(tint)}";
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                var created = CreateImageSource(fileNameOrPath, tint);
                Cache[key] = created;
                return created;
            }
        }

        /// <summary>
        /// Applies a solid color through a module icon's alpha mask, sharing results for each tint.
        /// The cache does not retain icon data after the owning catalog releases it.
        /// </summary>
        /// <param name="pngBytes">PNG data of the icon; the array instance identifies the icon in the cache.</param>
        /// <param name="tint">Color that replaces the icon color while the icon's alpha channel is kept.</param>
        internal static ImageSource Get(byte[] pngBytes, Color tint)
        {
            // Look up the per-icon table by array identity, then the image by tint color.
            var key = ColorToKey(tint);
            lock (Gate)
            {
                var colors = DataCache.GetOrCreateValue(pngBytes);
                if (colors.TryGetValue(key, out var existing)) return existing;

                // SrcIn keeps only the icon's alpha mask; if decoding fails, show the untinted icon.
                using var input = new MemoryStream(pngBytes, writable: false);
                var created = CreateImageSource(input, tint, SKBlendMode.SrcIn)
                    ?? ImageSource.FromStream(() => new MemoryStream(pngBytes, writable: false));
                colors[key] = created;
                return created;
            }
        }


        // Image creation

        /// <summary>
        /// Builds a stable cache key fragment from one tint color.
        /// </summary>
        private static string ColorToKey(Color c) =>
            FormattableString.Invariant($"{c.Red:F4}|{c.Green:F4}|{c.Blue:F4}|{c.Alpha:F4}");

        /// <summary>
        /// Decodes one icon, applies the tint, and returns a stream-backed image source.
        /// </summary>
        private static ImageSource CreateImageSource(string fileNameOrPath, Color tint)
        {
            // Modulate multiplies the tint into the icon pixels; any failure falls back to the untinted file.
            try
            {
                using var input = OpenImageStream(fileNameOrPath);
                return CreateImageSource(input, tint, SKBlendMode.Modulate)
                    ?? ImageSource.FromFile(fileNameOrPath);
            }
            catch
            {
                return ImageSource.FromFile(fileNameOrPath);
            }
        }

        /// <summary>
        /// Paints the decoded bitmap through a color filter of the given tint and blend mode and returns the
        /// result as a PNG-backed image source, or null when there is no stream or it cannot be decoded.
        /// </summary>
        /// <param name="input">Stream with the source PNG; may be null.</param>
        /// <param name="tint">Color used by the blend filter.</param>
        /// <param name="blendMode">How the tint combines with the source pixels.</param>
        private static ImageSource? CreateImageSource(Stream? input, Color tint, SKBlendMode blendMode)
        {
            if (input == null) return null;
            using var sk = SKBitmap.Decode(input);
            if (sk == null) return null;

            // Draw the source into a new transparent bitmap with the blend filter applied.
            using var painted = new SKBitmap(sk.Width, sk.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(painted);
            var skTint = new SKColor(
                (byte)(tint.Red * 255),
                (byte)(tint.Green * 255),
                (byte)(tint.Blue * 255),
                (byte)(tint.Alpha * 255));

            using var paint = new SKPaint { IsAntialias = true };
            paint.ColorFilter = SKColorFilter.CreateBlendMode(skTint, blendMode);
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(sk, 0, 0, paint);

            // Encode once; every stream the image source opens afterwards reads the same bytes.
            using var image = SKImage.FromBitmap(painted);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var bytes = data.ToArray();
            return ImageSource.FromStream(() => new MemoryStream(bytes, writable: false));
        }

        /// <summary>
        /// Opens the packaged or on-disk PNG stream used as the tint source bitmap.
        /// </summary>
        private static Stream? OpenImageStream(string fileNameOrPath)
        {
            // An existing absolute path is used directly.
            if (Path.IsPathRooted(fileNameOrPath) && File.Exists(fileNameOrPath))
            {
                return File.OpenRead(fileNameOrPath);
            }

            // Prefer the PNG copy shipped in the tint_png folder next to the executable.
            var logicalName = Path.GetFileName(fileNameOrPath);
            if (!string.IsNullOrEmpty(logicalName))
            {
                var tintPath = Path.Combine(AppContext.BaseDirectory, "tint_png", logicalName);
                if (File.Exists(tintPath))
                {
                    return File.OpenRead(tintPath);
                }
            }

            // Then try the MAUI app package; the lookup throws when the file is not part of it.
            try
            {
                return FileSystem.Current.OpenAppPackageFileAsync(fileNameOrPath).GetAwaiter().GetResult();
            }
            catch
            {
                // Not in the app package: continue with the file next to the executable.
            }

            // Last resort: a path relative to the application directory.
            var candidate = Path.Combine(AppContext.BaseDirectory, fileNameOrPath);
            return File.Exists(candidate) ? File.OpenRead(candidate) : null;
        }
    }
}
