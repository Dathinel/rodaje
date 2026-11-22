using Rodaje.Core.Model;

namespace Rodaje.Core.Collab;

/// <summary>Qué reservas de la sala necesita un parche (reservas por pista: <c>colaboracion.md</c> §3.2).</summary>
public static class PatchReservations
{
    /// <summary>Reserva de lo global: cabecera de secuencia, buses, mezcla maestra, marcadores, regiones, listas y medios.</summary>
    public const string Project = "proyecto";

    public static string Track(string trackId) => "pista:" + trackId;

    /// <summary>
    /// Las reservas que exige el parche sobre el proyecto <paramref name="current"/> (el del anfitrión o el del invitado antes de
    /// aplicarlo): <c>pista:&lt;id&gt;</c> por cada pista que toca (la de un clip nuevo, la de donde sale y la de donde llega) y
    /// <c>proyecto</c> por todo lo demás: crear o borrar una pista, cambiar o quitar un medio, las secuencias, marcadores, regiones,
    /// transcripciones y el orden de las listas. Añadir un medio nuevo no pide ninguna, y las pistas que crea el propio parche no se
    /// reservan (ya las cubre <c>proyecto</c>).
    /// </summary>
    public static IReadOnlySet<string> Required(Patch patch, Project current)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(current);
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        var created = patch.Ops
            .Where(op => op is { Kind: ElementKind.Track, Content: not null } && !ExistsTrack(current, op.Id))
            .Select(op => op.Id)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string>? media = null;

        foreach (PatchOp op in patch.Ops)
        {
            switch (op.Kind)
            {
                case ElementKind.Clip:
                    AddTrack(keys, created, op.Parent);
                    AddTrack(keys, created, CurrentTrackOf(current, op.Id));
                    break;
                case ElementKind.Track:
                    AddTrack(keys, created, op.Id);
                    keys.Add(Project);
                    break;
                case ElementKind.Media when op.Content is not null && !(media ??= MediaIds(current)).Contains(op.Id):
                    break;
                default:
                    keys.Add(Project);
                    break;
            }
        }

        if (patch.Orders.Count > 0)
        {
            keys.Add(Project);
        }

        return keys;
    }

    private static void AddTrack(SortedSet<string> keys, HashSet<string> created, string? trackId)
    {
        if (trackId is not null && !created.Contains(trackId))
        {
            keys.Add(Track(trackId));
        }
    }

    private static bool ExistsTrack(Project project, string trackId) =>
        project.Sequences.Any(s => s.FindTrack(trackId) is not null);

    // Índice por identificador: sin él, saber si un medio ya existe sale lineal por cada uno que trae el parche.
    private static HashSet<string> MediaIds(Project project) => [.. project.Media.Select(m => m.Id)];

    private static string? CurrentTrackOf(Project project, string clipId)
    {
        foreach (Sequence sequence in project.Sequences)
        {
            if (sequence.FindClip(clipId) is { } found)
            {
                return found.Track.Id;
            }
        }

        return null;
    }
}
