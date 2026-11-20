using System.Text.Json;
using System.Text.Json.Serialization;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Services.Settings;

/// <summary>Lee y guarda <see cref="AppSettings"/> en <c>ajustes.json</c> (JSON generado en compilación, apto para AOT).</summary>
public sealed class SettingsStore
{
    public const string FileName = "ajustes.json";

    private string? _written;

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
        Current.Normalize();
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
                _written = JsonSerializer.Serialize(loaded, SettingsJsonContext.Default.AppSettings);
                return null;
            }
        }
        catch (JsonException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin permiso de lectura (UnauthorizedAccessException) la app se cerraba antes de abrir la ventana.
            return $"No se pudieron leer los ajustes ({ex.Message}); se usan los de fábrica.";
        }

        string? aside = AppDataPaths.SetAside(Path, ".dañado");
        return "El archivo de ajustes estaba dañado; se usan los de fábrica" + (aside is null ? "." : $" y el archivo quedó en {aside}.");
    }

    public void Save() => SaveIfChanged(force: true);

    /// <summary>Guarda solo si algo cambió desde lo último que se leyó o escribió; <c>true</c> si escribió.</summary>
    public bool SaveIfChanged(bool force = false)
    {
        Current.Normalize();
        string text = JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings);
        if (!force && text == _written)
        {
            return false;
        }

        AppDataPaths.WriteAllTextAtomic(Path, text);
        _written = text;
        return true;
    }
}

// Los convertidores: Rational y Time son structs sin setters (los ajustes de exportación los llevan).
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    Converters = [typeof(RationalJsonConverter), typeof(TimeJsonConverter)])]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
