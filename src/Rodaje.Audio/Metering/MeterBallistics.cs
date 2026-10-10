namespace Rodaje.Audio.Metering;

/// <summary>Lo que muestra la barra de un canal: subida instantánea, caída a ritmo fijo, pico retenido y saturación.</summary>
/// <remarks>
/// Trabaja en dB. La barra sube de golpe a cada pico y baja a <see cref="DecayDecibelsPerSecond"/>, como un medidor de
/// picos de programa. El pico retenido se queda <see cref="HoldSeconds"/> y luego cae al mismo ritmo, sin bajar nunca
/// de la barra. La saturación queda marcada hasta <see cref="ResetClip"/>. Sin reservas de memoria: sirve para el
/// bucle de dibujo.
/// </remarks>
public sealed class MeterBallistics
{
    /// <summary>Lo más bajo que se dibuja; por debajo es −∞ (barra vacía).</summary>
    public const double FloorDecibels = -70;

    private double _decay = 20;
    private double _hold = 1.5;
    private double _holdAge;

    public double DecayDecibelsPerSecond
    {
        get => _decay;
        set => _decay = value > 0 && double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public double HoldSeconds
    {
        get => _hold;
        set => _hold = value >= 0 && double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>Nivel de la barra en dB; <see cref="FloorDecibels"/> o menos es barra vacía.</summary>
    public double Level { get; private set; } = double.NegativeInfinity;

    /// <summary>Pico retenido en dB.</summary>
    public double PeakHold { get; private set; } = double.NegativeInfinity;

    /// <summary>Mayor pico desde el último <see cref="ResetClip"/>, para la lectura numérica.</summary>
    public double MaxPeak { get; private set; } = double.NegativeInfinity;

    public bool Clipped { get; private set; }

    /// <summary>No queda nada que animar: barra y pico retenido vacíos.</summary>
    public bool IsIdle => Level <= FloorDecibels && PeakHold <= FloorDecibels;

    /// <summary>Avanza <paramref name="elapsedSeconds"/> con el pico medido en ese tiempo (−∞ si no sonó nada).</summary>
    public void Update(double peakDecibels, double elapsedSeconds, bool clipped = false)
    {
        double dt = Math.Max(0, elapsedSeconds);
        double input = double.IsNaN(peakDecibels) ? double.NegativeInfinity : peakDecibels;
        double fallen = Fall(Level, _decay * dt);
        Level = Math.Max(input, fallen);

        if (input >= PeakHold)
        {
            PeakHold = input;
            _holdAge = 0;
        }
        else
        {
            double before = _holdAge;
            _holdAge += dt;
            double decaying = _holdAge - Math.Max(before, _hold);
            if (decaying > 0)
            {
                PeakHold = Fall(PeakHold, _decay * decaying);
            }
        }

        PeakHold = Math.Max(PeakHold, Level);
        MaxPeak = Math.Max(MaxPeak, input);
        Clipped |= clipped;
    }

    public void ResetClip()
    {
        Clipped = false;
        MaxPeak = Level;
    }

    /// <summary>Todo a −∞ al instante (por ejemplo, al cerrar la secuencia).</summary>
    public void Clear()
    {
        Level = PeakHold = MaxPeak = double.NegativeInfinity;
        _holdAge = 0;
        Clipped = false;
    }

    private static double Fall(double level, double amount)
    {
        double next = level - amount;
        return next <= FloorDecibels ? double.NegativeInfinity : next;
    }
}

/// <summary>Escala de deflexión de los medidores de picos (la curva de IEC 60268-18): más resolución cerca de 0 dB.</summary>
public static class MeterScale
{
    // (dB, fracción de la altura) por tramos lineales.
    private static readonly (double Db, double Fraction)[] Points =
    [
        (-70, 0.0), (-60, 0.025), (-50, 0.075), (-40, 0.15), (-30, 0.30), (-20, 0.50), (0, 1.0),
    ];

    /// <summary>Marcas de la escala que se rotulan.</summary>
    public static IReadOnlyList<int> Ticks { get; } = [0, -3, -6, -12, -18, -24, -36, -48];

    /// <summary>Fracción de la altura (0 abajo, 1 arriba) para <paramref name="decibels"/>, acotada a [0, 1].</summary>
    public static double Deflection(double decibels)
    {
        if (double.IsNaN(decibels) || decibels <= Points[0].Db)
        {
            return 0;
        }

        if (decibels >= 0)
        {
            return 1;
        }

        for (int i = 1; i < Points.Length; i++)
        {
            if (decibels <= Points[i].Db)
            {
                (double db0, double f0) = Points[i - 1];
                (double db1, double f1) = Points[i];
                return f0 + ((decibels - db0) / (db1 - db0) * (f1 - f0));
            }
        }

        return 1;
    }
}
