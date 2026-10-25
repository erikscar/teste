using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Macro.Services;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        foreach (var text in new[] { "", "4", "4/20", "31/30", "4/300", "104/30", "4/30 5/30" })
            Check(MissionCountReader.ParseCount(text) is null, $"Reject ambiguous or invalid counter: '{text}'");
        Check(MissionCountReader.ParseCount("Missões 4 / 30") == 4, "Read numerator with spaces");
        Check(MissionCountReader.ParseCount("0/30") == 0, "Zero is a valid count");
        Check(MissionCountReader.ParseCount("30/30") == 30, "Thirty is a valid count");

        var fixture = new BitmapImage();
        fixture.BeginInit();
        fixture.UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "missions-4-of-30.png"));
        fixture.CacheOption = BitmapCacheOption.OnLoad;
        fixture.EndInit();
        fixture.Freeze();
        var reader = new MissionCountReader();
        var fullRegion = new RelativeSearchRegion(0, 0, 1, 1);
        var count = reader.ReadAsync(fixture, fullRegion, CancellationToken.None).GetAwaiter().GetResult();
        Check(count == 4, $"Read actual game screenshot: expected 4, got {count?.ToString() ?? "unknown"}");

        var report = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "missions-18-report.png")));
        var reportedCrop = new CroppedBitmap(report, new Int32Rect(28, 32, 217, 90));
        reportedCrop.Freeze();
        var reportedCount = reader.ReadAsync(reportedCrop, fullRegion, CancellationToken.None).GetAwaiter().GetResult();
        Check(reportedCount == 18, $"Read reported game counter: expected 18, got {reportedCount?.ToString() ?? "unknown"}");

        foreach (var expected in new[] { 0, 9, 10, 19, 29, 30 })
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(28, 35, 51)), null, new Rect(0, 0, 160, 66));
                drawing.DrawText(new FormattedText($"{expected}/30", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 20, Brushes.LightGray, 1), new Point(12, 18));
            }
            var rendered = new RenderTargetBitmap(160, 66, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            rendered.Freeze();
            var actual = reader.ReadAsync(rendered, fullRegion, CancellationToken.None).GetAwaiter().GetResult();
            Check(actual == expected, $"Read rendered {expected}/30: got {actual?.ToString() ?? "unknown"}");
        }

        var blank = BitmapSource.Create(160, 66, 96, 96, PixelFormats.Gray8, null, new byte[160 * 66], 160);
        blank.Freeze();
        Check(reader.ReadAsync(blank, fullRegion, CancellationToken.None).GetAwaiter().GetResult() is null,
            "Blank screenshot must not count as zero or complete");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            reader.ReadAsync(fixture, fullRegion, cancellation.Token).GetAwaiter().GetResult();
            throw new Exception("Expected cancellation");
        }
        catch (OperationCanceledException) { Console.WriteLine("PASS Cancel OCR"); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
}
