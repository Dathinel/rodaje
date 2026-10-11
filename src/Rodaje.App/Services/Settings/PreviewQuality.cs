namespace Rodaje.App.Services.Settings;

/// <summary>Resolución a la que compone la vista previa. La exportación compone siempre a resolución completa.</summary>
public enum PreviewQuality
{
    /// <summary>Completa; al reproducir baja sola si la GPU no llega (<see cref="PreviewQualityGovernor"/>).</summary>
    Auto,
    Full,
    Half,
    Quarter,
}

/// <summary>
/// Decide el divisor de resolución de la vista previa (1, 2 o 4). En Auto, si al reproducir el p95 de la composición
/// en la GPU pasa de <see cref="BudgetMs"/> durante <see cref="Window"/>, baja un escalón (hasta un cuarto); en pausa
/// vuelve a completa. Las cifras llegan del hilo de render, que es el único que lo usa.
/// </summary>
public sealed class PreviewQualityGovernor
{
    public const double BudgetMs = 10;
    public const int MaxDivisor = 4;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly List<double> _samples = new(64);
    private TimeSpan _windowStart;
    private TimeSpan _lastSample;
    private PreviewQuality _quality;
    private int _autoDivisor = 1;

    public PreviewQuality Quality
    {
        get => _quality;
        set
        {
            _quality = value;
            ResetAuto();
        }
    }

    /// <summary>Por cuánto se divide el ancho y el alto de la secuencia.</summary>
    public int Divisor => _quality switch
    {
        PreviewQuality.Full => 1,
        PreviewQuality.Half => 2,
        PreviewQuality.Quarter => 4,
        _ => _autoDivisor,
    };

    /// <summary>Cuántas veces bajó Auto la resolución desde que se creó (para medir).</summary>
    public int Downgrades { get; private set; }

    /// <summary>Una medida de la GPU tomada al reproducir. Devuelve si cambió el divisor.</summary>
    /// <param name="now">Instante de la medida (un reloj monótono cualquiera).</param>
    public bool Sample(double gpuMs, TimeSpan now)
    {
        if (_quality != PreviewQuality.Auto || !double.IsFinite(gpuMs))
        {
            return false;
        }

        // Tras un hueco (se dejó de reproducir) lo medido antes ya no cuenta.
        if (_samples.Count > 0 && now - _lastSample > Window)
        {
            _samples.Clear();
        }

        if (_samples.Count == 0)
        {
            _windowStart = now;
        }

        _lastSample = now;
        _samples.Add(gpuMs);
        if (now - _windowStart < Window)
        {
            return false;
        }

        double p95 = Percentile95();
        _samples.Clear();
        if (p95 <= BudgetMs || _autoDivisor >= MaxDivisor)
        {
            return false;
        }

        _autoDivisor *= 2;
        Downgrades++;
        return true;
    }

    /// <summary>En pausa: Auto vuelve a completa. Devuelve si cambió el divisor.</summary>
    public bool Paused()
    {
        bool changed = _quality == PreviewQuality.Auto && _autoDivisor != 1;
        ResetAuto();
        return changed;
    }

    private void ResetAuto()
    {
        _autoDivisor = 1;
        _samples.Clear();
    }

    private double Percentile95()
    {
        _samples.Sort();
        int index = Math.Clamp((int)Math.Ceiling(_samples.Count * 0.95) - 1, 0, _samples.Count - 1);
        return _samples[index];
    }
}
