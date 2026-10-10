using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rodaje.App.Services.Settings;

/// <summary>Lee y guarda <see cref="AppSettings"/> en <c>ajustes.json</c> (JSON generado en compilación, apto para AOT).</summary>
public sealed class SettingsStore
{
    public const string FileName = "ajustes.json";

    public SettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public static string DefaultPath => System.IO.Path.Combine(AppDataPaths.Root, FileName);

    public string Path { get; }

    public AppSettings Current { get; private set; } = new();

    /// <summary>
    /// Lee el archivo; sin archivo quedan los de fábrica. Devuelve <c>null</c> si todo fue bien o el aviso en español si
    /// estaba dañado (en ese caso se aparta para no pisarlo).
    /// </summary>
    public string? Load()
    {
        Current = new AppSettings();
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            AppSettings? loaded = JsonSerializer.Deserialize(File.ReadAllText(Path), SettingsJsonContext.Default.AppSettings);
            if (loaded is not null)
            {
                loaded.Normalize();
                Current = loaded;
                return null;
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException ex)
        {
            return $"No se pudieron leer los ajustes ({ex.Message}); se usan los de fábrica.";
        }

        string? aside = AppDataPaths.SetAside(Path, ".dañado");
        return "El archivo de ajustes estaba dañado; se usan los de fábrica" + (aside is null ? "." : $" y el archivo quedó en {aside}.");
    }

    public void Save()
    {
        Current.Normalize();
        AppDataPaths.WriteAllTextAtomic(Path, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
