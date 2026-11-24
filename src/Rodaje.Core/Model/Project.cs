namespace Rodaje.Core.Model;

/// <summary>Raíz del documento: biblioteca de medios y secuencias.</summary>
/// <remarks>
/// Árbol de datos sin lógica de edición; los cambios pasan por <see cref="Commands.ICommand"/> y las
/// invariantes las comprueba <see cref="ModelValidator"/>.
/// </remarks>
public sealed class Project
{
    /// <summary>Versión del formato JSON; los archivos antiguos se migran al cargarlos.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public const int CurrentSchemaVersion = 16;

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public List<MediaRef> Media { get; set; } = [];

    public List<Sequence> Sequences { get; set; } = [];

    /// <summary>Transcripciones de los medios (formato 16, D-061): como mucho una por medio.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<MediaTranscript> Transcripts { get; set; } = [];

    // Solo en el JSON: sin transcripciones no se escribe «transcripts» y el archivo sigue igual que en el 15.
    [System.Text.Json.Serialization.JsonInclude]
    [System.Text.Json.Serialization.JsonPropertyName("transcripts")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    internal List<MediaTranscript>? StoredTranscripts
    {
        get => Transcripts.Count > 0 ? Transcripts : null;
        set => Transcripts = value ?? [];
    }

    public MediaTranscript? FindTranscript(string mediaId) => Transcripts.Find(t => t.MediaId == mediaId);

    public MediaRef? FindMedia(string mediaId) => Media.Find(m => m.Id == mediaId);

    public MediaRef GetMedia(string mediaId) =>
        FindMedia(mediaId) ?? throw new KeyNotFoundException($"No existe el medio «{mediaId}» en el proyecto.");

    public Sequence? FindSequence(string sequenceId) => Sequences.Find(s => s.Id == sequenceId);

    public Sequence GetSequence(string sequenceId) =>
        FindSequence(sequenceId) ?? throw new KeyNotFoundException($"No existe la secuencia «{sequenceId}» en el proyecto.");

    /// <summary>Copia profunda: se puede leer desde otro hilo mientras el original se sigue editando.</summary>
    public Project Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Id = Id,
        Name = Name,
        Media = Media.ConvertAll(m => m.Clone()),
        Sequences = Sequences.ConvertAll(s => s.Clone()),
        Transcripts = Transcripts.ConvertAll(t => t.Clone()),
    };
}
