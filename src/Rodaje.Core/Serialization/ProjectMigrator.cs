using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.Core.Effects;
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
        [2] = AddEffectsAndTransitions,
        [3] = RemoveSpeedEffects,
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

    /// <summary>
    /// 2 → 3: las pistas ganan <c>effects</c> y <c>transitions</c> y los efectos <c>options</c>, vacíos. La envolvente
    /// vieja (<c>rodaje.volume</c>, ganancia lineal en <c>gain</c>) pasa a <c>rodaje.audio</c> en decibelios
    /// (<c>gainDb</c> = 20·log10(gain), con −60 dB como silencio), keyframes incluidos.
    /// </summary>
    internal static void AddEffectsAndTransitions(JsonObject root)
    {
        foreach (JsonObject sequence in Objects(root["sequences"]))
        {
            foreach (JsonObject track in Objects(sequence["videoTracks"]).Concat(Objects(sequence["audioTracks"])))
            {
                track["effects"] ??= new JsonArray();
                track["transitions"] ??= new JsonArray();
                foreach (JsonObject effect in Objects(track["effects"]))
                {
                    MigrateEffectToV3(effect);
                }

                foreach (JsonObject clip in Objects(track["clips"]))
                {
                    foreach (JsonObject effect in Objects(clip["effects"]))
                    {
                        MigrateEffectToV3(effect);
                    }
                }
            }
        }

        root[VersionProperty] = 3;
    }

    /// <summary>
    /// 3 → 4: los clips pueden llevar <c>speed</c> y <c>duration</c>. Las instancias de <c>rodaje.speed</c>, reservado y
    /// nunca aplicado, se quitan de los clips y de las pistas.
    /// </summary>
    internal static void RemoveSpeedEffects(JsonObject root)
    {
        foreach (JsonObject sequence in Objects(root["sequences"]))
        {
            foreach (JsonObject track in Objects(sequence["videoTracks"]).Concat(Objects(sequence["audioTracks"])))
            {
                RemoveSpeed(track["effects"]);
                foreach (JsonObject clip in Objects(track["clips"]))
                {
                    RemoveSpeed(clip["effects"]);
                }
            }
        }

        root[VersionProperty] = 4;
    }

    private static void RemoveSpeed(JsonNode? effects)
    {
        if (effects is not JsonArray items)
        {
            return;
        }

        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is JsonObject effect && effect["type"] is JsonValue type
                && type.TryGetValue(out string? name) && name == BuiltInEffects.Speed)
            {
                items.RemoveAt(i);
            }
        }
    }

    private static void MigrateEffectToV3(JsonObject effect)
    {
        effect["options"] ??= new JsonObject();
        if (effect["type"] is not JsonValue type || !type.TryGetValue(out string? name) || name != "rodaje.volume")
        {
            return;
        }

        effect["type"] = BuiltInEffects.Audio;
        if (effect["parameters"] is not JsonObject parameters
            || parameters["gain"] is not JsonObject gain
            || parameters.ContainsKey(BuiltInEffects.AudioParameters.GainDb))
        {
            return;
        }

        parameters.Remove("gain");
        ToDecibels(gain, "value");
        foreach (JsonObject keyframe in Objects(gain["keyframes"]))
        {
            ToDecibels(keyframe, "value");
        }

        parameters[BuiltInEffects.AudioParameters.GainDb] = gain;
    }

    private static void ToDecibels(JsonObject owner, string property)
    {
        if (owner[property] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out double linear))
        {
            owner[property] = linear > 0 && double.IsFinite(linear)
                ? Math.Max(20 * Math.Log10(linear), EffectRegistry.SilenceDb)
                : EffectRegistry.SilenceDb;
        }
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? array) =>
        array is JsonArray items ? items.OfType<JsonObject>() : [];

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
