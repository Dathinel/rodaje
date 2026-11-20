using Rodaje.Core.Interchange.Edl;
using Rodaje.Core.Interchange.Fcpxml;
using Rodaje.Core.Interchange.Otio;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.Core.Interchange;

/// <summary>
/// Qué sobrevive de una secuencia en cada formato (tabla de <c>docs/intercambio.md</c>). De esta misma regla salen el
/// informe de pérdidas y la prueba de ida y vuelta: <c>Read(Write(s))</c> debe ser equivalente a <see cref="Project"/>.
/// La regla de cada formato vive junto a su lector y su escritor (<c>EdlFidelity</c>, <c>FcpxmlFidelity</c>,
/// <c>OtioFidelity</c>).
/// </summary>
public static class Fidelity
{
    /// <summary>
    /// La secuencia como debería volver después de escribirla en <paramref name="format"/> y leerla, con sus medios y sus
    /// compuestos; cada cosa que quita o aproxima la anota en <paramref name="report"/>. No modifica <paramref name="project"/>.
    /// </summary>
    public static TimelineDraft Project(Model.Project project, Sequence sequence, InterchangeFormat format, InterchangeOptions options, InterchangeReport report)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(report);
        return format switch
        {
            InterchangeFormat.Edl => EdlFidelity.Project(project, sequence, options, report),
            InterchangeFormat.Fcpxml => FcpxmlFidelity.Project(project, sequence, options, report),
            _ => OtioFidelity.Project(project, sequence, options, report),
        };
    }

    /// <summary>
    /// Nulo si las dos son iguales sin mirar los identificadores (se emparejan por posición: los clips por su inicio, los
    /// medios y los compuestos por su primer uso); si no, la primera diferencia en una frase.
    /// </summary>
    public static string? Difference(TimelineDraft expected, TimelineDraft actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        string[] left = Canonical(expected).Split('\n');
        string[] right = Canonical(actual).Split('\n');
        for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            string a = i < left.Length ? left[i].Trim() : "(fin)";
            string b = i < right.Length ? right[i].Trim() : "(fin)";
            if (a != b)
            {
                return $"línea {i + 1}: se esperaba {a} y se leyó {b}";
            }
        }

        return null;
    }

    /// <summary>El borrador en JSON con identificadores secuenciales, los clips ordenados y los medios por su primer uso.</summary>
    internal static string Canonical(TimelineDraft draft)
    {
        Model.Project copy = draft.Project.Clone();
        Sequence main = copy.FindSequence(draft.Sequence.Id) ?? throw new ArgumentException("La secuencia principal no está en el borrador.", nameof(draft));
        foreach (Track track in copy.Sequences.SelectMany(s => s.AllTracks))
        {
            track.SortClips();
            Dictionary<string, int> order = track.Clips.Select((c, i) => (c.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
            track.Transitions.Sort((a, b) => order.GetValueOrDefault(a.From).CompareTo(order.GetValueOrDefault(b.From)));
        }

        var firstUse = new Dictionary<string, int>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(Sequence sequence)
        {
            if (!visited.Add(sequence.Id))
            {
                return;
            }

            foreach (Clip clip in sequence.AllTracks.SelectMany(t => t.Clips))
            {
                if (clip.SequenceId is { } id && copy.FindSequence(id) is { } inner)
                {
                    Visit(inner);
                }
                else if (clip.MediaId.Length > 0)
                {
                    firstUse.TryAdd(clip.MediaId, firstUse.Count);
                }
            }
        }

        Visit(main);
        copy.Media = [.. copy.Media.OrderBy(m => firstUse.GetValueOrDefault(m.Id, int.MaxValue)).ThenBy(m => m.Path, StringComparer.Ordinal)];
        RenumberedDraft numbered = DraftCopy.Renumber(new TimelineDraft(copy, main), new SequentialIdGenerator(), InterchangeLimits.Default);
        return ProjectSerializer.Serialize(new Model.Project
        {
            Id = "proyecto",
            Media = numbered.Media,
            Sequences = [.. numbered.Compounds, numbered.Main],
        });
    }
}
