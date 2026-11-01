namespace Rodaje.Core.Serialization;

/// <summary>
/// Decide cuándo toca escribir una copia de autoguardado: poco después de dejar de editar y, si se edita sin
/// pausa, como mucho cada <see cref="Interval"/>. No guarda nada por sí mismo ni mira el reloj: quien lo usa
/// le pasa la hora en cada llamada, así que se prueba sin esperas.
/// </summary>
public sealed class AutosaveSchedule
{
    private DateTimeOffset? _firstPendingEdit;
    private DateTimeOffset _lastEdit;
    private DateTimeOffset? _lastAttempt;

    /// <summary>Crea el plan con los tiempos de fábrica: 2 min, 5 s de calma y 20 s entre copias.</summary>
    public AutosaveSchedule()
        : this(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20))
    {
    }

    /// <summary>Crea el plan con tiempos propios.</summary>
    /// <param name="interval">Lo máximo que puede quedar un cambio sin copia aunque se siga editando.</param>
    /// <param name="quietPeriod">Tiempo sin cambios tras el que se copia.</param>
    /// <param name="minimumGap">Tiempo mínimo entre dos intentos, también tras un fallo.</param>
    public AutosaveSchedule(TimeSpan interval, TimeSpan quietPeriod, TimeSpan minimumGap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(quietPeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumGap, TimeSpan.Zero);
        Interval = interval;
        QuietPeriod = quietPeriod;
        MinimumGap = minimumGap;
    }

    /// <summary>Lo máximo que puede quedar un cambio sin copia; se cambia con <see cref="SetInterval"/>.</summary>
    public TimeSpan Interval { get; private set; }

    /// <summary>El intervalo más corto y el más largo que se aceptan desde Preferencias, en minutos.</summary>
    public const int MinIntervalMinutes = 1;

    public const int MaxIntervalMinutes = 30;

    public const int DefaultIntervalMinutes = 2;

    /// <summary>
    /// Cambia el intervalo (Preferencias › General). Lo pendiente sigue pendiente y se mide con el intervalo nuevo
    /// desde su primer cambio: acortarlo puede hacer que toque enseguida; alargarlo, que espere.
    /// </summary>
    public void SetInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        Interval = interval;
    }

    /// <summary>Minutos acotados a lo que admite Preferencias.</summary>
    public static int ClampMinutes(int minutes) => Math.Clamp(minutes, MinIntervalMinutes, MaxIntervalMinutes);

    public TimeSpan QuietPeriod { get; }

    public TimeSpan MinimumGap { get; }

    /// <summary>Hay cambios que todavía no tienen copia.</summary>
    public bool HasPendingChanges => _firstPendingEdit is not null;

    /// <summary>El proyecto cambió.</summary>
    public void NoteEdit(DateTimeOffset now)
    {
        _firstPendingEdit ??= now;
        _lastEdit = now;
    }

    /// <summary>
    /// Ya no hay nada que copiar: se guardó el proyecto, se abrió otro o la copia empezada con lo de
    /// <paramref name="since"/> terminó bien. Los cambios posteriores a <paramref name="since"/> siguen pendientes.
    /// </summary>
    public void NoteCaughtUp(DateTimeOffset since)
    {
        if (_firstPendingEdit is not null && _lastEdit <= since)
        {
            _firstPendingEdit = null;
        }
        else if (_firstPendingEdit is not null)
        {
            // Lo editado durante la escritura queda para la próxima copia, que cuenta desde ese cambio.
            _firstPendingEdit = _lastEdit;
        }
    }

    /// <summary>Se empezó (o se intentó) una copia: hasta <see cref="MinimumGap"/> no se vuelve a intentar.</summary>
    public void NoteAttempt(DateTimeOffset now) => _lastAttempt = now;

    /// <summary>Si toca escribir una copia ahora.</summary>
    public bool IsDue(DateTimeOffset now)
    {
        if (_firstPendingEdit is not { } first)
        {
            return false;
        }

        if (_lastAttempt is { } attempt && now - attempt < MinimumGap)
        {
            return false;
        }

        return now - _lastEdit >= QuietPeriod || now - first >= Interval;
    }
}
