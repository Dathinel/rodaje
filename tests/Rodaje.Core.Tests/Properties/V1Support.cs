using System.Globalization;
using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Properties;

/// <summary>Ayudas comunes de las pruebas de propiedades de lo nuevo de la v1 (compuestos, palabras, recorte, máscaras, ganancia).</summary>
internal static class V1Support
{
    public static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// El proyecto serializado con las transiciones de cada pista ordenadas por id: su orden en la lista no cambia nada de
    /// lo que se ve y al descomponer se agregan al final.
    /// </summary>
    public static string Canonical(Project project)
    {
        JsonNode root = JsonNode.Parse(ProjectSerializer.Serialize(project))!;
        foreach (JsonObject sequence in Objects(root["sequences"]))
        {
            foreach (JsonObject track in Objects(sequence["videoTracks"]).Concat(Objects(sequence["audioTracks"])))
            {
                if (track["transitions"] is JsonArray transitions && transitions.Count > 1)
                {
                    JsonNode?[] sorted = [.. transitions.OrderBy(t => (string?)t?["id"], StringComparer.Ordinal).Select(t => t?.DeepClone())];
                    transitions.Clear();
                    foreach (JsonNode? t in sorted)
                    {
                        transitions.Add(t);
                    }
                }
            }
        }

        return root.ToJsonString();
    }

    public static IEnumerable<JsonObject> Objects(JsonNode? array) =>
        array is JsonArray items ? items.OfType<JsonObject>() : [];

    /// <summary>Un medio de video con audio de <paramref name="seconds"/> segundos a 30 fps.</summary>
    public static MediaRef Media(string id, double seconds) => new()
    {
        Id = id,
        Name = id,
        Path = id + ".mp4",
        Duration = Time.FromSeconds(seconds),
        HasVideo = true,
        HasAudio = true,
        FrameRate = Rational.Fps30,
        Width = 1920,
        Height = 1080,
        SampleRate = 48000,
        AudioChannels = 2,
    };

    /// <summary>Qué se ve u oye en <paramref name="t"/> de la pista: el origen (medio, generador o secuencia) y su instante.</summary>
    public static (string Source, Time Origin)? Probe(Track track, Time t)
    {
        foreach (Clip clip in track.Clips)
        {
            if (clip.Start <= t && t < clip.End)
            {
                string source = clip.SequenceId is { } s ? "seq:" + s : clip.Generator is { } g ? "gen:" + g.Text : "media:" + clip.MediaId;
                return (source, clip.ToSourceTime(t));
            }
        }

        return null;
    }
}
