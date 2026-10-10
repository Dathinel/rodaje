using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>Una marca con nombre en un instante de la secuencia.</summary>
public sealed class Marker
{
    public string Id { get; set; } = "";

    public Time Time { get; set; }

    public string Name { get; set; } = "";

    public Marker Clone() => (Marker)MemberwiseClone();
}

/// <summary>Un tramo con nombre de la secuencia, <c>[Start, End)</c>.</summary>
public sealed class Region
{
    public string Id { get; set; } = "";

    public Time Start { get; set; }

    public Time End { get; set; }

    public string Name { get; set; } = "";

    public Region Clone() => (Region)MemberwiseClone();
}
