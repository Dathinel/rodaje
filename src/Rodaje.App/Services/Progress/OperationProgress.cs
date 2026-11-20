using Rodaje.App.Services.Format;

namespace Rodaje.App.Services.Progress;

/// <summary>
/// Lo que la interfaz dibuja de una operación larga en un instante: nunca una rueda, siempre algo que diga si avanza.
/// Con la fracción conocida, barra con porcentaje y tiempo restante; sin ella, barra en movimiento con las fases
/// («Paso 2 de 5: …») o el tiempo que lleva; y si pasan unos segundos sin avance, «Sigue trabajando; último avance hace 4 s».
/// </summary>
public sealed record OperationSnapshot(
    double? Fraction,
    int Phase,
    int PhaseCount,
    string PhaseText,
    string Message,
    TimeSpan Elapsed,
    TimeSpan SinceLastAdvance,
    TimeSpan? Remaining,
    bool IsStalled,
    bool CanCancel,
    bool IsCancelling,
    bool IsFinished)
{
    /// <summary>«42 %», o vacío si no se sabe cuánto falta.</summary>
    public string PercentText => Fraction is { } f ? string.Create(SpanishNumbers.Format, $"{Math.Floor((f * 100) + 1e-9):0} %") : "";

    /// <summary>«Paso 2 de 5: Analizar», o solo el texto de la fase si hay un único paso, o vacío.</summary>
    public string PhaseLabel => PhaseCount > 1 && Phase >= 1
        ? (PhaseText.Length > 0 ? $"Paso {Phase} de {PhaseCount}: {PhaseText}" : $"Paso {Phase} de {PhaseCount}")
        : PhaseText;

    /// <summary>«quedan unos 40 s», o vacío si no se puede estimar todavía (o hace rato que no avanza).</summary>
    public string RemainingText => Remaining is { } r ? OperationText.Remaining(r) : "";

    /// <summary>«Sigue trabajando; último avance hace 4 s» cuando lleva unos segundos sin avance; si no, vacío.</summary>
    public string StalledText => IsStalled && !IsCancelling ? $"Sigue trabajando; último avance hace {OperationText.Duration(SinceLastAdvance)}" : "";

    /// <summary>La línea principal bajo la barra: fase o mensaje, y el tiempo restante (o lo que lleva si no hay porcentaje).</summary>
    public string Text => TextFor(includeMessage: true);

    /// <summary>
    /// Como <see cref="Text"/>; sin <paramref name="includeMessage"/> omite el mensaje, para donde ya se muestra aparte
    /// (el título de la barra de avisos).
    /// </summary>
    public string TextFor(bool includeMessage)
    {
        List<string> parts = [];
        string phase = PhaseLabel;
        if (phase.Length > 0)
        {
            parts.Add(phase);
        }

        if (includeMessage && Message.Length > 0 && Message != phase)
        {
            parts.Add(Message);
        }

        if (!IsCancelling && !IsFinished)
        {
            if (RemainingText.Length > 0)
            {
                parts.Add(RemainingText);
            }
            else if (Fraction is null && Elapsed >= OperationProgress.MinElapsedForText)
            {
                parts.Add($"lleva {OperationText.Duration(Elapsed)}");
            }
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Todo en una frase para el lector de pantalla: porcentaje, línea principal y aviso de sin avance.</summary>
    public string AccessibleText => string.Join(". ", new[] { PercentText, Text, StalledText }.Where(s => s.Length > 0));
}

/// <summary>Textos de tiempo para el usuario, en español.</summary>
public static class OperationText
{
    /// <summary>«4 s», «1 min 5 s», «2 h 03 min».</summary>
    public static string Duration(TimeSpan t)
    {
        int s = (int)Math.Max(0, Math.Round(t.TotalSeconds));
        return s < 60
            ? $"{s} s"
            : s < 3600
                ? (s % 60 == 0 ? $"{s / 60} min" : $"{s / 60} min {s % 60} s")
                : $"{s / 3600} h {s % 3600 / 60:00} min";
    }

    /// <summary>«queda poco», «quedan unos 40 s», «quedan unos 3 min», «quedan unas 2 h 05 min». Redondea: no promete exactitud.</summary>
    public static string Remaining(TimeSpan t)
    {
        double s = Math.Max(0, t.TotalSeconds);
        if (s < 10)
        {
            return "queda poco";
        }

        if (s < 60)
        {
            return $"quedan unos {Math.Min(55, (int)(Math.Round(s / 5) * 5))} s";
        }

        if (s < 90)
        {
            return "queda cerca de 1 min";
        }

        if (s < 3600)
        {
            return $"quedan unos {(int)Math.Ceiling(s / 60)} min";
        }

        int total = (int)Math.Ceiling(s / 60);
        return $"quedan unas {total / 60} h {total % 60:00} min";
    }
}

/// <summary>
/// El estado de avance de una operación larga, sin WinUI y seguro entre hilos: quien trabaja lo informa con
/// <see cref="Report"/>, <see cref="SetPhase"/> o <see cref="Pulse"/> desde cualquier hilo y la interfaz lo lee con
/// <see cref="Snapshot"/> a su ritmo (la vista consulta unas 4 veces por segundo; informar cientos de veces por segundo no
/// cuesta nada ni llena la cola de la interfaz). Calcula el tiempo restante suavizado y detecta cuándo lleva unos segundos
/// sin avance.
/// </summary>
/// <remarks>
/// «Avance» es que suba la fracción, que cambie la fase o el mensaje, o un <see cref="Pulse"/> (para lo que no puede dar
/// porcentaje pero sabe que sigue vivo: un paquete que llega, un archivo que se sondea).
/// </remarks>
public sealed class OperationProgress
{
    /// <summary>Segundos sin avance a partir de los cuales se dice «Sigue trabajando; último avance hace…».</summary>
    public static readonly TimeSpan DefaultStallAfter = TimeSpan.FromSeconds(5);

    /// <summary>Antes de esto no se estima el tiempo restante ni se dice cuánto lleva: sería ruido.</summary>
    public static readonly TimeSpan MinElapsedForText = TimeSpan.FromSeconds(3);

    private const double MinFractionForEstimate = 0.02;
    private const double Smoothing = 0.3;
    private const double MinSampleSeconds = 0.5;

    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly long _start;
    private readonly TimeSpan _stallAfter;
    private double? _fraction;
    private int _phase;
    private int _phaseCount;
    private string _phaseText = "";
    private string _message = "";
    private long _lastAdvance;
    private long _sampleTime;
    private double _sampleFraction;
    private double _rate;
    private bool _canCancel;
    private bool _cancelling;
    private bool _finished;

    public OperationProgress(TimeProvider? clock = null, TimeSpan? stallAfter = null)
    {
        _clock = clock ?? TimeProvider.System;
        _stallAfter = stallAfter ?? DefaultStallAfter;
        _start = _lastAdvance = _sampleTime = _clock.GetTimestamp();
    }

    /// <summary>La fracción de un paso dentro del total: (<paramref name="phase"/> - 1 + <paramref name="within"/>) / <paramref name="count"/>.</summary>
    public static double PhaseFraction(int phase, int count, double within) =>
        count <= 0 ? 0 : Math.Clamp((phase - 1 + Math.Clamp(within, 0, 1)) / count, 0, 1);

    /// <summary>Informa el avance (de 0 a 1; nulo si no se sabe) y, si cambia, el mensaje. Barato: puede llamarse cada cuadro.</summary>
    public void Report(double? fraction, string? message = null)
    {
        double? f = fraction is { } v && double.IsFinite(v) ? Math.Clamp(v, 0, 1) : null;
        long now = _clock.GetTimestamp();
        lock (_lock)
        {
            if (_finished || _cancelling)
            {
                return;
            }

            bool advanced = false;
            if (message is not null && message != _message)
            {
                _message = message;
                advanced = true;
            }

            if (f is { } nf)
            {
                if (_fraction is not { } old || nf < old)
                {
                    // Primera fracción, o retrocede (empieza otra cosa): el ritmo anterior ya no vale.
                    _sampleTime = now;
                    _sampleFraction = nf;
                    _rate = 0;
                    advanced = true;
                }
                else if (nf > old)
                {
                    advanced = true;
                    double dt = _clock.GetElapsedTime(_sampleTime, now).TotalSeconds;
                    if (dt >= MinSampleSeconds)
                    {
                        double instant = (nf - _sampleFraction) / dt;
                        _rate = _rate > 0 ? (Smoothing * instant) + ((1 - Smoothing) * _rate) : instant;
                        _sampleTime = now;
                        _sampleFraction = nf;
                    }
                }
            }

            _fraction = f;
            if (advanced)
            {
                _lastAdvance = now;
            }
        }
    }

    /// <summary>Marca el paso <paramref name="phase"/> de <paramref name="count"/> («Paso 2 de 5: texto»).</summary>
    public void SetPhase(int phase, int count, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        long now = _clock.GetTimestamp();
        lock (_lock)
        {
            if (_finished || _cancelling)
            {
                return;
            }

            if (phase != _phase || count != _phaseCount || text != _phaseText)
            {
                _phase = phase;
                _phaseCount = count;
                _phaseText = text;
                _lastAdvance = now;
            }
        }
    }

    /// <summary>Dice que sigue vivo sin cambiar la fracción ni el mensaje (reinicia el «último avance»).</summary>
    public void Pulse()
    {
        long now = _clock.GetTimestamp();
        lock (_lock)
        {
            if (!_finished && !_cancelling)
            {
                _lastAdvance = now;
            }
        }
    }

    /// <summary>Si «Cancelar» se ofrece desde ahora.</summary>
    public void SetCancellable(bool canCancel)
    {
        lock (_lock)
        {
            if (!_finished && !_cancelling)
            {
                _canCancel = canCancel;
            }
        }
    }

    /// <summary>Se pidió cancelar: «Cancelando…», sin porcentaje ni botón, hasta que termine. Los avances posteriores no lo tapan.</summary>
    public void MarkCancelling(string message = "Cancelando…")
    {
        long now = _clock.GetTimestamp();
        lock (_lock)
        {
            if (_finished || _cancelling)
            {
                return;
            }

            _cancelling = true;
            _canCancel = false;
            _fraction = null;
            _message = message;
            _phaseText = "";
            _phase = _phaseCount = 0;
            _lastAdvance = now;
        }
    }

    /// <summary>Terminó: la barra se llena y deja de decir «sin avance».</summary>
    public void Complete()
    {
        lock (_lock)
        {
            _finished = true;
            _canCancel = false;
            _fraction = 1;
        }
    }

    /// <summary>Para pasar a lo que ya informa con <see cref="IProgress{T}"/> (una descarga, un recorrido de archivos).</summary>
    public IProgress<double> AsProgress() => new Adapter(this);

    /// <summary>Lo que hay que dibujar ahora.</summary>
    public OperationSnapshot Snapshot()
    {
        long now = _clock.GetTimestamp();
        lock (_lock)
        {
            TimeSpan elapsed = _clock.GetElapsedTime(_start, now);
            TimeSpan since = _clock.GetElapsedTime(_lastAdvance, now);
            bool stalled = !_finished && !_cancelling && since >= _stallAfter;
            return new OperationSnapshot(
                _fraction, _phase, _phaseCount, _phaseText, _message, elapsed, since,
                stalled || _finished || _cancelling ? null : Estimate(elapsed),
                stalled, _canCancel, _cancelling, _finished);
        }
    }

    private TimeSpan? Estimate(TimeSpan elapsed)
    {
        if (_fraction is not { } f || f < MinFractionForEstimate || f >= 1 || elapsed < MinElapsedForText)
        {
            return null;
        }

        double rate = _rate > 0 ? _rate : f / Math.Max(elapsed.TotalSeconds, 0.001);
        return rate > 0 && double.IsFinite(rate) ? TimeSpan.FromSeconds((1 - f) / rate) : null;
    }

    private sealed class Adapter(OperationProgress owner) : IProgress<double>
    {
        public void Report(double value) => owner.Report(value);
    }
}
