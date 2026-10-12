using System.Collections.Concurrent;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Timeline;
using Rodaje.Paquetes.Content;
using Rodaje.Playback.Composition;

namespace Rodaje.App.Services.Effects;

/// <summary>Una LUT o transición que no se pudo leer; el mensaje está en español.</summary>
public sealed class ContentProblemEventArgs(string reference, string message) : EventArgs
{
    public string Reference { get; } = reference;

    public string Message { get; } = message;
}

/// <summary>
/// El contenido de paquetes que pide el motor (<see cref="IEffectContent"/>): LUT y curvas de transiciones, leídas con un
/// <see cref="IContentLoader"/> y guardadas en memoria por referencia.
/// </summary>
/// <remarks>
/// <see cref="TryGetLut"/> se llama desde el hilo de render: con la LUT ya leída es una búsqueda en un diccionario, sin
/// reservas; si falta, la pide a un hilo del pool y devuelve <c>false</c>. Al terminar avisa con <see cref="Changed"/>.
/// Lo que no se pudo leer no se reintenta hasta <see cref="Invalidate"/> (por ejemplo, al instalar o quitar paquetes).
/// </remarks>
public sealed class EffectContent : IAwaitableEffectContent
{
    private readonly IContentLoader _loader;
    private readonly ConcurrentDictionary<string, LutEntry> _luts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TransitionCurves?> _transitions = new(StringComparer.Ordinal);
    private volatile string? _projectFolder;
    private int _generation;

    public EffectContent(IContentLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    /// <summary>Terminó de cargar algo o se descartó la caché: en pausa hay que volver a componer.</summary>
    public event EventHandler? Changed;

    /// <summary>Una LUT o transición no se pudo leer. Llega en un hilo del pool.</summary>
    public event EventHandler<ContentProblemEventArgs>? Problem;

    /// <summary>Carpeta del <c>.rodaje</c>, para las LUT sueltas con ruta relativa; nula si el proyecto no se guardó.</summary>
    public string? ProjectFolder
    {
        get => _projectFolder;
        set
        {
            if (string.Equals(_projectFolder, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _projectFolder = value;
            bool removed = false;
            foreach (string key in _luts.Keys)
            {
                if (LutReference.IsFile(key))
                {
                    removed |= _luts.TryRemove(key, out _);
                }
            }

            if (removed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>De dónde salen las correcciones de «Estabilizar» (la caché de análisis de la app); sin él, no se estabiliza.</summary>
    public Func<string, double, bool, StabilizationTrack?>? StabilizationSource { get; set; }

    public StabilizationTrack? FindStabilization(string analysis, double intensity, bool autoCrop) =>
        StabilizationSource?.Invoke(analysis, intensity, autoCrop);

    public bool TryGetLut(ResourceKey key, out LutData? lut)
    {
        lut = null;
        string? reference = key.Reference;
        if (string.IsNullOrEmpty(reference))
        {
            return false;
        }

        if (_luts.TryGetValue(reference, out LutEntry? entry))
        {
            lut = entry.Lut;
            return lut is not null;
        }

        var created = new LutEntry();
        if (_luts.TryAdd(reference, created))
        {
            int generation = Volatile.Read(ref _generation);
            ThreadPool.UnsafeQueueUserWorkItem(_ => LoadLut(reference, created, generation), null);
        }

        return false;
    }

    public TransitionCurves? FindTransition(string reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        return _transitions.GetOrAdd(reference, LoadTransition);
    }

    /// <summary>Sube con cada <see cref="Invalidate"/>: las transiciones del plan ya armado pueden haber cambiado.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Descarta todo lo leído (cambiaron los paquetes instalados) y avisa.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _luts.Clear();
        _transitions.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Espera a que termine de cargar la LUT de <paramref name="reference"/> (bien o mal); si no se pidió, termina ya.</summary>
    public Task WhenLoadedAsync(string reference) =>
        _luts.TryGetValue(reference, out LutEntry? entry) ? entry.Loaded.Task : Task.CompletedTask;

    /// <summary>Para la exportación, que espera cada LUT antes del primer cuadro (<c>OfflineTimeline.Prepare</c>).</summary>
    public Task WhenLutLoadedAsync(ResourceKey key) => key.Reference is { Length: > 0 } reference ? WhenLoadedAsync(reference) : Task.CompletedTask;

    /// <summary>Lo que el motor necesita de un preajuste de transición: mezcla, tamaño de referencia y curvas de 0 a 1.</summary>
    public static TransitionCurves ToCurves(TransitionPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        int mix = EffectRegistry.Find(preset.Mix) is { IsTransition: true } definition && definition.Type != BuiltInTransitions.Preset
            ? definition.Code
            : EffectRegistry.Find(BuiltInTransitions.Crossfade)!.Code;
        double duration = preset.DurationSeconds > 0 ? preset.DurationSeconds : 1;
        return new TransitionCurves(mix, preset.ReferenceWidth, preset.ReferenceHeight, Curves(preset.Outgoing, duration), Curves(preset.Incoming, duration))
        {
            MixParameters = preset.MixParameters,
            MixOptions = preset.MixOptions,
        };
    }

    public static Interpolation ToInterpolation(PresetInterpolation interpolation) => interpolation switch
    {
        PresetInterpolation.Hold => Interpolation.Hold,
        PresetInterpolation.EaseIn => Interpolation.EaseIn,
        PresetInterpolation.EaseOut => Interpolation.EaseOut,
        PresetInterpolation.EaseInOut => Interpolation.EaseInOut,
        _ => Interpolation.Linear,
    };

    private static List<TransitionCurve> Curves(IReadOnlyList<PresetCurve> curves, double duration) =>
    [
        .. curves
            .Where(c => c.Effect == BuiltInEffects.Transform && c.Keys.Count > 0)
            .Select(c => new TransitionCurve(c.Parameter, [.. c.Keys.Select(k => new CurveKey(Math.Clamp(k.T / duration, 0, 1), k.Value, ToInterpolation(k.Interpolation)))])),
    ];

    private void LoadLut(string reference, LutEntry entry, int generation)
    {
        try
        {
            LutTable? table;
            if (LutReference.IsFile(reference))
            {
                string? path = LutReference.ResolveFile(reference, _projectFolder);
                table = path is null ? null : _loader.LoadLutFile(path);
            }
            else
            {
                table = _loader.LoadLut(reference);
            }

            if (table is not null)
            {
                entry.Lut = new LutData(table.Size, table.Rgb);
                if (Volatile.Read(ref _generation) == generation)
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception ex) when (ex is PackageContentException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Problem?.Invoke(this, new ContentProblemEventArgs(reference, ex.Message));
        }
        finally
        {
            entry.Loaded.TrySetResult();
        }
    }

    private TransitionCurves? LoadTransition(string reference)
    {
        try
        {
            return _loader.LoadTransition(reference) is { } preset ? ToCurves(preset) : null;
        }
        catch (Exception ex) when (ex is PackageContentException or IOException or UnauthorizedAccessException)
        {
            Problem?.Invoke(this, new ContentProblemEventArgs(reference, ex.Message));
            return null;
        }
    }

    private sealed class LutEntry
    {
        private volatile LutData? _lut;

        public LutData? Lut
        {
            get => _lut;
            set => _lut = value;
        }

        public TaskCompletionSource Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
