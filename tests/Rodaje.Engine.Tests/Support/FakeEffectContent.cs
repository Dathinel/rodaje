using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Tests.Support;

/// <summary>Contenido de paquetes falso: LUT listas por referencia y transiciones de paquete por referencia.</summary>
internal sealed class FakeEffectContent : IEffectContent
{
    public HashSet<string> ReadyLuts { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, TransitionCurves> Transitions { get; } = new(StringComparer.Ordinal);

    public int LutRequests { get; private set; }

    public event EventHandler? Changed;

    public bool TryGetLut(ResourceKey key, out LutData? lut)
    {
        LutRequests++;
        lut = null;
        return ReadyLuts.Contains(key.Reference);
    }

    public TransitionCurves? FindTransition(string reference) => Transitions.GetValueOrDefault(reference);

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
