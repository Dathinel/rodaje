using System.Text.Json;
using System.Text.Json.Nodes;
using FotoToma.Core.Model;

namespace FotoToma.Core.Serialization;

/// <summary>
/// Lleva el JSON de un proyecto escrito con una versión anterior del formato a la versión actual,
/// antes de convertirlo en objetos del modelo.
/// </summary>
/// <remarks>
/// <para>
/// Las migraciones trabajan sobre el árbol <see cref="JsonObject"/> y no sobre las clases del modelo,
/// porque las clases solo saben describir la versión actual: un campo renombrado o eliminado ya no
/// existe en ellas. Cada paso convierte la versión <c>v</c> en <c>v + 1</c> y se encadenan hasta llegar a
/// <see cref="Project.CurrentSchemaVersion"/>.
/// </para>
/// <para>
/// Cómo se agrega una migración, por ejemplo una futura versión 2 que añade la velocidad de los clips:
/// primero se sube <see cref="Project.CurrentSchemaVersion"/> a 2 y se añade la propiedad al modelo
/// (<c>Clip.Speed</c> con valor por defecto 1). Luego se registra en <see cref="DefaultChain"/> la entrada
/// <c>[1] = MigrateV1ToV2</c>, un método que recorre <c>sequences[*].videoTracks[*].clips[*]</c> y
/// <c>audioTracks</c>, escribe <c>"speed": 1</c> donde falte y termina con
/// <c>root["schemaVersion"] = 2</c>. Por último se regenera el esquema JSON (ver
/// <see cref="ProjectSchema"/>) con un nombre de archivo nuevo y se agrega una prueba con un archivo real
/// de la versión 1. Una migración publicada nunca se modifica: si tenía un error se corrige con otra.
/// </para>
/// </remarks>
public sealed class ProjectMigrator
{
    private const string VersionProperty = "schemaVersion";

    /// <summary>
    /// Cadena real de migraciones: la clave es la versión de origen. Vacía mientras el formato siga en la
    /// versión 1.
    /// </summary>
    private static readonly Dictionary<int, Action<JsonObject>> DefaultChain = [];

    private readonly IReadOnlyDictionary<int, Action<JsonObject>> _chain;

    /// <summary>Migrador con la cadena real hasta <see cref="Project.CurrentSchemaVersion"/>.</summary>
    public static ProjectMigrator Default { get; } = new(DefaultChain, Project.CurrentSchemaVersion);

    /// <summary>Migrador con una cadena y una versión de destino propias; existe para las pruebas.</summary>
    internal ProjectMigrator(IReadOnlyDictionary<int, Action<JsonObject>> chain, int targetVersion)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetVersion, 1);
        _chain = chain;
        TargetVersion = targetVersion;
    }

    /// <summary>Versión a la que se llevan los archivos.</summary>
    public int TargetVersion { get; }

    /// <summary>
    /// Lee la versión del documento y aplica en orden las migraciones necesarias, modificando
    /// <paramref name="root"/> en el sitio.
    /// </summary>
    /// <returns>La versión que tenía el archivo antes de migrarlo.</returns>
    /// <exception cref="ProjectFormatException">
    /// Si el documento no declara una versión válida o fue escrito por una versión más nueva del programa.
    /// </exception>
    public int Migrate(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        int original = ReadVersion(root);
        if (original > TargetVersion)
        {
            throw new ProjectFormatException(
                $"El archivo fue creado con una versión más nueva de FotoToma (formato {original}); esta versión " +
                $"solo sabe abrir hasta el formato {TargetVersion}. Actualiza FotoToma para abrirlo.");
        }

        for (int version = original; version < TargetVersion; version++)
        {
            if (!_chain.TryGetValue(version, out Action<JsonObject>? step))
            {
                throw new InvalidOperationException(
                    $"Falta la migración del formato {version} al {version + 1}.");
            }

            step(root);

            if (!TryGetVersion(root, out int after) || after != version + 1)
            {
                throw new InvalidOperationException(
                    $"La migración del formato {version} no dejó schemaVersion en {version + 1}.");
            }
        }

        return original;
    }

    private static int ReadVersion(JsonObject root)
    {
        if (!TryGetVersion(root, out int version) || version < 1)
        {
            throw new ProjectFormatException(
                "El archivo no es un proyecto de FotoToma válido: no indica una versión de formato reconocible " +
                "(falta el campo schemaVersion o no es un entero positivo).");
        }

        return version;
    }

    private static bool TryGetVersion(JsonObject root, out int version)
    {
        version = 0;
        return root[VersionProperty] is JsonValue value
            && value.GetValueKind() == JsonValueKind.Number
            && value.TryGetValue(out version);
    }
}
