namespace Rodaje.App.Services.Collab;

/// <summary>
/// Lo que una operación larga le dice a la persona cuando no sabe cuánto falta: cuánto lleva y, si hace rato que no avanza,
/// cuándo fue el último avance («sigue trabajando, último avance hace 8 s»). Así una espera nunca parece colgada.
/// </summary>
public sealed class OperationClock(Func<long>? clock = null)
{
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private long _started = (clock ?? (() => Environment.TickCount64))();
    private long _lastProgress = (clock ?? (() => Environment.TickCount64))();

    /// <summary>Hubo un avance (un porcentaje nuevo, un archivo más): reinicia el «último avance».</summary>
    public void Progressed() => _lastProgress = _clock();

    public void Restart() => _started = _lastProgress = _clock();

    public TimeSpan Elapsed => TimeSpan.FromMilliseconds(Math.Max(0, _clock() - _started));

    public TimeSpan SinceProgress => TimeSpan.FromMilliseconds(Math.Max(0, _clock() - _lastProgress));

    /// <summary>«Llevas 0:25.» y, tras 6 s sin avance, «Sigue trabajando: último avance hace 8 s.»</summary>
    public string Describe()
    {
        string text = $"Llevas {FormatClock(Elapsed)}.";
        return SinceProgress >= TimeSpan.FromSeconds(6)
            ? text + $" Sigue trabajando: último avance hace {(int)SinceProgress.TotalSeconds} s."
            : text;
    }

    public static string FormatClock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    /// <summary>El tiempo que falta a este ritmo («unos 2 min»), o <c>null</c> si todavía no se puede estimar.</summary>
    public static string? Remaining(double fraction, TimeSpan elapsed)
    {
        if (fraction is <= 0.03 or >= 1 || elapsed < TimeSpan.FromSeconds(3))
        {
            return null;
        }

        double seconds = elapsed.TotalSeconds * (1 - fraction) / fraction;
        return seconds < 60 ? $"unos {Math.Max(5, (int)Math.Round(seconds / 5) * 5)} s" : $"unos {(int)Math.Ceiling(seconds / 60)} min";
    }
}
