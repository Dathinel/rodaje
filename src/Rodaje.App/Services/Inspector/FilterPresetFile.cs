using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Packages.Content;

namespace Rodaje.App.Services.Inspector;

/// <summary>
/// «Guardar como preset»: los filtros de imagen del clip, con sus valores en el cursor, en un <c>rodaje.filter</c> v1 dentro
/// de los complementos propios (<c>propios\&lt;nombre&gt;.json</c>, referencia <c>propios@0.0.0/&lt;id&gt;</c>). Es el mismo
/// archivo que va en un <c>.paquete</c>, así un preset propio se publica tal cual.
/// </summary>
public static class FilterPresetFile
{
    public const string Kind = "filter";

    /// <summary>Lo que entra en un preset: lo que cambia la imagen. La transformación, el recorte, las máscaras, la mezcla y estabilizar son del clip.</summary>
    public static bool Belongs(EffectInstance effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return effect.Enabled && EffectRegistry.Find(effect.Type) is { Stage: EffectStage.PerPixel or EffectStage.Spatial } definition
            && definition.Type is not BuiltInEffects.Mask;
    }

    /// <summary>El preset como JSON: valores fijos en <paramref name="at"/> (tiempo del efecto) y las opciones tal cual.</summary>
    public static string ToJson(string id, string name, IEnumerable<EffectInstance> effects, Time at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(effects);
        List<EffectInstance> kept = [.. effects.Where(Belongs)];
        if (kept.Count == 0)
        {
            throw new InvalidOperationException("El clip no tiene filtros de imagen para guardar.");
        }

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("format", FilterPreset.Format);
            json.WriteNumber("version", FilterPreset.Version);
            json.WriteString("id", id);
            json.WriteString("name", name.Trim());
            json.WriteString("kind", Kind);
            json.WriteStartArray("effects");
            foreach (EffectInstance effect in kept)
            {
                EffectDefinition definition = EffectRegistry.Find(effect.Type)!;
                json.WriteStartObject();
                json.WriteString("type", effect.Type);
                json.WriteStartObject("parameters");
                foreach (ParameterDefinition parameter in definition.Parameters)
                {
                    double value = effect.Parameters.TryGetValue(parameter.Name, out Parameter? p) ? p.Evaluate(at) : parameter.DefaultValue;
                    if (Math.Abs(value - parameter.DefaultValue) > 1e-9)
                    {
                        json.WriteNumber(parameter.Name, Math.Round(parameter.Clamp(value), 5));
                    }
                }

                json.WriteEndObject();
                json.WriteStartObject("options");
                foreach ((string option, string value) in effect.Options.OrderBy(o => o.Key, StringComparer.Ordinal))
                {
                    if (option != BuiltInEffects.CommonOptions.Source && value.Length is > 0 and <= 256)
                    {
                        json.WriteString(option, value);
                    }
                }

                json.WriteEndObject();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Escribe el preset en <paramref name="folder"/> sin pisar otro y devuelve la ruta.</summary>
    public static string Save(string folder, string name, IEnumerable<EffectInstance> effects, Time at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string slug = AnimationPreset.Slug(name);
        string path = Path.Combine(folder, slug + ".json");
        for (int n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{slug}-{n.ToString(CultureInfo.InvariantCulture)}.json");
        }

        string id = "propios." + Path.GetFileNameWithoutExtension(path);
        AppDataPaths.WriteAllTextAtomic(path, ToJson(id, name, effects, at));
        return path;
    }

    /// <summary>Los efectos de un preset de paquete como instancias (para su miniatura).</summary>
    public static List<EffectInstance> ToInstances(FilterPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var list = new List<EffectInstance>(preset.Effects.Count);
        int n = 0;
        foreach (PresetEffect effect in preset.Effects)
        {
            var instance = new EffectInstance { Id = "preset-" + (n++).ToString(CultureInfo.InvariantCulture), Type = effect.Type };
            foreach ((string parameter, double value) in effect.Parameters)
            {
                instance.Parameters[parameter] = new Parameter { Value = value };
            }

            foreach ((string option, string value) in effect.Options)
            {
                instance.Options[option] = value;
            }

            list.Add(instance);
        }

        return list;
    }
}
