using System.Text.Json.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

public enum TrackKind
{
    Video,
    Audio,
}

/// <summary>Pista con clips ordenados por posición y sin solaparse; los huecos no se guardan.</summary>
public sealed class Track
{
    public string Id { get; set; } = "";

    public TrackKind Kind { get; set; }

    public string Name { get; set; } = "";

    public bool Muted { get; set; }

    /// <summary>Una pista bloqueada no se puede modificar con ninguna operación de edición.</summary>
    public bool Locked { get; set; }

    public bool Hidden { get; set; }

    public List<Clip> Clips { get; set; } = [];

    [JsonIgnore]
    public Time End => Clips.Count == 0 ? Time.Zero : Clips[^1].End;

    /// <summary>Clip que cubre el instante dado (el inicio cuenta, el final no).</summary>
    public Clip? ClipAt(Time time) => Clips.Find(c => c.Start <= time && time < c.End);

    public void SortClips() => Clips.Sort((a, b) => a.Start.CompareTo(b.Start));

    public Track Clone()
    {
        Track copy = (Track)MemberwiseClone();
        copy.Clips = Clips.ConvertAll(c => c.Clone());
        return copy;
    }

    /// <summary>Copia el estado de otra pista sobre esta, conservando la identidad del objeto.</summary>
    internal void CopyStateFrom(Track other)
    {
        Id = other.Id;
        Kind = other.Kind;
        Name = other.Name;
        Muted = other.Muted;
        Locked = other.Locked;
        Hidden = other.Hidden;
        Clips = other.Clips.ConvertAll(c => c.Clone());
    }
}
