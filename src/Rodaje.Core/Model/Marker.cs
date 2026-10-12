using System.Text.Json.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>Una marca con nombre en un instante de la secuencia.</summary>
public sealed class Marker
{
    public string Id { get; set; } = "";

    public Time Time { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Color propio, <c>#RRGGBB</c>; nulo: el de los marcadores.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }

    /// <summary>Nota libre; nula si no tiene.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    public Marker Clone() => (Marker)MemberwiseClone();
}

/// <summary>Un tramo con nombre de la secuencia, <c>[Start, End)</c>.</summary>
public sealed class Region
{
    public string Id { get; set; } = "";

    public Time Start { get; set; }

    public Time End { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Color propio, <c>#RRGGBB</c>; nulo: el de las regiones.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }

    /// <summary>Nota libre; nula si no tiene.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    public Region Clone() => (Region)MemberwiseClone();
}

/// <summary>Reglas del color y la nota de marcadores y regiones.</summary>
public static class MarkerStyle
{
    /// <summary>Lo más largo que puede ser una nota.</summary>
    public const int MaxNoteLength = 2000;

    private static readonly System.Buffers.SearchValues<char> Hex = System.Buffers.SearchValues.Create("0123456789ABCDEFabcdef");

    /// <summary>Si el texto es un color <c>#RRGGBB</c>.</summary>
    public static bool IsColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && !value.AsSpan(1).ContainsAnyExcept(Hex);
}
