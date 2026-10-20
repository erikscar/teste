using Macro.Services;
foreach (var size in new[] { 1d, 2d, 5d, 20d, 48d, 300d })
foreach (var anchor in new[] { 0d, 0.3, 0.5, 0.7, 1d })
for (var i = 0; i < 1000; i++)
{
    var point = MouseVariation.TargetCoordinate(-100, size, anchor);
    var inset = Math.Min(size * 0.2, 3);
    var center = Math.Clamp(size * anchor, inset, size - inset) - 100;
    if (point < -100 + inset - 1e-9 || point > -100 + size - inset + 1e-9 || Math.Abs(point - center) > 6.000001)
        throw new Exception("Clique fora da margem interna ou da variação máxima.");
}
var values = Enumerable.Range(0, 100).Select(_ => MouseVariation.TargetCoordinate(0, 48)).Distinct().Count();
if (values < 2) throw new Exception("Cliques sem variação.");
foreach (var end in new[] { (-100d, -50d), (0d, 0d), (2000d, 800d) })
{
    var controlX = end.Item1 * 0.3;
    var controlY = end.Item2 * 0.7;
    for (var i = 0; i <= 100; i++)
    {
        var point = MouseVariation.CurvePoint(0, 0, end.Item1, end.Item2, controlX, controlY, i / 100d);
        if (point.X < Math.Min(0, end.Item1) - 1e-9 || point.X > Math.Max(0, end.Item1) + 1e-9 ||
            point.Y < Math.Min(0, end.Item2) - 1e-9 || point.Y > Math.Max(0, end.Item2) + 1e-9)
            throw new Exception("Trajetória ultrapassou os extremos.");
        if (i == 100 && point != end) throw new Exception("Trajetória não terminou no alvo.");
    }
}
Console.WriteLine("OK: 30.000 pontos dentro dos limites; variação presente; trajetórias terminam no alvo sem ultrapassagem.");
