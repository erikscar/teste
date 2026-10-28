using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Macro.Services;
class Program {
[STAThread] static void Main() {
 var root = Path.Combine(AppContext.BaseDirectory, "Fixtures");
 var frame = new BitmapImage(new Uri(Path.Combine(root, "mining-colors.png"))); frame.Freeze();
 var service = new TemplateSearchService();
 var template = Path.Combine(root, "mining-template.png");
 foreach (var (name, region, expected) in new[] {
 ("green", new RelativeSearchRegion(0,0,1,0.49), true),
 ("blue", new RelativeSearchRegion(0,0.51,1,0.49), false),
 ("both", new RelativeSearchRegion(0,0,1,1), true) }) {
 var result = service.FindAsync(frame, template, region, 0.78, CancellationToken.None, matchColor:true).GetAwaiter().GetResult();
 if(result.Found != expected || (result.Found && result.Bounds.Y > frame.PixelHeight/2)) throw new Exception($"{name}: unexpected match {result.Found} at {result.Bounds}");
 Console.WriteLine($"PASS {name}: found={result.Found}, confidence={result.Confidence:F2}");
 }
}
}
