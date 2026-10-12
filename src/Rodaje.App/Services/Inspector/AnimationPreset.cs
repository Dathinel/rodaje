using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Inspector;

/// <summary>
/// La animación de la transformación de un clip guardada como complemento propio: tiempos relativos al primer keyframe,
/// valores y curvas, en un JSON que un <c>.paquete</c> puede llevar tal cual.
/// </summary>
/// <remarks>
/// Forma: <c>{ format, version, id, name, kind, effect, durationSeconds, reference: {width, height},
/// tracks: { parámetro: [ {t, value, interp} ] } }</c>. Las posiciones van en píxeles de la secuencia de
/// <c>reference</c>, para poder escalarlas a otra resolución.
/// </remarks>
public static class AnimationPreset
{
    public const string Format = "rodaje.preset";
    public const int Version = 1;
    public const string Kind = "transition";
    public const string FileExtension = ".json";

    /// <summary>Carpeta de los complementos propios: <c>%LOCALAPPDATA%\Rodaje\complementos\propios</c>.</summary>
    public static string UserFolder => Path.Combine(AppDataPaths.Root, "complementos", "propios");

    /// <summary>Hace falta al menos dos keyframes en algún parámetro, separados en el tiempo.</summary>
    public static bool CanExport(EffectInstance? transform) =>
        transform is not null
        && transform.Parameters.Values.Any(p => p.Keyframes.Count >= 2 && p.Keyframes[^1].Time > p.Keyframes[0].Time);

    public static string ToJson(string id, string name, EffectInstance transform, int referenceWidth, int referenceHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(transform);
        if (!CanExport(transform))
        {
            throw new InvalidOperationException("La animación necesita al menos dos keyframes.");
        }

        List<KeyValuePair<string, Parameter>> animated = [.. transform.Parameters
            .Where(p => p.Value.Keyframes.Count > 0)
            .OrderBy(p => p.Key, StringComparer.Ordinal)];
        Time first = animated.Min(p => p.Value.Keyframes[0].Time);
        Time last = animated.Max(p => p.Value.Keyframes[^1].Time);

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("format", Format);
            json.WriteNumber("version", Version);
            json.WriteString("id", id);
            json.WriteString("name", name.Trim());
            json.WriteString("kind", Kind);
            json.WriteString("effect", transform.Type);
            json.WriteNumber("durationSeconds", Round((last - first).ToSeconds()));
            json.WriteStartObject("reference");
            json.WriteNumber("width", referenceWidth);
            json.WriteNumber("height", referenceHeight);
            json.WriteEndObject();
            json.WriteStartObject("tracks");
            foreach ((string parameter, Parameter value) in animated)
            {
                json.WriteStartArray(parameter);
                foreach (Keyframe key in value.Keyframes)
                {
                    json.WriteStartObject();
                    json.WriteNumber("t", Round((key.Time - first).ToSeconds()));
                    json.WriteNumber("value", key.Value);
                    json.WriteString("interp", InterpolationName(key.Interpolation));
                    if (key.Interpolation == Interpolation.Bezier)
                    {
                        BezierEase ease = key.Ease ?? BezierEase.Smooth;
                        json.WriteStartArray("ease");
                        json.WriteNumberValue(ease.X1);
                        json.WriteNumberValue(ease.Y1);
                        json.WriteNumberValue(ease.X2);
                        json.WriteNumberValue(ease.Y2);
                        json.WriteEndArray();
                    }

                    json.WriteEndObject();
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>Guarda el preset en <paramref name="folder"/> con un nombre de archivo libre; devuelve la ruta.</summary>
    public static string Save(string folder, string name, EffectInstance transform, int referenceWidth, int referenceHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string slug = Slug(name);
        string path = Path.Combine(folder, slug + FileExtension);
        for (int n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{slug}-{n.ToString(CultureInfo.InvariantCulture)}{FileExtension}");
        }

        string id = "propios." + Path.GetFileNameWithoutExtension(path);
        AppDataPaths.WriteAllTextAtomic(path, ToJson(id, name, transform, referenceWidth, referenceHeight));
        return path;
    }

    /// <summary>Nombre de archivo e identificador: minúsculas sin tildes, con guiones.</summary>
    public static string Slug(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            char lower = WithoutAccent(char.ToLowerInvariant(c));
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(lower);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        string slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "animacion" : slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }

    // Tabla propia: con InvariantGlobalization no se puede contar con la normalización Unicode.
    private static char WithoutAccent(char c) => c switch
    {
        'á' or 'à' or 'â' or 'ä' or 'ã' => 'a',
        'é' or 'è' or 'ê' or 'ë' => 'e',
        'í' or 'ì' or 'î' or 'ï' => 'i',
        'ó' or 'ò' or 'ô' or 'ö' or 'õ' => 'o',
        'ú' or 'ù' or 'û' or 'ü' => 'u',
        'ñ' => 'n',
        'ç' => 'c',
        _ => c,
    };

    public static string InterpolationName(Interpolation interpolation) => interpolation switch
    {
        Interpolation.Hold => "hold",
        Interpolation.Linear => "linear",
        Interpolation.EaseIn => "easeIn",
        Interpolation.EaseOut => "easeOut",
        Interpolation.EaseInOut => "easeInOut",
        Interpolation.Bezier => "bezier",
        Interpolation.Bounce => "bounce",
        Interpolation.Elastic => "elastic",
        _ => throw new ArgumentOutOfRangeException(nameof(interpolation), interpolation, null),
    };

    private static double Round(double seconds) => Math.Round(seconds, 6);
}
