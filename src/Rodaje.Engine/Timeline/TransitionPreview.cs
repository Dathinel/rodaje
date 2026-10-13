using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>
/// Un cuadro de una transición fuera de la línea de tiempo (las miniaturas animadas): la misma resolución que el plan
/// (mezcla, curva, obturador, imagen de luma y curvas de un paquete), así lo que se ve en la galería es lo que se exporta.
/// </summary>
/// <param name="Mix">La mezcla, sin clip que entra (lo pone quien dibuja).</param>
/// <param name="Outgoing">Transformación del clip que sale (identidad salvo las curvas de un paquete).</param>
/// <param name="Incoming">Transformación del clip que entra.</param>
public readonly record struct TransitionPreviewFrame(LayerTransition Mix, LayerTransform Outgoing, LayerTransform Incoming);

public static class TransitionPreview
{
    /// <summary>
    /// Los cuadros de <paramref name="transition"/> en <paramref name="count"/> instantes repartidos de su principio a su
    /// final (el primero es 0 y el último 1), como en una secuencia de <paramref name="width"/> × <paramref name="height"/>
    /// a <paramref name="frameRate"/>.
    /// </summary>
    public static IReadOnlyList<TransitionPreviewFrame> Frames(TransitionInstance transition, int count, int width, int height, Rational frameRate,
        IEffectContent? content)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 2);
        Time duration = transition.Duration > Time.Zero ? transition.Duration : Time.FromSeconds(1);
        TransitionPlan plan = TransitionPlan.From(transition, Time.Zero, duration, width, height, frameRate, content);
        var frames = new TransitionPreviewFrame[count];
        for (int i = 0; i < count; i++)
        {
            Time at = i == count - 1 ? duration : Time.FromTicks(duration.Ticks * i / (count - 1));
            float progress = plan.Progress(at);
            LayerTransition mix = plan.Layer(at, progress, null!);
            frames[i] = new TransitionPreviewFrame(mix, plan.Apply(LayerTransform.Identity, incoming: false, progress),
                plan.Apply(LayerTransform.Identity, incoming: true, progress));
        }

        return frames;
    }
}
