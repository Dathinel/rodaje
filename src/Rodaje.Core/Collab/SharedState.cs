using System.Security.Cryptography;
using System.Text;
using Rodaje.Core.Model;

namespace Rodaje.Core.Collab;

/// <summary>Lo que cuenta de un elemento compartido: su padre y su contenido canónico.</summary>
public sealed record ElementState(string? Parent, string Content);

/// <summary>
/// Una fotografía de lo que se comparte de un proyecto: cada elemento con su contenido canónico (JSON sin rutas, con las
/// claves ordenadas) y el orden de las listas. No incluye el nombre del proyecto, las rutas de los medios ni lo que vive fuera
/// del <c>.rodaje</c>. Dos réplicas están iguales si y solo si su <see cref="Hash"/> coincide.
/// </summary>
public sealed class SharedState
{
    private string? _hash;

    internal SharedState(string projectId, IReadOnlyDictionary<ElementKey, ElementState> elements, IReadOnlyDictionary<string, IReadOnlyList<string>> orders)
    {
        ProjectId = projectId;
        Elements = elements;
        Orders = orders;
    }

    public string ProjectId { get; }

    public IReadOnlyDictionary<ElementKey, ElementState> Elements { get; }

    /// <summary>Las listas ordenadas por su clave de contenedor (<see cref="PatchContainers"/>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Orders { get; }

    /// <summary>SHA-256 en hexadecimal (mayúsculas) del texto canónico de la fotografía.</summary>
    public string Hash => _hash ??= ComputeHash();

    /// <summary>
    /// Saca la fotografía del proyecto tal como está ahora. <paramref name="blobs"/> traduce las referencias <c>file:</c> a blobs;
    /// sin él, viajan vacías.
    /// </summary>
    /// <exception cref="Rodaje.Core.Serialization.ProjectFormatException">Si algún elemento tiene un valor que no se puede guardar (un número NaN, un texto con un sustituto suelto).</exception>
    public static SharedState Capture(Project project, IBlobMap? blobs = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var elements = new Dictionary<ElementKey, ElementState>();
        var orders = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (MediaRef media in project.Media)
        {
            elements[new ElementKey(ElementKind.Media, media.Id)] = new ElementState(null, ElementCodec.EncodeMedia(media, blobs));
        }

        orders[PatchContainers.Media] = [.. project.Media.Select(m => m.Id)];

        foreach (MediaTranscript transcript in project.Transcripts)
        {
            elements[new ElementKey(ElementKind.Transcript, transcript.MediaId)] = new ElementState(null, ElementCodec.EncodeTranscript(transcript, blobs));
        }

        orders[PatchContainers.Transcripts] = [.. project.Transcripts.Select(t => t.MediaId)];

        foreach (Sequence sequence in project.Sequences)
        {
            elements[new ElementKey(ElementKind.Sequence, sequence.Id)] = new ElementState(null, ElementCodec.EncodeSequence(sequence, blobs));
            AddTracks(elements, sequence, sequence.VideoTracks, blobs);
            AddTracks(elements, sequence, sequence.AudioTracks, blobs);
            orders[PatchContainers.VideoTracks(sequence.Id)] = [.. sequence.VideoTracks.Select(t => t.Id)];
            orders[PatchContainers.AudioTracks(sequence.Id)] = [.. sequence.AudioTracks.Select(t => t.Id)];

            foreach (Marker marker in sequence.Markers)
            {
                elements[new ElementKey(ElementKind.Marker, marker.Id)] = new ElementState(sequence.Id, ElementCodec.EncodeMarker(marker));
            }

            orders[PatchContainers.Markers(sequence.Id)] = [.. sequence.Markers.Select(m => m.Id)];

            foreach (Region region in sequence.Regions)
            {
                elements[new ElementKey(ElementKind.Region, region.Id)] = new ElementState(sequence.Id, ElementCodec.EncodeRegion(region));
            }

            orders[PatchContainers.Regions(sequence.Id)] = [.. sequence.Regions.Select(r => r.Id)];
        }

        orders[PatchContainers.Sequences] = [.. project.Sequences.Select(s => s.Id)];
        return new SharedState(project.Id, elements, orders);
    }

    private static void AddTracks(Dictionary<ElementKey, ElementState> elements, Sequence sequence, List<Track> tracks, IBlobMap? blobs)
    {
        foreach (Track track in tracks)
        {
            elements[new ElementKey(ElementKind.Track, track.Id)] = new ElementState(sequence.Id, ElementCodec.EncodeTrack(track, blobs));
            foreach (Clip clip in track.Clips)
            {
                elements[new ElementKey(ElementKind.Clip, clip.Id)] = new ElementState(track.Id, ElementCodec.EncodeClip(clip, blobs));
            }
        }
    }

    // Cada texto lleva su longitud delante: un identificador raro nunca puede confundirse con el siguiente campo.
    private string ComputeHash()
    {
        var text = new StringBuilder();
        Field(text, ProjectId);
        text.Append('\n');
        foreach ((ElementKey key, ElementState state) in Elements.OrderBy(p => p.Key.WireKind, StringComparer.Ordinal).ThenBy(p => p.Key.Id, StringComparer.Ordinal))
        {
            Field(text, key.WireKind);
            Field(text, key.Id);
            Field(text, state.Parent ?? "\0");
            Field(text, state.Content);
            text.Append('\n');
        }

        foreach ((string container, IReadOnlyList<string> ids) in Orders.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Field(text, container);
            text.Append(ids.Count).Append(':');
            foreach (string id in ids)
            {
                Field(text, id);
            }

            text.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void Field(StringBuilder text, string value) => text.Append(value.Length).Append(':').Append(value).Append(';');
}
