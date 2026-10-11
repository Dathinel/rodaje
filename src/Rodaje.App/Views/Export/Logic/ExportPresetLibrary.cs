using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rodaje.Core.Serialization;
using Rodaje.App.Services;
using Rodaje.App.Services.Settings;
using Rodaje.Export;

namespace Rodaje.App.Views.Export;

/// <summary>
/// Los preajustes que ve el diálogo (los de fábrica y los propios), guardar, quitar, importar y exportar como archivo
/// <c>.rodaje-preajuste</c>, y lo que se recuerda de la última exportación.
/// </summary>
public static class ExportPresetLibrary
{
    public const string FileExtension = ".rodaje-preajuste";

    public const string CustomId = "personalizado";

    /// <summary>Instagram cuadrado. Va aquí hasta que el contrato (<see cref="ExportPresets"/>) lo traiga.</summary>
    public static ExportPreset Square { get; } = new("rodaje.cuadrado-1080x1080", "Instagram 1:1",
        "1080×1080 con barras si la secuencia es horizontal.", ExportPresetCategory.Platform, ExportPresetOrigin.Factory,
        new ExportSettings
        {
            Video = new VideoExportSettings
            {
                Codec = Rodaje.Media.Export.ExportCodec.H264, Width = 1080, Height = 1080, Fit = Rodaje.Media.Export.ExportFit.Bars,
                RateControl = RateControl.Vbr(8_000, 12_000), Profile = "high",
            },
            Audio = new AudioExportSettings { Codec = Rodaje.Media.Export.ExportCodec.Aac, Loudness = LoudnessTarget.Platforms },
        });

    /// <summary>Los de fábrica en el orden de las tarjetas: los del contrato con el cuadrado tras el vertical.</summary>
    public static IReadOnlyList<ExportPreset> Factory { get; } =
    [
        .. ExportPresets.All.TakeWhile(p => p.Id != ExportPresets.Vertical.Id), ExportPresets.Vertical, Square,
        .. ExportPresets.All.SkipWhile(p => p.Id != ExportPresets.Vertical.Id).Skip(1),
    ];

    public static IReadOnlyList<ExportPreset> All(ExportPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return [.. Factory, .. preferences.UserPresets];
    }

    public static ExportPreset? Find(ExportPreferences preferences, string? id) =>
        id is null ? null : All(preferences).FirstOrDefault(p => p.Id == id);

    /// <summary>Si los ajustes son los del preajuste (sin contar el modo de rendimiento, que se elige aparte).</summary>
    public static bool Matches(ExportPreset preset, ExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(settings);
        return Canonical(preset.Settings) == Canonical(settings);
    }

    /// <summary>Guarda los ajustes como preajuste propio; con el mismo nombre, lo reemplaza.</summary>
    public static ExportPreset SaveUserPreset(ExportPreferences preferences, string name, ExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(settings);
        name = (name ?? "").Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("El preajuste necesita un nombre.", nameof(name));
        }

        int existing = preferences.UserPresets.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        string id = existing >= 0 ? preferences.UserPresets[existing].Id : UniqueId(preferences, name);
        var preset = new ExportPreset(id, name, Summary(settings), ExportPresetCategory.Custom, ExportPresetOrigin.User, Canonical(settings));
        if (existing >= 0)
        {
            preferences.UserPresets[existing] = preset;
        }
        else
        {
            if (preferences.UserPresets.Count >= ExportPreferences.MaxUserPresets)
            {
                throw new InvalidOperationException($"Hay {ExportPreferences.MaxUserPresets} preajustes propios; quita alguno antes de guardar otro.");
            }

            preferences.UserPresets.Add(preset);
        }

        return preset;
    }

    public static bool Remove(ExportPreferences preferences, string id)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        bool removed = preferences.UserPresets.RemoveAll(p => p.Id == id) > 0;
        if (removed && preferences.PresetId == id)
        {
            preferences.PresetId = null;
        }

        return removed;
    }

    /// <summary>Lo que se recuerda al lanzar una exportación.</summary>
    public static void Remember(ExportPreferences preferences, string? presetId, ExportSettings settings, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(settings);
        preferences.PresetId = presetId;
        preferences.Settings = Canonical(settings) with { Performance = settings.Performance };
        preferences.Performance = settings.Performance;
        preferences.Folder = Path.GetDirectoryName(outputPath) ?? preferences.Folder;
    }

    /// <summary>Escribe el preajuste en un archivo para llevarlo a otro equipo.</summary>
    public static void WriteFile(ExportPreset preset, string path)
    {
        ArgumentNullException.ThrowIfNull(preset);
        string json = JsonSerializer.Serialize(preset with { Origin = ExportPresetOrigin.User }, ExportPresetJsonContext.Default.ExportPreset);
        AppDataPaths.WriteAllTextAtomic(path, json);
    }

    /// <summary>Lee un preajuste de archivo y lo añade como propio (con un id nuevo si choca).</summary>
    /// <exception cref="InvalidDataException">El archivo no es un preajuste de Rodaje.</exception>
    public static ExportPreset Import(ExportPreferences preferences, string path)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ExportPreset? read;
        try
        {
            read = JsonSerializer.Deserialize(File.ReadAllText(path, Encoding.UTF8), ExportPresetJsonContext.Default.ExportPreset);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("El archivo no es un preajuste de exportación de Rodaje.", ex);
        }

        if (read is not { Settings: not null } || string.IsNullOrWhiteSpace(read.Name))
        {
            throw new InvalidDataException("El archivo no es un preajuste de exportación de Rodaje.");
        }

        return SaveUserPreset(preferences, read.Name, read.Settings);
    }

    /// <summary>Una línea que resume los ajustes: «MP4 · H.264 · 1920×1080 · AAC».</summary>
    public static string Summary(ExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var parts = new List<string> { ExportChoices.ContainerName(settings.Container) };
        if (settings.Video is { } video)
        {
            parts.Add(ExportChoices.CodecName(video.Codec));
            if (video.Width is { } w && video.Height is { } h)
            {
                parts.Add($"{w}×{h}");
            }
        }

        if (settings.Audio is { } audio)
        {
            parts.Add(ExportChoices.CodecName(audio.Codec));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Sin metadatos (son del proyecto) y con el modo de rendimiento fijo, para comparar y guardar.</summary>
    private static ExportSettings Canonical(ExportSettings settings) =>
        settings with { Metadata = Rodaje.Media.Export.ExportMetadata.Empty, Performance = ExportPerformance.Background };

    private static string UniqueId(ExportPreferences preferences, string name)
    {
        string slug = new([.. name.ToLowerInvariant().Normalize(NormalizationForm.FormD)
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-')
            .Select(c => c == ' ' ? '-' : c)]);
        slug = slug.Trim('-');
        string id = "usuario." + (slug.Length == 0 ? "preajuste" : slug);
        string candidate = id;
        for (int n = 2; preferences.UserPresets.Any(p => p.Id == candidate) || Factory.Any(p => p.Id == candidate); n++)
        {
            candidate = $"{id}-{n}";
        }

        return candidate;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    Converters = [typeof(RationalJsonConverter), typeof(TimeJsonConverter)])]
[JsonSerializable(typeof(ExportPreset))]
internal sealed partial class ExportPresetJsonContext : JsonSerializerContext;
