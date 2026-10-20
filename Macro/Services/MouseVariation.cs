namespace Macro.Services;

public static class MouseVariation
{
    // Varia perto da âncora e mantém uma margem interna no alvo detectado.
    public static double TargetCoordinate(double origin, double size, double anchor = 0.5)
    {
        if (!double.IsFinite(origin) || !double.IsFinite(size) || size <= 0 ||
            !double.IsFinite(anchor) || anchor < 0 || anchor > 1)
            throw new ArgumentOutOfRangeException(nameof(size));
        var inset = Math.Min(size * 0.2, 3);
        var center = Math.Clamp(size * anchor, inset, size - inset);
        var radius = Math.Min(6, Math.Min(size * 0.12, Math.Min(center - inset, size - inset - center)));
        return origin + center + (Random.Shared.NextDouble() * 2 - 1) * radius;
    }

    public static (double X, double Y) CurvePoint(double startX, double startY,
        double endX, double endY, double controlX, double controlY, double progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        t = t * t * (3 - 2 * t);
        var u = 1 - t;
        return (u * u * startX + 2 * u * t * controlX + t * t * endX,
            u * u * startY + 2 * u * t * controlY + t * t * endY);
    }
}
