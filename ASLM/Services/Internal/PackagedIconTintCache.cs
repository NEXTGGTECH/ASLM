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
        private static readonly object Gate = new();
        private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.Ordinal);
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
        internal static ImageSource Get(string fileNameOrPath, Color tint)
        {
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
        internal static ImageSource Get(byte[] pngBytes, Color tint)
        {
            var key = ColorToKey(tint);
            lock (Gate)
            {
                var colors = DataCache.GetOrCreateValue(pngBytes);
                if (colors.TryGetValue(key, out var existing)) return existing;

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

        private static ImageSource? CreateImageSource(Stream? input, Color tint, SKBlendMode blendMode)
        {
            if (input == null) return null;
            using var sk = SKBitmap.Decode(input);
            if (sk == null) return null;

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
            if (Path.IsPathRooted(fileNameOrPath) && File.Exists(fileNameOrPath))
            {
                return File.OpenRead(fileNameOrPath);
            }

            var logicalName = Path.GetFileName(fileNameOrPath);
            if (!string.IsNullOrEmpty(logicalName))
            {
                var tintPath = Path.Combine(AppContext.BaseDirectory, "tint_png", logicalName);
                if (File.Exists(tintPath))
                {
                    return File.OpenRead(tintPath);
                }
            }

            try
            {
                return FileSystem.Current.OpenAppPackageFileAsync(fileNameOrPath).GetAwaiter().GetResult();
            }
            catch
            {
            }

            var candidate = Path.Combine(AppContext.BaseDirectory, fileNameOrPath);
            return File.Exists(candidate) ? File.OpenRead(candidate) : null;
        }
    }
}
