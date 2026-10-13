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

    /// <summary>Función de la pista (<see cref="TrackRoles"/>, formato 5); nula en una pista corriente.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    /// <summary>Solo de una pista de audio: con alguno puesto, solo suenan las pistas y buses con solo (formato 7).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Solo { get; set; }

    /// <summary>Armada para grabar: «Grabar voz» propone esta pista (formato 7).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Armed { get; set; }

    /// <summary>Bus al que sale una pista de audio (<see cref="AudioBus.Id"/>); nulo, a la mezcla (formato 7).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Output { get; set; }

    /// <summary>Envíos de una pista de audio a buses, como mucho uno por bus (formato 7).</summary>
    [JsonIgnore]
    public List<AudioSend> Sends { get; set; } = [];

    /// <summary><see cref="Sends"/> en el archivo: no se escribe si está vacía.</summary>
    [JsonPropertyName("sends")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public List<AudioSend>? SendsInFile
    {
        get => Sends.Count > 0 ? Sends : null;
        set => Sends = value ?? [];
    }

    /// <summary>Alto de la pista en la línea de tiempo, en píxeles (formato 11); nulo: el de siempre.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Height { get; set; }

    public const int MinHeight = 24;

    public const int MaxHeight = 320;

    public List<Clip> Clips { get; set; } = [];

    /// <summary>Efectos de toda la pista (por ejemplo el volumen); sus keyframes están en tiempo de la secuencia.</summary>
    public List<EffectInstance> Effects { get; set; } = [];

    /// <summary>Transiciones en los cortes entre clips contiguos, ordenadas por posición.</summary>
    public List<TransitionInstance> Transitions { get; set; } = [];

    [JsonIgnore]
    public Time End => Clips.Count == 0 ? Time.Zero : Clips[^1].End;

    /// <summary>Clip que cubre el instante dado (el inicio cuenta, el final no).</summary>
    public Clip? ClipAt(Time time) => Clips.Find(c => c.Start <= time && time < c.End);

    public void SortClips() => Clips.Sort((a, b) => a.Start.CompareTo(b.Start));

    public Track Clone()
    {
        Track copy = (Track)MemberwiseClone();
        copy.Clips = Clips.ConvertAll(c => c.Clone());
        copy.Effects = Effects.ConvertAll(e => e.Clone());
        copy.Transitions = Transitions.ConvertAll(t => t.Clone());
        copy.Sends = Sends.ConvertAll(t => t.Clone());
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
        Role = other.Role;
        Solo = other.Solo;
        Armed = other.Armed;
        Output = other.Output;
        Sends = other.Sends.ConvertAll(t => t.Clone());
        Height = other.Height;
        Clips = other.Clips.ConvertAll(c => c.Clone());
        Effects = other.Effects.ConvertAll(e => e.Clone());
        Transitions = other.Transitions.ConvertAll(t => t.Clone());
    }

    public TransitionInstance? FindTransition(string transitionId) => Transitions.Find(t => t.Id == transitionId);

    /// <summary>La transición del corte donde termina <paramref name="clipId"/>, si la hay.</summary>
    public TransitionInstance? TransitionFrom(string clipId) => Transitions.Find(t => t.From == clipId);

    /// <summary>La transición del corte donde empieza <paramref name="clipId"/>, si la hay.</summary>
    public TransitionInstance? TransitionTo(string clipId) => Transitions.Find(t => t.To == clipId);
}
