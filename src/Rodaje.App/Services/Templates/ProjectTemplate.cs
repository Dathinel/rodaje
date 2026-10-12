using System.Globalization;
using System.Text.Json.Serialization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Templates;

/// <summary>
/// Formato y pistas con los que empieza un proyecto nuevo: tamaño, tasa de cuadros, frecuencia de audio y las pistas
/// de video y de audio con sus nombres. Las de fábrica están en <see cref="BuiltInTemplates"/>; las propias se guardan
/// con <see cref="ProjectTemplateStore"/>.
/// </summary>
public sealed class ProjectTemplate
{
    /// <summary>Lado mínimo y máximo de la imagen, en píxeles.</summary>
    public const int MinSide = 16;
    public const int MaxSide = 16_384;

    /// <summary>Tasa de cuadros máxima que se acepta (cuadros por segundo).</summary>
    public const int MaxFramesPerSecond = 240;

    public const int MaxNameLength = 80;

    /// <summary>Prefijo de los identificadores de las plantillas de fábrica.</summary>
    public const string BuiltInPrefix = "fabrica.";

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public Rational FrameRate { get; set; } = Rational.Fps30;

    public int SampleRate { get; set; } = 48000;

    /// <summary>Pistas de video de abajo arriba; la primera es la principal.</summary>
    public List<TemplateTrack> VideoTracks { get; set; } = [];

    public List<TemplateTrack> AudioTracks { get; set; } = [];

    /// <summary>Viene con Rodaje y no se puede borrar ni pisar.</summary>
    [JsonIgnore]
    public bool IsBuiltIn => Id.StartsWith(BuiltInPrefix, StringComparison.Ordinal);

    /// <summary>Resumen para el menú, por ejemplo «1080×1920 · 30 fps · 2 de video, 2 de audio».</summary>
    [JsonIgnore]
    public string Summary => string.Create(CultureInfo.InvariantCulture,
        $"{Width}×{Height} · {FormatRate(FrameRate)} fps · {VideoTracks.Count} de video, {AudioTracks.Count} de audio");

    /// <summary>
    /// Crea el proyecto vacío: una secuencia con el formato de la plantilla y sus pistas en el mismo orden (la primera
    /// de video sigue siendo la principal). Una plantilla fuera de rango se corrige antes con <see cref="Normalized"/>.
    /// </summary>
    public Project CreateProject(string projectName, IIdGenerator ids)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        ArgumentNullException.ThrowIfNull(ids);
        ProjectTemplate template = Normalized() ?? throw new InvalidOperationException("La plantilla no tiene un formato válido.");
        Project project = ProjectFactory.CreateDefault(projectName, ids, template.FrameRate);
        Sequence sequence = project.Sequences[0];
        sequence.Width = template.Width;
        sequence.Height = template.Height;
        sequence.SampleRate = template.SampleRate;
        sequence.VideoTracks = template.VideoTracks.ConvertAll(t => NewTrack(TrackKind.Video, t, ids));
        sequence.AudioTracks = template.AudioTracks.ConvertAll(t => NewTrack(TrackKind.Audio, t, ids));
        return project;
    }

    /// <summary>
    /// «Guardar como plantilla»: toma el formato de la secuencia y sus pistas con sus nombres y funciones, sin clips,
    /// efectos ni estados (silencio, bloqueo). Si hay más de <see cref="NewProjectTracks.Max"/> pistas de un tipo, se
    /// quedan las primeras.
    /// </summary>
    public static ProjectTemplate FromSequence(string name, Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(sequence);
        var template = new ProjectTemplate
        {
            Id = NewUserId(),
            Name = name,
            Width = sequence.Width,
            Height = sequence.Height,
            FrameRate = sequence.FrameRate,
            SampleRate = sequence.SampleRate,
            VideoTracks = sequence.VideoTracks.ConvertAll(t => new TemplateTrack { Name = t.Name, Role = t.Role }),
            AudioTracks = sequence.AudioTracks.ConvertAll(t => new TemplateTrack { Name = t.Name, Role = t.Role }),
        };
        return template.Normalized() ?? throw new ArgumentException("La secuencia no tiene un formato que se pueda guardar.", nameof(sequence));
    }

    /// <summary>Identificador nuevo para una plantilla propia.</summary>
    public static string NewUserId() => "propia." + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Copia corregida para usarla o guardarla: nombre sin caracteres de control y acotado, lados pares entre
    /// <see cref="MinSide"/> y <see cref="MaxSide"/>, de 1 a <see cref="NewProjectTracks.Max"/> pistas de cada tipo
    /// (las vacías toman «Video N» / «Audio N»). Devuelve <c>null</c> si no tiene arreglo: sin identificador, sin
    /// nombre, o con una tasa o una frecuencia imposibles (lo que llega de un archivo editado a mano).
    /// </summary>
    public ProjectTemplate? Normalized()
    {
        string id = Clean(Id, 64);
        string name = Clean(Name, MaxNameLength);
        if (id.Length == 0 || name.Length == 0 || FrameRate.Num <= 0 || FrameRate.Den <= 0
            || FrameRate.ToDouble() is < 1 or > MaxFramesPerSecond || SampleRate is < 8000 or > 384_000)
        {
            return null;
        }

        return new ProjectTemplate
        {
            Id = id,
            Name = name,
            Width = EvenSide(Width),
            Height = EvenSide(Height),
            FrameRate = FrameRate,
            SampleRate = SampleRate,
            VideoTracks = NormalizeTracks(VideoTracks, "Video"),
            AudioTracks = NormalizeTracks(AudioTracks, "Audio"),
        };
    }

    private static Track NewTrack(TrackKind kind, TemplateTrack track, IIdGenerator ids) => new()
    {
        Id = ids.NewId("track"),
        Kind = kind,
        Name = track.Name,
        Role = track.Role,
    };

    private static List<TemplateTrack> NormalizeTracks(List<TemplateTrack>? tracks, string label)
    {
        var result = new List<TemplateTrack>();
        foreach (TemplateTrack? track in tracks ?? [])
        {
            if (result.Count == NewProjectTracks.Max)
            {
                break;
            }

            if (track is null)
            {
                continue;
            }

            string name = Clean(track.Name, MaxNameLength);
            string role = Clean(track.Role, 32);
            result.Add(new TemplateTrack
            {
                Name = name.Length == 0 ? string.Create(CultureInfo.InvariantCulture, $"{label} {result.Count + 1}") : name,
                Role = role.Length == 0 ? null : role,
            });
        }

        while (result.Count < NewProjectTracks.Min)
        {
            result.Add(new TemplateTrack { Name = string.Create(CultureInfo.InvariantCulture, $"{label} {result.Count + 1}") });
        }

        return result;
    }

    /// <summary>Lado acotado y par (los codificadores de 4:2:0 piden lados pares).</summary>
    private static int EvenSide(int side) => Math.Clamp(side, MinSide, MaxSide) & ~1;

    private static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        string kept = new([.. text.Where(c => !char.IsControl(c))]);
        kept = kept.Trim();
        if (kept.Length <= maxLength)
        {
            return kept;
        }

        // Sin partir un par sustituto (un emoji) por la mitad.
        int length = char.IsHighSurrogate(kept[maxLength - 1]) ? maxLength - 1 : maxLength;
        return kept[..length].TrimEnd();
    }

    private static string FormatRate(Rational rate) =>
        rate.Den == 1 ? rate.Num.ToString(CultureInfo.InvariantCulture) : rate.ToDouble().ToString("0.###", CultureInfo.InvariantCulture);
}
