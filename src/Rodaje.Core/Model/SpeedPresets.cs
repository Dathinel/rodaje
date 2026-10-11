using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>Preajustes de curva de velocidad propios de Rodaje.</summary>
public enum SpeedPreset
{
    /// <summary>«Montaje»: tres tramos rápidos (2,5×) separados por dos frenadas cortas a 0,5×.</summary>
    Montage,

    /// <summary>«Héroe»: a 1× con una cámara lenta (0,2×) en el centro.</summary>
    Hero,

    /// <summary>«Bala»: rápido (3×), una cámara lenta extrema y larga (0,12×) y otra vez rápido.</summary>
    Bullet,

    /// <summary>«Entrada rápida»: empieza a 4× y frena hasta 1× en el primer 40 %.</summary>
    FastIn,

    /// <summary>«Salida rápida»: a 1× y acelera hasta 4× desde el 60 %.</summary>
    FastOut,
}

/// <summary>
/// Formas de los <see cref="SpeedPreset"/>: puntos en diezmilésimas del clip y velocidad en centésimas. Son datos; la
/// duración la pone <see cref="Commands.Edits.ApplySpeedPreset"/> con la velocidad media de la forma, así el clip
/// conserva su material.
/// </summary>
public static class SpeedPresets
{
    private static readonly SpeedPoint[] MontagePoints =
    [
        new(0, 250), new(2000, 250), new(2800, 50), new(3800, 50), new(4600, 250),
        new(6000, 250), new(6800, 50), new(7800, 50), new(8600, 250), new(10_000, 250),
    ];

    private static readonly SpeedPoint[] HeroPoints =
        [new(0, 100), new(3500, 100), new(4300, 20), new(5700, 20), new(6500, 100), new(10_000, 100)];

    private static readonly SpeedPoint[] BulletPoints =
        [new(0, 300), new(2500, 300), new(3300, 12), new(6700, 12), new(7500, 300), new(10_000, 300)];

    private static readonly SpeedPoint[] FastInPoints = [new(0, 400), new(1500, 400), new(4000, 100), new(10_000, 100)];

    private static readonly SpeedPoint[] FastOutPoints = [new(0, 100), new(6000, 100), new(8500, 400), new(10_000, 400)];

    public static IReadOnlyList<SpeedPreset> All { get; } =
        [SpeedPreset.Montage, SpeedPreset.Hero, SpeedPreset.Bullet, SpeedPreset.FastIn, SpeedPreset.FastOut];

    /// <summary>El nombre que ve el usuario.</summary>
    public static string Name(SpeedPreset preset) => preset switch
    {
        SpeedPreset.Montage => "Montaje",
        SpeedPreset.Hero => "Héroe",
        SpeedPreset.Bullet => "Bala",
        SpeedPreset.FastIn => "Entrada rápida",
        SpeedPreset.FastOut => "Salida rápida",
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Preajuste de velocidad desconocido."),
    };

    /// <summary>Los puntos de la forma, de 0 a <see cref="SpeedCurve.AtScale"/>.</summary>
    public static IReadOnlyList<SpeedPoint> Points(SpeedPreset preset) => preset switch
    {
        SpeedPreset.Montage => MontagePoints,
        SpeedPreset.Hero => HeroPoints,
        SpeedPreset.Bullet => BulletPoints,
        SpeedPreset.FastIn => FastInPoints,
        SpeedPreset.FastOut => FastOutPoints,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Preajuste de velocidad desconocido."),
    };

    /// <summary>
    /// Curva con la forma del preajuste y <see cref="SpeedCurve.Span"/> de un segundo; al aplicarla a un clip la edición
    /// la estira hasta cubrir su material.
    /// </summary>
    public static SpeedCurve Curve(SpeedPreset preset) => new() { Span = Time.FromSeconds(1), Points = [.. Points(preset)] };

    /// <summary>El preajuste con exactamente esos puntos (la duración no cuenta); nulo si la curva es otra.</summary>
    public static SpeedPreset? Match(SpeedCurve curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        foreach (SpeedPreset preset in All)
        {
            if (curve.Points.SequenceEqual(Points(preset)))
            {
                return preset;
            }
        }

        return null;
    }

    /// <summary>Velocidad media de la forma en el tiempo de la línea de tiempo (la integral sobre su duración).</summary>
    public static double MeanRate(IReadOnlyList<SpeedPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        long area = 0;
        for (int k = 1; k < points.Count; k++)
        {
            area += (long)(points[k].At - points[k - 1].At) * (points[k].Hundredths + points[k - 1].Hundredths);
        }

        return area / (2.0 * 100 * SpeedCurve.AtScale);
    }
}
