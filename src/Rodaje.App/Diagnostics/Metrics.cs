using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Rodaje.App.Diagnostics;

/// <summary>Cifras del sondeo: arranque, ritmo de presentación, GPU, latencia del hilo de la interfaz y de la entrada.</summary>
/// <remarks>Se escribe desde el hilo de render y desde el de la interfaz; cada lista va con su candado.</remarks>
internal sealed class Metrics
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, double> _milestonesMs = [];
    private readonly List<double> _presentIntervalsMs = new(8192);
    private readonly List<double> _renderCpuMs = new(8192);
    private readonly List<double> _gpuComposeMs = [];
    private readonly List<double> _uiQueueLatencyMs = [];
    private readonly List<double> _inputToHandlerMs = [];
    private readonly List<double> _handlerToAppliedMs = [];
    private readonly List<double> _appliedToPresentMs = [];
    private readonly List<double> _resizeToPresentMs = [];
    private readonly List<string> _notes = [];
    private long _lastPresent;
    private bool _frozen;

    /// <summary>Cuándo arrancó el proceso, según Windows; las marcas se miden desde ahí.</summary>
    public DateTime ProcessStartUtc { get; } = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    /// <summary>Anota una marca de arranque (solo la primera vez con cada nombre).</summary>
    public void Milestone(string name)
    {
        double ms = (DateTime.UtcNow - ProcessStartUtc).TotalMilliseconds;
        lock (_lock)
        {
            _milestonesMs.TryAdd(name, ms);
        }
    }

    public bool HasMilestone(string name)
    {
        lock (_lock)
        {
            return _milestonesMs.ContainsKey(name);
        }
    }

    public void Note(string text)
    {
        lock (_lock)
        {
            _notes.Add(text);
        }
    }

    /// <summary>Un <c>Present</c> del hilo de render; los intervalos solo cuentan mientras se reproduce sin cortes.</summary>
    public void Presented(bool playing, double renderCpuMs)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (_frozen)
            {
                return;
            }

            if (playing && _lastPresent != 0)
            {
                _presentIntervalsMs.Add(Stopwatch.GetElapsedTime(_lastPresent, now).TotalMilliseconds);
            }

            if (playing)
            {
                _renderCpuMs.Add(renderCpuMs);
            }

            _lastPresent = playing ? now : 0;
        }
    }

    public void GpuCompose(double ms) => AddSteady(_gpuComposeMs, ms);

    public void UiQueueLatency(double ms) => AddSteady(_uiQueueLatencyMs, ms);

    /// <summary>Deja de contar ritmo, GPU y cola de la interfaz: lo que sigue (teclas, redimensionar) los perturba a propósito.</summary>
    public void FreezeSteadyState()
    {
        lock (_lock)
        {
            _frozen = true;
        }
    }

    public void InputToHandler(double ms) => Add(_inputToHandlerMs, ms);

    public void HandlerToApplied(double ms) => Add(_handlerToAppliedMs, ms);

    public void AppliedToPresent(double ms) => Add(_appliedToPresentMs, ms);

    public void ResizeToPresent(double ms) => Add(_resizeToPresentMs, ms);

    /// <summary>Descarta lo medido en reproducción (para empezar a contar ya en régimen).</summary>
    public void ResetSteadyState()
    {
        lock (_lock)
        {
            _presentIntervalsMs.Clear();
            _renderCpuMs.Clear();
            _gpuComposeMs.Clear();
            _uiQueueLatencyMs.Clear();
            _lastPresent = 0;
        }
    }

    public string Report(IEnumerable<string> extra)
    {
        var sb = new StringBuilder();
        CultureInfo c = CultureInfo.InvariantCulture;
        lock (_lock)
        {
            foreach ((string name, double ms) in _milestonesMs.OrderBy(m => m.Value))
            {
                sb.AppendLine(c, $"arranque.{name}_ms: {ms:0}");
            }

            Summary(sb, "presentacion.intervalo", _presentIntervalsMs, hitches: true);
            Summary(sb, "render.cpu_por_cuadro", _renderCpuMs, hitches: false);
            Summary(sb, "gpu.composicion", _gpuComposeMs, hitches: false);
            Summary(sb, "ui.cola_dispatcher", _uiQueueLatencyMs, hitches: false);
            Summary(sb, "entrada.tecla_a_keydown", _inputToHandlerMs, hitches: false);
            Summary(sb, "entrada.keydown_a_sesion", _handlerToAppliedMs, hitches: false);
            Summary(sb, "entrada.sesion_a_present", _appliedToPresentMs, hitches: false);
            Summary(sb, "redimensionar.a_present", _resizeToPresentMs, hitches: false);
            foreach (string note in _notes)
            {
                sb.AppendLine(c, $"nota: {note}");
            }
        }

        foreach (string line in extra)
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static void Summary(StringBuilder sb, string name, List<double> values, bool hitches)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        if (values.Count == 0)
        {
            sb.AppendLine(c, $"{name}: sin muestras");
            return;
        }

        double[] sorted = [.. values];
        Array.Sort(sorted);
        double median = sorted[sorted.Length / 2];
        double p99 = sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.99) - 1)];
        sb.Append(c, $"{name}_ms: n={sorted.Length} mediana={median:0.00} p99={p99:0.00} max={sorted[^1]:0.00}");
        if (hitches)
        {
            sb.Append(c, $" saltos(>1.5x mediana)={sorted.Count(v => v > median * 1.5)}");
        }

        sb.AppendLine();
    }

    private void AddSteady(List<double> list, double value)
    {
        lock (_lock)
        {
            if (!_frozen)
            {
                list.Add(value);
            }
        }
    }

    private void Add(List<double> list, double value)
    {
        lock (_lock)
        {
            list.Add(value);
        }
    }
}
