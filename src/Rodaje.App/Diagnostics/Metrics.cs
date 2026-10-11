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
    private readonly List<double> _reloadToPresentMs = [];
    private readonly List<double> _editToPresentMs = [];
    private readonly List<double> _editUiMs = [];
    private readonly List<double> _editQueueMs = [];
    private readonly List<double> _editEngineMs = [];
    private readonly List<double> _editFrameMs = [];
    private readonly List<string> _slowRefreshes = [];
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

    public void ReloadToPresent(double ms) => Add(_reloadToPresentMs, ms);

    /// <summary>Desde que empieza una edición (antes de ejecutar el comando y copiar el proyecto) hasta que se ve.</summary>
    /// <param name="total">Desde antes de ejecutar el comando hasta que se presenta.</param>
    /// <param name="afterReload">Desde que el motor terminó de recargar hasta que se presenta: esperar el cuadro y dibujarlo.</param>
    public void EditToPresent(double total, double afterReload)
    {
        Add(_editToPresentMs, total);
        Add(_editFrameMs, afterReload);
    }

    /// <summary>
    /// Las partes de una edición antes de que el motor la tenga: la interfaz (comando, copia del proyecto y vistas), la espera
    /// del hilo de render y <c>TimelineSession.Reload</c> (plan y demanda).
    /// </summary>
    public void EditParts(double ui, double queue, double engine)
    {
        Add(_editUiMs, ui);
        Add(_editQueueMs, queue);
        Add(_editEngineMs, engine);
    }

    /// <summary>Un refresco de más de 12 ms (espera del vsync, <c>Tick</c>, componer y presentar), para saber dónde se fue el tiempo.</summary>
    public void SlowRefresh(double waitMs, double tickMs, double drawMs)
    {
        if (waitMs + tickMs + drawMs <= 12)
        {
            return;
        }

        lock (_lock)
        {
            // Solo después de la medida en régimen: teclas, ediciones y redimensionar.
            if (_frozen && _slowRefreshes.Count < 40)
            {
                _slowRefreshes.Add(string.Create(CultureInfo.InvariantCulture, $"{waitMs:0.0}/{tickMs:0.0}/{drawMs:0.0}"));
            }
        }
    }

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
            Summary(sb, "edicion.a_present", _reloadToPresentMs, hitches: false);
            Summary(sb, "edicion.deslizador_a_present", _editToPresentMs, hitches: false);
            Summary(sb, "edicion.parte_interfaz", _editUiMs, hitches: false);
            Summary(sb, "edicion.parte_espera_render", _editQueueMs, hitches: false);
            Summary(sb, "edicion.parte_motor_reload", _editEngineMs, hitches: false);
            Summary(sb, "edicion.parte_cuadro_y_present", _editFrameMs, hitches: false);
            if (_slowRefreshes.Count > 0)
            {
                sb.AppendLine(c, $"render.refrescos_lentos (espera vsync/tick/componer y presentar, ms): {string.Join(" ", _slowRefreshes)}");
            }

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
        double p95 = sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.95) - 1)];
        double p99 = sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.99) - 1)];
        sb.Append(c, $"{name}_ms: n={sorted.Length} mediana={median:0.00} p95={p95:0.00} p99={p99:0.00} max={sorted[^1]:0.00}");
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
