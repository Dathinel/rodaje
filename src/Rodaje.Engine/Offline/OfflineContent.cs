using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Offline;

/// <summary>
/// Contenido que sabe avisar cuándo terminó de cargar una LUT. Sin esto, <see cref="OfflineTimeline"/> espera la carga
/// mirando <see cref="IEffectResources.Changed"/> hasta <see cref="OfflineTimelineOptions.ContentTimeout"/>.
/// </summary>
public interface IAwaitableEffectContent : IEffectContent
{
    /// <summary>Termina cuando la carga de la LUT pedida con <see cref="IEffectResources.TryGetLut"/> acabó, bien o mal.</summary>
    Task WhenLutLoadedAsync(ResourceKey key);
}

/// <summary>Una LUT o transición de paquete que usa la secuencia y no está.</summary>
/// <param name="Reference">Referencia (<c>paquete@versión/elemento</c> o <c>file:ruta</c>).</param>
/// <param name="ClipId">Clip con la LUT, o el clip que sale de la transición.</param>
/// <param name="IsTransition">Es una transición de paquete (se ve como fundido cruzado) y no una LUT (la capa sale sin ella).</param>
public sealed record MissingContentItem(string Reference, string ClipId, bool IsTransition);

/// <summary>Lo que la secuencia necesita de los paquetes en un rango, buscado en el modelo antes del primer cuadro.</summary>
internal static class OfflineContent
{
    /// <summary>LUT de los clips visibles de <paramref name="plan"/> que se ven en <c>[start, end)</c>, en orden y sin repetir.</summary>
    public static List<(ResourceKey Key, string ClipId)> Luts(Sequence sequence, TimelinePlan plan, Time start, Time end)
    {
        HashSet<string> visible = VisibleClips(plan, start, end);
        var luts = new List<(ResourceKey, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Track track in sequence.VideoTracks)
        {
            foreach (Clip clip in track.Clips)
            {
                if (!visible.Contains(clip.Id))
                {
                    continue;
                }

                foreach (EffectInstance effect in clip.Effects)
                {
                    if (!effect.Enabled || EffectRegistry.Find(effect.Type) is not { Stage: EffectStage.PerPixel or EffectStage.Spatial } definition)
                    {
                        continue;
                    }

                    foreach (OptionDefinition option in definition.Options)
                    {
                        if (option.Kind == OptionKind.PackageReference && effect.Options.GetValueOrDefault(option.Name) is { Length: > 0 } reference
                            && seen.Add(reference))
                        {
                            luts.Add((new ResourceKey(reference), clip.Id));
                        }
                    }
                }
            }
        }

        return luts;
    }

    /// <summary>Transiciones de paquete del rango que el plan resolvió como fundido cruzado porque faltaba su contenido.</summary>
    public static List<MissingContentItem> FallenBackTransitions(Sequence sequence, TimelinePlan plan, Time start, Time end)
    {
        var missing = new List<MissingContentItem>();
        foreach (VideoLayerPlan layer in plan.Layers)
        {
            foreach (TransitionPlan? outgoing in layer.Clips.Select(c => c.Outgoing).Concat(layer.Groups.Select(g => g.Outgoing)))
            {
                if (outgoing is not { FellBack: true } transition || transition.End <= start || transition.Start >= end)
                {
                    continue;
                }

                string reference = sequence.VideoTracks.SelectMany(t => t.Transitions).FirstOrDefault(t => t.Id == transition.Id)
                    ?.Options.GetValueOrDefault(BuiltInTransitions.PresetOptions.Preset) ?? transition.Id;
                missing.Add(new MissingContentItem(reference.Length > 0 ? reference : transition.Id, transition.FromClipId, IsTransition: true));
            }
        }

        return missing;
    }

    /// <summary>Medios de los clips que se ven en <c>[start, end)</c>, sin repetir.</summary>
    public static List<SourceKey> Sources(TimelinePlan plan, Time start, Time end)
    {
        var sources = new List<SourceKey>();
        foreach (VideoLayerPlan lane in plan.Lanes)
        {
            foreach (VideoClipPlan clip in lane.Clips)
            {
                if (clip.End > start && clip.Start < end && !sources.Contains(clip.Source))
                {
                    sources.Add(clip.Source);
                }
            }
        }

        return sources;
    }

    /// <summary>
    /// Espera a que el contenido tenga la LUT: con <see cref="IAwaitableEffectContent"/> hasta que termine su carga; si
    /// no, mirando <see cref="IEffectResources.Changed"/>. Falso si falta o no llegó a tiempo.
    /// </summary>
    public static bool WaitForLut(IEffectContent content, ResourceKey key, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (content.TryGetLut(key, out _))
        {
            return true;
        }

        if (content is IAwaitableEffectContent awaitable)
        {
            Task loaded = awaitable.WhenLutLoadedAsync(key);
            try
            {
                if (!loaded.Wait(timeout, cancellationToken))
                {
                    return false;
                }
            }
            catch (AggregateException)
            {
                return false;
            }

            return content.TryGetLut(key, out _);
        }

        using var changed = new SemaphoreSlim(0);
        void OnChanged(object? sender, EventArgs e) => changed.Release();
        content.Changed += OnChanged;
        try
        {
            long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (!content.TryGetLut(key, out _))
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0)
                {
                    return false;
                }

                changed.Wait((int)Math.Min(left, 50), cancellationToken);
            }

            return true;
        }
        finally
        {
            content.Changed -= OnChanged;
        }
    }

    private static HashSet<string> VisibleClips(TimelinePlan plan, Time start, Time end)
    {
        var clips = new HashSet<string>(StringComparer.Ordinal);
        foreach (VideoLayerPlan lane in plan.Lanes)
        {
            foreach (VideoClipPlan clip in lane.Clips)
            {
                if (clip.End > start && clip.Start < end)
                {
                    clips.Add(clip.ClipId);
                }
            }

            foreach (GroupClipPlan group in lane.Groups.Concat(lane.IncomingGroups))
            {
                if (group.End > start && group.Start < end)
                {
                    clips.Add(group.ClipId);
                }
            }
        }

        return clips;
    }
}
