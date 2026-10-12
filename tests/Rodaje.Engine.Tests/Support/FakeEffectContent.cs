using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Tests.Support;

/// <summary>Contenido de paquetes falso: LUT listas por referencia y transiciones de paquete por referencia.</summary>
internal sealed class FakeEffectContent : IEffectContent
{
    public HashSet<string> ReadyLuts { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, TransitionCurves> Transitions { get; } = new(StringComparer.Ordinal);

    public int LutRequests { get; private set; }

    /// <summary>Correcciones de estabilizar por análisis; cada pedido queda en <see cref="StabilizationRequests"/>.</summary>
    public Dictionary<string, StabilizationTrack> Stabilizations { get; } = new(StringComparer.Ordinal);

    public List<(string Analysis, double Intensity, bool AutoCrop)> StabilizationRequests { get; } = [];

    public event EventHandler? Changed;

    public bool TryGetLut(ResourceKey key, out LutData? lut)
    {
        LutRequests++;
        lut = null;
        return ReadyLuts.Contains(key.Reference);
    }

    public TransitionCurves? FindTransition(string reference) => Transitions.GetValueOrDefault(reference);

    public StabilizationTrack? FindStabilization(string analysis, double intensity, bool autoCrop)
    {
        StabilizationRequests.Add((analysis, intensity, autoCrop));
        return Stabilizations.GetValueOrDefault(analysis);
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
