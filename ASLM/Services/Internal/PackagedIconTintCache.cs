// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Storage;
using SkiaSharp;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Builds tinted PNG <see cref="ImageSource"/> streams (sidebar icons: bundled names or absolute paths).
    /// </summary>
    internal static class PackagedIconTintCache
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.Ordinal);


        // Cache lifecycle

        /// <summary>
        /// Clears every cached tinted image so palette changes can rebuild icons.
        /// </summary>
        internal static void Clear()
        {
            lock (Gate)
            {
                Cache.Clear();
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
                if (input is null)
                {
                    return ImageSource.FromFile(fileNameOrPath);
                }

                using var sk = SKBitmap.Decode(input);
                if (sk is null)
                {
                    return ImageSource.FromFile(fileNameOrPath);
                }

                using var painted = new SKBitmap(sk.Width, sk.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var canvas = new SKCanvas(painted);
                var skTint = new SKColor(
                    (byte)(tint.Red * 255),
                    (byte)(tint.Green * 255),
                    (byte)(tint.Blue * 255),
                    (byte)(tint.Alpha * 255));

                using var paint = new SKPaint { IsAntialias = true };
                paint.ColorFilter = SKColorFilter.CreateBlendMode(skTint, SKBlendMode.Modulate);
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(sk, 0, 0, paint);

                using var image = SKImage.FromBitmap(painted);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                var bytes = data.ToArray();

                return ImageSource.FromStream(() => new MemoryStream(bytes));
            }
            catch
            {
                return ImageSource.FromFile(fileNameOrPath);
            }
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
