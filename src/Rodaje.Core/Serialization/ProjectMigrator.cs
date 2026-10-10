using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.Core.Model;

namespace Rodaje.Core.Serialization;

/// <summary>Lleva el JSON de un proyecto de una versión anterior del formato a la actual.</summary>
/// <remarks>
/// Trabaja sobre el árbol <see cref="JsonObject"/> porque las clases del modelo solo describen la versión
/// actual. Cada paso convierte <c>v</c> en <c>v + 1</c>; una migración publicada nunca se modifica.
/// </remarks>
public sealed class ProjectMigrator
{
    private const string VersionProperty = "schemaVersion";

    // Clave: versión de origen.
    private static readonly Dictionary<int, Action<JsonObject>> DefaultChain = new()
    {
        [1] = AddMarkersAndRegions,
    };

    private readonly IReadOnlyDictionary<int, Action<JsonObject>> _chain;

    public static ProjectMigrator Default { get; } = new(DefaultChain, Project.CurrentSchemaVersion);

    internal ProjectMigrator(IReadOnlyDictionary<int, Action<JsonObject>> chain, int targetVersion)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetVersion, 1);
        _chain = chain;
        TargetVersion = targetVersion;
    }

    public int TargetVersion { get; }

    /// <summary>Migra <paramref name="root"/> en el sitio.</summary>
    /// <returns>La versión que tenía el archivo antes de migrarlo.</returns>
    /// <exception cref="ProjectFormatException">Si la versión falta, no es válida o es más nueva que la soportada.</exception>
    public int Migrate(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        int original = ReadVersion(root);
        if (original > TargetVersion)
        {
            throw new ProjectFormatException(
                $"El archivo fue creado con una versión más nueva de Rodaje (formato {original}); esta versión " +
                $"solo sabe abrir hasta el formato {TargetVersion}. Actualiza Rodaje para abrirlo.");
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

    /// <summary>1 → 2: las secuencias ganan <c>markers</c> y <c>regions</c>, vacías.</summary>
    internal static void AddMarkersAndRegions(JsonObject root)
    {
        if (root["sequences"] is JsonArray sequences)
        {
            foreach (JsonObject sequence in sequences.OfType<JsonObject>())
            {
                sequence["markers"] ??= new JsonArray();
                sequence["regions"] ??= new JsonArray();
            }
        }

        root[VersionProperty] = 2;
    }

    private static int ReadVersion(JsonObject root)
    {
        if (!TryGetVersion(root, out int version) || version < 1)
        {
            throw new ProjectFormatException(
                "El archivo no es un proyecto de Rodaje válido: no indica una versión de formato reconocible " +
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
