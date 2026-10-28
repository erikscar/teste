using OpenCvSharp;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Macro.Services;

public sealed record RelativeSearchRegion(double X, double Y, double Width, double Height);
public sealed record TemplateSearchResult(bool Found, double Confidence, OpenCvSharp.Rect Bounds, BitmapSource AnnotatedFrame);

public sealed class TemplateSearchService
{
    // Os templates e as escalas são os mesmos a cada frame. Mantê-los em memória
    // evita reler PNGs e redimensionar dezenas de vezes durante cada busca.
    private static readonly ConcurrentDictionary<TemplateCacheKey, Lazy<IReadOnlyList<TemplateVariant>>> TemplateCache = new();

    public Task<TemplateSearchResult> FindAsync(BitmapSource frame, string templatePath,
        RelativeSearchRegion region, double threshold, CancellationToken token, Int32Rect? templateCrop = null, bool matchColor = false) =>
        Task.Run(() => Find(frame, templatePath, region, threshold, token, templateCrop, matchColor), token);

    private static TemplateSearchResult Find(BitmapSource frame, string templatePath,
        RelativeSearchRegion region, double threshold, CancellationToken token, Int32Rect? templateCrop, bool matchColor)
    {
        Validate(region, threshold);
        using var color = ToBgr(frame);
        using var gray = new Mat();
        if (matchColor) color.CopyTo(gray); else Cv2.CvtColor(color, gray, ColorConversionCodes.BGR2GRAY);
        var fullPath = Path.GetFullPath(templatePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists) throw new FileNotFoundException("Não foi possível abrir a imagem selecionada.", fullPath);
        var key = new TemplateCacheKey(fullPath, file.Length, file.LastWriteTimeUtc.Ticks,
            templateCrop?.X ?? -1, templateCrop?.Y ?? -1, templateCrop?.Width ?? -1, templateCrop?.Height ?? -1, matchColor);
        var variants = TemplateCache.GetOrAdd(key, cacheKey => new Lazy<IReadOnlyList<TemplateVariant>>(
            () => LoadVariants(cacheKey), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        token.ThrowIfCancellationRequested();

        var x = Math.Clamp((int)Math.Round(region.X * gray.Width), 0, gray.Width - 1);
        var y = Math.Clamp((int)Math.Round(region.Y * gray.Height), 0, gray.Height - 1);
        var right = Math.Clamp((int)Math.Round((region.X + region.Width) * gray.Width), x + 1, gray.Width);
        var bottom = Math.Clamp((int)Math.Round((region.Y + region.Height) * gray.Height), y + 1, gray.Height);
        var roi = new OpenCvSharp.Rect(x, y, right - x, bottom - y);
        using var roiMat = new Mat(gray, roi);
        var found = false;
        double confidence = double.NegativeInfinity;
        var bounds = new OpenCvSharp.Rect();
        // Preserva a faixa e os passos de escala originais; somente os templates
        // pré-processados são reutilizados entre frames.
        foreach (var variant in variants)
        {
            token.ThrowIfCancellationRequested();
            var scaledTemplate = variant.Template;
            var width = scaledTemplate.Width;
            var height = scaledTemplate.Height;
            if (width > roi.Width || height > roi.Height) continue;
            using var scores = new Mat();
            if (variant.Mask is { } mask)
                Cv2.MatchTemplate(roiMat, scaledTemplate, scores, TemplateMatchModes.CCorrNormed, mask);
            else
                Cv2.MatchTemplate(roiMat, scaledTemplate, scores, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(scores, out _, out var max, out _, out var location);
            if (matchColor)
            {
                // Continua procurando quando o melhor formato tem a cor errada.
                for (var candidate = 0; candidate < 100 && double.IsFinite(max) && max >= threshold; candidate++)
                {
                    token.ThrowIfCancellationRequested();
                    using var patch = new Mat(roiMat, new OpenCvSharp.Rect(location.X, location.Y, width, height));
                    if (HasMatchingHue(scaledTemplate, patch, variant.Mask)) break;
                    scores.Set<float>(location.Y, location.X, -1);
                    Cv2.MinMaxLoc(scores, out _, out max, out _, out location);
                    if (candidate == 99) max = 0;
                }
            }
            if (double.IsFinite(max) && max > confidence)
            {
                confidence = max;
                bounds = new(location.X + x, location.Y + y, width, height);
            }
        }
        if (!double.IsFinite(confidence)) confidence = 0;
        found = confidence >= threshold;

        Cv2.Rectangle(color, roi, Scalar.DodgerBlue, 2);
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            var markerColor = found ? Scalar.LimeGreen : Scalar.OrangeRed;
            Cv2.Rectangle(color, bounds, markerColor, 2);
            Cv2.Circle(color, new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), 4, Scalar.Red, -1);
            Cv2.PutText(color, $"{confidence:0.00} / {threshold:0.00}", new(bounds.X, Math.Max(18, bounds.Y - 6)),
                HersheyFonts.HersheySimplex, 0.6, markerColor, 2);
        }
        token.ThrowIfCancellationRequested();
        using var bgra = new Mat();
        Cv2.CvtColor(color, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[bgra.Rows * bgra.Cols * 4];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        var bitmap = BitmapSource.Create(bgra.Cols, bgra.Rows, 96, 96, PixelFormats.Bgra32,
            null, pixels, bgra.Cols * 4);
        bitmap.Freeze();
        return new(found, confidence, bounds, bitmap);
    }

    private static bool HasMatchingHue(Mat template, Mat candidate, Mat? mask)
    {
        using var expected = new Mat();
        using var actual = new Mat();
        Cv2.CvtColor(template, expected, ColorConversionCodes.BGR2HSV);
        Cv2.CvtColor(candidate, actual, ColorConversionCodes.BGR2HSV);
        var colored = 0;
        var matched = 0;
        for (var y = 0; y < expected.Rows; y++)
        for (var x = 0; x < expected.Cols; x++)
        {
            if (mask is not null && mask.At<byte>(y, x) == 0) continue;
            var a = expected.At<Vec3b>(y, x);
            if (a.Item1 < 70 || a.Item2 < 60) continue;
            colored++;
            var b = actual.At<Vec3b>(y, x);
            var difference = Math.Abs(a.Item0 - b.Item0);
            if (b.Item1 >= 50 && b.Item2 >= 40 && Math.Min(difference, 180 - difference) <= 15)
                matched++;
        }
        return colored >= 8 && matched >= colored * 0.75;
    }

    private static IReadOnlyList<TemplateVariant> LoadVariants(TemplateCacheKey key)
    {
        using var sourceTemplate = Cv2.ImRead(key.Path, ImreadModes.Unchanged);
        if (sourceTemplate.Empty()) throw new InvalidOperationException("Não foi possível abrir a imagem selecionada.");
        using var originalTemplate = new Mat();
        using var originalMask = new Mat();
        if (sourceTemplate.Channels() == 4)
        {
            Cv2.CvtColor(sourceTemplate, originalTemplate, key.MatchColor ? ColorConversionCodes.BGRA2BGR : ColorConversionCodes.BGRA2GRAY);
            Cv2.ExtractChannel(sourceTemplate, originalMask, 3);
            Cv2.Threshold(originalMask, originalMask, 0, 255, ThresholdTypes.Binary);
        }
        else if (sourceTemplate.Channels() == 3)
            { if (key.MatchColor) sourceTemplate.CopyTo(originalTemplate); else Cv2.CvtColor(sourceTemplate, originalTemplate, ColorConversionCodes.BGR2GRAY); }
        else
            sourceTemplate.CopyTo(originalTemplate);

        var crop = key.CropX < 0
            ? new Int32Rect(0, 0, originalTemplate.Width, originalTemplate.Height)
            : new Int32Rect(key.CropX, key.CropY, key.CropWidth, key.CropHeight);
        if (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0 ||
            crop.X + crop.Width > originalTemplate.Width || crop.Y + crop.Height > originalTemplate.Height)
            throw new ArgumentException("Recorte do ícone fora dos limites da imagem.");

        using var template = new Mat(originalTemplate, new OpenCvSharp.Rect(crop.X, crop.Y, crop.Width, crop.Height));
        using var templateMask = originalMask.Empty() ? new Mat() : new Mat(originalMask,
            new OpenCvSharp.Rect(crop.X, crop.Y, crop.Width, crop.Height));
        var variants = new List<TemplateVariant>(36);
        for (var scaleStep = 40; scaleStep <= 180; scaleStep += 4)
        {
            var scale = scaleStep / 100d;
            var width = Math.Max(1, (int)Math.Round(template.Width * scale));
            var height = Math.Max(1, (int)Math.Round(template.Height * scale));
            var scaledTemplate = new Mat();
            Cv2.Resize(template, scaledTemplate, new OpenCvSharp.Size(width, height), 0, 0,
                scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Cubic);
            Mat? scaledMask = null;
            if (!templateMask.Empty())
            {
                scaledMask = new Mat();
                Cv2.Resize(templateMask, scaledMask, new OpenCvSharp.Size(width, height), 0, 0,
                    InterpolationFlags.Nearest);
            }
            variants.Add(new(scaledTemplate, scaledMask));
        }
        return variants;
    }

    private sealed record TemplateCacheKey(string Path, long Length, long LastWriteTicks,
        int CropX, int CropY, int CropWidth, int CropHeight, bool MatchColor);
    private sealed record TemplateVariant(Mat Template, Mat? Mask);

    private static Mat ToBgr(BitmapSource frame)
    {
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        using var bgra = new Mat(converted.PixelHeight, converted.PixelWidth, MatType.CV_8UC4);
        Marshal.Copy(pixels, 0, bgra.Data, pixels.Length);
        var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr;
    }

    private static void Validate(RelativeSearchRegion region, double threshold)
    {
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            region.X + region.Width > 1 || region.Y + region.Height > 1)
            throw new ArgumentOutOfRangeException(nameof(region), "A região deve estar dentro da janela, em valores de 0 a 100%.");
        if (threshold <= 0 || threshold > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), "A confiança deve estar entre 0 e 1.");
    }
}
