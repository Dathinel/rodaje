using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Tests.Support;

/// <summary>Contenido de paquetes falso: LUT listas por referencia y transiciones de paquete por referencia.</summary>
internal sealed class FakeEffectContent : IEffectContent
{
    private EventHandler? _changed;

    public HashSet<string> ReadyLuts { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, TransitionCurves> Transitions { get; } = new(StringComparer.Ordinal);

    public int LutRequests { get; private set; }

    /// <summary>Correcciones de estabilizar por análisis; cada pedido queda en <see cref="StabilizationRequests"/>.</summary>
    public Dictionary<string, StabilizationTrack> Stabilizations { get; } = new(StringComparer.Ordinal);

    public List<(string Analysis, double Intensity, bool AutoCrop)> StabilizationRequests { get; } = [];

    /// <summary>Quien pregunta por una LUT que aún no está; se llama dentro de <see cref="TryGetLut"/>, antes de contestar.</summary>
    public Action<ResourceKey>? LutNotReady { get; set; }

    /// <summary>Cada manejador que se suscribió a <see cref="Changed"/>, aunque ya se haya quitado.</summary>
    public List<EventHandler> ChangedSubscribers { get; } = [];

    public event EventHandler? Changed
    {
        add
        {
            lock (ReadyLuts)
            {
                _changed += value;
                if (value is not null)
                {
                    ChangedSubscribers.Add(value);
                }
            }
        }

        remove
        {
            lock (ReadyLuts)
            {
                _changed -= value;
            }
        }
    }

    public bool TryGetLut(ResourceKey key, out LutData? lut)
    {
        LutRequests++;
        lut = null;
        bool ready;
        lock (ReadyLuts)
        {
            ready = ReadyLuts.Contains(key.Reference);
        }

        if (!ready)
        {
            LutNotReady?.Invoke(key);
        }

        return ready;
    }

    /// <summary>Termina de «cargar» una LUT desde cualquier hilo y avisa con <see cref="Changed"/>.</summary>
    public void FinishLoading(string reference)
    {
        lock (ReadyLuts)
        {
            ReadyLuts.Add(reference);
        }

        RaiseChanged();
    }

    public TransitionCurves? FindTransition(string reference) => Transitions.GetValueOrDefault(reference);

    public StabilizationTrack? FindStabilization(string analysis, double intensity, bool autoCrop)
    {
        StabilizationRequests.Add((analysis, intensity, autoCrop));
        return Stabilizations.GetValueOrDefault(analysis);
    }

    public void RaiseChanged() => Volatile.Read(ref _changed)?.Invoke(this, EventArgs.Empty);
}
