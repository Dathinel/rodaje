using System.Globalization;

namespace Rodaje.App.Views.Library;

/// <summary>Qué mira una regla de una colección inteligente.</summary>
public enum SmartField
{
    /// <summary>El texto aparece en el nombre, el archivo, las etiquetas o los metadatos.</summary>
    Text,

    /// <summary>Video, imagen o audio (<see cref="LibraryKind"/> por nombre).</summary>
    Kind,

    /// <summary>Al menos tantas estrellas.</summary>
    MinRating,

    /// <summary>Favorito, rechazado o sin marca (<see cref="MediaMark"/> por nombre).</summary>
    Mark,

    /// <summary>Tiene esa etiqueta.</summary>
    Tag,

    /// <summary>Dura al menos tantos segundos.</summary>
    MinSeconds,

    /// <summary>Dura como mucho tantos segundos.</summary>
    MaxSeconds,

    /// <summary>Alto de la imagen de al menos tantos píxeles (720, 1080, 2160).</summary>
    MinHeight,

    /// <summary>Cuadros por segundo, ±0,05.</summary>
    FrameRate,

    /// <summary>El códec de video o de audio contiene el texto.</summary>
    Codec,

    /// <summary>La cámara contiene el texto.</summary>
    Camera,

    /// <summary>Grabado en los últimos tantos días (fecha del archivo si no trae fecha de grabación).</summary>
    WithinDays,

    /// <summary>El archivo no está donde dice el proyecto («true») o sí está («false»).</summary>
    Offline,

    /// <summary>Se usa en alguna secuencia («true») o no («false»).</summary>
    Used,
}

/// <summary>Una regla: un campo y un valor escrito como texto (número con punto, nombre de enumeración o texto libre).</summary>
public sealed record SmartRule(SmartField Field, string Value)
{
    /// <summary>Texto para la lista de reglas: «Al menos 3 estrellas», «Etiqueta: boda».</summary>
    public string Describe() => Field switch
    {
        SmartField.Text => $"Contiene «{Value}»",
        SmartField.Kind => $"Tipo: {LibraryFilter.Kinds.FirstOrDefault(k => k.Kind.ToString() == Value).Label ?? Value}",
        SmartField.MinRating => $"Al menos {Value} {(Value == "1" ? "estrella" : "estrellas")}",
        SmartField.Mark => Value switch { nameof(MediaMark.Favorite) => "Favoritos", nameof(MediaMark.Rejected) => "Rechazados", _ => "Sin marca" },
        SmartField.Tag => $"Etiqueta: {Value}",
        SmartField.MinSeconds => $"Dura al menos {Value} s",
        SmartField.MaxSeconds => $"Dura como mucho {Value} s",
        SmartField.MinHeight => $"Al menos {Value}p",
        SmartField.FrameRate => $"{Value.Replace('.', ',')} fps",
        SmartField.Codec => $"Códec: {Value}",
        SmartField.Camera => $"Cámara: {Value}",
        SmartField.WithinDays => $"De los últimos {Value} días",
        SmartField.Offline => Value == "true" ? "Sin conexión" : "Con conexión",
        SmartField.Used => Value == "true" ? "Usados en la secuencia" : "Sin usar",
        _ => Value,
    };

    internal double Number => double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : double.NaN;

    internal bool IsTrue => Value == "true";
}

/// <summary>
/// Una colección inteligente: las reglas que cumple un medio para estar en ella, todas (<see cref="MatchAll"/>) o al menos
/// una. No guarda medios: se recalcula con cada cambio, también con los medios que se importen después.
/// </summary>
public sealed record SmartBin(string Id, string Name, IReadOnlyList<SmartRule> Rules, bool MatchAll = true)
{
    public bool Matches(MediaFacts facts, LibraryOrganization organization, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(organization);
        if (Rules.Count == 0)
        {
            return true;
        }

        return MatchAll
            ? Rules.All(r => Check(r, facts, organization, now))
            : Rules.Any(r => Check(r, facts, organization, now));
    }

    public static bool Check(SmartRule rule, MediaFacts facts, LibraryOrganization organization, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(organization);
        string id = facts.Media.Id;
        return rule.Field switch
        {
            SmartField.Text => LibraryFilter.Matches(facts, SearchText.Words(rule.Value)),
            SmartField.Kind => facts.Kind.ToString() == rule.Value,
            SmartField.MinRating => organization.RatingOf(id) >= rule.Number,
            SmartField.Mark => organization.MarkOf(id).ToString() == rule.Value,
            SmartField.Tag => organization.TagsOf(id).Any(t => SearchText.Fold(t) == SearchText.Fold(rule.Value)),
            SmartField.MinSeconds => facts.Media.Duration.ToSeconds() >= rule.Number,
            SmartField.MaxSeconds => facts.Media.Duration.ToSeconds() <= rule.Number,
            SmartField.MinHeight => facts.Media.HasVideo && Math.Min(facts.Media.Width, facts.Media.Height) >= rule.Number,
            SmartField.FrameRate => facts.Media.FrameRate is { } rate && Math.Abs(rate.ToDouble() - rule.Number) <= 0.05,
            SmartField.Codec => Contains(facts.Codecs, rule.Value),
            SmartField.Camera => Contains(facts.Camera, rule.Value),
            SmartField.WithinDays => facts.Date is { } date && (now - date).TotalDays <= rule.Number && date <= now.AddDays(1),
            SmartField.Offline => facts.Offline == rule.IsTrue,
            SmartField.Used => facts.Used == rule.IsTrue,
            _ => false,
        };
    }

    private static bool Contains(string? text, string value) =>
        text is not null && SearchText.Fold(text).Contains(SearchText.Fold(value).Trim(), StringComparison.Ordinal);
}
