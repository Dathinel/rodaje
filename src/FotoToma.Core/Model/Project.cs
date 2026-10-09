namespace FotoToma.Core.Model;

/// <summary>
/// Raíz del documento: un proyecto tiene una biblioteca de medios y una o más secuencias.
/// </summary>
/// <remarks>
/// El modelo es un árbol de objetos simples (sin lógica de edición) para que se serialice tal cual a
/// JSON y otros programas lo puedan leer. Toda modificación durante la edición pasa por comandos
/// (<see cref="Commands.ICommand"/>) para que se pueda deshacer; las invariantes las comprueba
/// <see cref="ModelValidator"/>.
/// </remarks>
public sealed class Project
{
    /// <summary>Versión del formato JSON; los archivos antiguos se migran al cargarlos.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public const int CurrentSchemaVersion = 1;

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public List<MediaRef> Media { get; set; } = [];

    public List<Sequence> Sequences { get; set; } = [];

    public MediaRef? FindMedia(string mediaId) => Media.Find(m => m.Id == mediaId);

    public MediaRef GetMedia(string mediaId) =>
        FindMedia(mediaId) ?? throw new KeyNotFoundException($"No existe el medio «{mediaId}» en el proyecto.");

    public Sequence? FindSequence(string sequenceId) => Sequences.Find(s => s.Id == sequenceId);

    public Sequence GetSequence(string sequenceId) =>
        FindSequence(sequenceId) ?? throw new KeyNotFoundException($"No existe la secuencia «{sequenceId}» en el proyecto.");
}
