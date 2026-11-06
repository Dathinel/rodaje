using Rodaje.Core.Model;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Core.Effects;

/// <summary>Una animación de fábrica de la transformación: un clic la convierte en keyframes que se editan como cualquiera.</summary>
public sealed record MotionPreset(string Id, string Name, string Description);

/// <summary>Un keyframe de una animación de fábrica, a <see cref="Seconds"/> del inicio del clip (tiempo de la línea de tiempo).</summary>
public readonly record struct MotionKey(double Seconds, double Value, Interpolation Interpolation, BezierEase? Ease = null);

/// <summary>Los valores de la transformación de los que parte la animación: la que el clip ya tenía al inicio.</summary>
public readonly record struct MotionBase(double X, double Y, double Scale, double Rotation)
{
    public static readonly MotionBase Default = new(0, 0, 1, 0);
}

/// <summary>
/// Animaciones de fábrica de <c>rodaje.transform</c>. Cada una da keyframes de posición, escala o giro relativos a la
/// transformación de partida (se suman a la posición y al giro y multiplican la escala) y en segundos de la línea de
/// tiempo: a 2× un «deslizar» de 0,6 s sigue durando 0,6 s en pantalla. Las posiciones se miden en el tamaño de la
/// secuencia. Determinista: la sacudida usa siempre la misma semilla.
/// </summary>
public static class MotionPresets
{
    public const string KenBurns = "kenBurns";
    public const string KenBurnsZoomIn = "kenBurnsZoomIn";
    public const string KenBurnsZoomOut = "kenBurnsZoomOut";
    public const string KenBurnsPanLeft = "kenBurnsPanLeft";
    public const string KenBurnsPanRight = "kenBurnsPanRight";
    public const string SlowZoom = "slowZoom";
    public const string SlideIn = "slideIn";
    public const string SlideOut = "slideOut";
    public const string Shake = "shake";
    public const string Heartbeat = "heartbeat";
    public const string Spin = "spin";
    public const string PunchZoom = "punchZoom";

    /// <summary>Duración de deslizar y del zoom de impacto (o la mitad del clip, si es más corto).</summary>
    public const double SlideSeconds = 0.6;

    public const double PunchSeconds = 0.4;

    /// <summary>Período de la sacudida: un keyframe cada doceavo de segundo.</summary>
    public const double ShakeStep = 1.0 / 12;

    /// <summary>Tope de keyframes por parámetro: en un clip largo, la sacudida y el latido se espacian para no pasarlo.</summary>
    public const int MaxKeys = 240;

    private static readonly BezierEase EaseOutExpo = new(0.16, 1, 0.3, 1);
    private static readonly BezierEase EaseInExpo = new(0.7, 0, 0.84, 0);

    public static IReadOnlyList<MotionPreset> All { get; } =
    [
        new(KenBurns, "Ken Burns", "Acercamiento lento con un paneo suave, para fotos y tomas quietas."),
        new(KenBurnsZoomIn, "Acercar", "Acercamiento suave hacia el centro."),
        new(KenBurnsZoomOut, "Alejar", "Alejamiento suave desde un encuadre más cercano."),
        new(KenBurnsPanLeft, "Desplazar a la izquierda", "Desplazamiento horizontal suave hacia la izquierda."),
        new(KenBurnsPanRight, "Desplazar a la derecha", "Desplazamiento horizontal suave hacia la derecha."),
        new(SlowZoom, "Zoom lento", "Se acerca un 12 % a lo largo de todo el clip."),
        new(SlideIn, "Deslizar entrada", "Entra desde la izquierda y frena al llegar."),
        new(SlideOut, "Deslizar salida", "Sale por la derecha, acelerando, al final del clip."),
        new(Shake, "Sacudida de cámara", "Temblor de cámara en mano con un 5 % de acercamiento para que no se vean los bordes."),
        new(Heartbeat, "Latido", "Dos pulsos por segundo, como un corazón."),
        new(Spin, "Giro", "Una vuelta completa a lo largo del clip, suave al empezar y al terminar."),
        new(PunchZoom, "Zoom de impacto", "Empieza un 25 % más cerca y vuelve con un rebote elástico."),
    ];

    public static MotionPreset? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Los keyframes de la animación para un clip de <paramref name="seconds"/> en una secuencia de
    /// <paramref name="width"/>×<paramref name="height"/>, por parámetro y en orden.
    /// </summary>
    /// <exception cref="ArgumentException">La animación no existe.</exception>
    public static IReadOnlyDictionary<string, IReadOnlyList<MotionKey>> Build(string id, int width, int height, double seconds, MotionBase from)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        double d = double.IsFinite(seconds) && seconds > 0 ? seconds : 1;
        double edge = Math.Min(SlideSeconds, d / 2);
        return id switch
        {
            KenBurns => new Dictionary<string, IReadOnlyList<MotionKey>>
            {
                [T.Scale] = Pair(from.Scale, from.Scale * 1.15, d, Interpolation.EaseInOut),
                [T.X] = Pair(from.X, from.X - (0.04 * width), d, Interpolation.EaseInOut),
                [T.Y] = Pair(from.Y, from.Y - (0.03 * height), d, Interpolation.EaseInOut),
            },
            KenBurnsZoomIn => new Dictionary<string, IReadOnlyList<MotionKey>>
            {
                [T.Scale] = Pair(from.Scale, from.Scale * 1.2, d, Interpolation.EaseInOut),
                [T.X] = Pair(from.X, from.X, d, Interpolation.EaseInOut),
                [T.Y] = Pair(from.Y, from.Y, d, Interpolation.EaseInOut),
            },
            KenBurnsZoomOut => new Dictionary<string, IReadOnlyList<MotionKey>>
            {
                [T.Scale] = Pair(from.Scale * 1.2, from.Scale, d, Interpolation.EaseInOut),
                [T.X] = Pair(from.X, from.X, d, Interpolation.EaseInOut),
                [T.Y] = Pair(from.Y, from.Y, d, Interpolation.EaseInOut),
            },
            KenBurnsPanLeft => new Dictionary<string, IReadOnlyList<MotionKey>>
            {
                [T.Scale] = Pair(from.Scale * 1.15, from.Scale * 1.15, d, Interpolation.EaseInOut),
                [T.X] = Pair(from.X + (0.05 * width), from.X - (0.05 * width), d, Interpolation.EaseInOut),
                [T.Y] = Pair(from.Y, from.Y, d, Interpolation.EaseInOut),
            },
            KenBurnsPanRight => new Dictionary<string, IReadOnlyList<MotionKey>>
            {
                [T.Scale] = Pair(from.Scale * 1.15, from.Scale * 1.15, d, Interpolation.EaseInOut),
                [T.X] = Pair(from.X - (0.05 * width), from.X + (0.05 * width), d, Interpolation.EaseInOut),
                [T.Y] = Pair(from.Y, from.Y, d, Interpolation.EaseInOut),
            },
            SlowZoom => new Dictionary<string, IReadOnlyList<MotionKey>>
            {
                [T.Scale] = Pair(from.Scale, from.Scale * 1.12, d, Interpolation.Linear),
            },
            SlideIn => new Dictionary<string, IReadOnlyList<MotionKey>>
            {
                [T.X] = [new(0, from.X - width, Interpolation.Bezier, EaseOutExpo), new(edge, from.X, Interpolation.Linear)],
            },
            SlideOut => new Dictionary<string, IReadOnlyList<MotionKey>>
            {
                [T.X] = [new(d - edge, from.X, Interpolation.Bezier, EaseInExpo), new(d, from.X + width, Interpolation.Linear)],
            },
            Shake => ShakeKeys(width, d, from),
            Heartbeat => new Dictionary<string, IReadOnlyList<MotionKey>> { [T.Scale] = HeartbeatKeys(d, from.Scale) },
            Spin => new Dictionary<string, IReadOnlyList<MotionKey>>
            {
                [T.Rotation] = Pair(from.Rotation, from.Rotation + 360, d, Interpolation.EaseInOut),
            },
            PunchZoom => new Dictionary<string, IReadOnlyList<MotionKey>>
            {
                [T.Scale] = [new(0, from.Scale * 1.25, Interpolation.Elastic), new(Math.Min(PunchSeconds, d), from.Scale, Interpolation.Linear)],
            },
            _ => throw new ArgumentException($"No hay una animación «{id}».", nameof(id)),
        };
    }

    private static MotionKey[] Pair(double start, double end, double seconds, Interpolation interpolation) =>
        [new(0, start, interpolation), new(seconds, end, Interpolation.Linear)];

    private static Dictionary<string, IReadOnlyList<MotionKey>> ShakeKeys(int width, double seconds, MotionBase from)
    {
        int steps = Math.Clamp((int)Math.Ceiling(seconds / ShakeStep), 1, MaxKeys - 1);
        double step = seconds / steps;
        double amplitude = 0.012 * width;
        var x = new List<MotionKey>(steps + 1);
        var y = new List<MotionKey>(steps + 1);
        var rotation = new List<MotionKey>(steps + 1);
        uint state = 0x9E3779B9;
        for (int i = 0; i <= steps; i++)
        {
            double t = i == steps ? seconds : i * step;
            bool edge = i == 0 || i == steps;
            x.Add(new(t, from.X + (edge ? 0 : amplitude * Noise(ref state)), Interpolation.EaseInOut));
            y.Add(new(t, from.Y + (edge ? 0 : amplitude * Noise(ref state)), Interpolation.EaseInOut));
            rotation.Add(new(t, from.Rotation + (edge ? 0 : 0.8 * Noise(ref state)), Interpolation.EaseInOut));
        }

        return new Dictionary<string, IReadOnlyList<MotionKey>>
        {
            [T.X] = x,
            [T.Y] = y,
            [T.Rotation] = rotation,
            [T.Scale] = [new(0, from.Scale * 1.05, Interpolation.Hold)],
        };
    }

    /// <summary>Dos pulsos por segundo (sístole y diástole): 1 → 1,08 → 1 → 1,05 → 1.</summary>
    private static List<MotionKey> HeartbeatKeys(double seconds, double scale)
    {
        ReadOnlySpan<(double At, double Factor)> beat = [(0, 1), (0.12, 1.08), (0.3, 1), (0.42, 1.05), (0.7, 1)];
        int cycles = Math.Clamp((int)Math.Floor(seconds), 1, (MaxKeys - 1) / beat.Length);
        double period = Math.Min(1, seconds / cycles);
        var keys = new List<MotionKey>((cycles * beat.Length) + 1);
        for (int c = 0; c < cycles; c++)
        {
            foreach ((double at, double factor) in beat)
            {
                keys.Add(new((c + at) * period, scale * factor, factor > 1 ? Interpolation.EaseIn : Interpolation.EaseOut));
            }
        }

        if (keys[^1].Seconds < seconds)
        {
            keys.Add(new(seconds, scale, Interpolation.Linear));
        }

        return keys;
    }

    /// <summary>Ruido uniforme en −1..1 (xorshift32).</summary>
    private static double Noise(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return ((state / (double)uint.MaxValue) * 2) - 1;
    }
}
