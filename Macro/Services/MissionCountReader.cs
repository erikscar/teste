using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using Tesseract;

namespace Macro.Services;

public sealed class MissionCountReader
{
    public static int? ParseCount(string text)
    {
        var matches = Regex.Matches(text, @"(?<!\d)(\d{1,2})\s*/\s*30(?!\d)");
        if (matches.Count != 1 || !int.TryParse(matches[0].Groups[1].Value, out var count) || count > 30)
            return null;
        return count;
    }

    public Task<int?> ReadAsync(BitmapSource frame, RelativeSearchRegion region, CancellationToken token)
    {
        var rectangle = new Int32Rect((int)(frame.PixelWidth * region.X), (int)(frame.PixelHeight * region.Y),
            (int)(frame.PixelWidth * region.Width), (int)(frame.PixelHeight * region.Height));
        var crop = new CroppedBitmap(frame, rectangle);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(crop));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var bytes = stream.ToArray();
        return Task.Run(() => ReadImage(bytes, token), token);
    }

    private static int? ReadImage(byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var source = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
        using var enlarged = new Mat();
        Cv2.Resize(source, enlarged, new OpenCvSharp.Size(), 4, 4, InterpolationFlags.Cubic);
        // O jogo mostra letras claras em fundo escuro. OCR usa texto escuro em fundo claro.
        Cv2.BitwiseNot(enlarged, enlarged);
        using var engine = new TesseractEngine(Path.Combine(AppContext.BaseDirectory, "Assets", "Ocr"),
            "eng", EngineMode.LstmOnly);
        foreach (var mode in new[] { PageSegMode.SparseText, PageSegMode.SingleLine })
        {
            token.ThrowIfCancellationRequested();
            using var pix = Pix.LoadFromMemory(enlarged.ToBytes(".png"));
            using var page = engine.Process(pix, mode);
            var count = ParseCount(page.GetText());
            if (count.HasValue && page.GetMeanConfidence() >= 0.65f) return count;
            // Texto cortado e o botão + podem reduzir a confiança da região inteira.
            // Valida a confiança do contador em si, sem transformar letras em dígitos.
            if (count.HasValue)
            {
                using var iterator = page.GetIterator();
                iterator.Begin();
                do
                {
                    if (ParseCount(iterator.GetText(PageIteratorLevel.Word) ?? "") == count &&
                        iterator.GetConfidence(PageIteratorLevel.Word) >= 65)
                        return count;
                } while (iterator.Next(PageIteratorLevel.Word));
            }
        }
        return null;
    }
}
