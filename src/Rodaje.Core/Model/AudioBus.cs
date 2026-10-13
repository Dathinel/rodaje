using System.Text.Json.Serialization;

namespace Rodaje.Core.Model;

/// <summary>
/// Submezcla de la secuencia («Voces», «Música», «Efectos»): suma lo que le mandan las pistas (como salida o como envío),
/// pasa por sus inserciones y su fader (<c>rodaje.audio</c>: volumen, paneo y balance, con keyframes en tiempo de la
/// secuencia) y sale a la mezcla. Formato 7.
/// </summary>
public sealed class AudioBus
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Muted { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Solo { get; set; }

    /// <summary>Inserciones y fader, como los de una pista de audio.</summary>
    public List<EffectInstance> Effects { get; set; } = [];

    public AudioBus Clone()
    {
        AudioBus copy = (AudioBus)MemberwiseClone();
        copy.Effects = Effects.ConvertAll(e => e.Clone());
        return copy;
    }

    public bool ContentEquals(AudioBus other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Id == other.Id && Name == other.Name && Muted == other.Muted && Solo == other.Solo
            && Effects.Count == other.Effects.Count
            && Effects.Zip(other.Effects).All(p => Commands.SequenceSnapshot.EffectStateEquals(p.First, p.Second));
    }
}

/// <summary>Un envío de una pista de audio a un bus, después de sus inserciones y antes o después de su fader.</summary>
public sealed class AudioSend
{
    public const double MinGainDb = -60;

    public const double MaxGainDb = 12;

    public string BusId { get; set; } = "";

    /// <summary>Nivel del envío en dB; el mínimo es silencio.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double GainDb { get; set; }

    /// <summary>Sale antes del fader y del paneo de la pista (lo que se mande no depende de su volumen).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PreFader { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Muted { get; set; }

    public AudioSend Clone() => (AudioSend)MemberwiseClone();

    public bool ContentEquals(AudioSend other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return BusId == other.BusId && GainDb.Equals(other.GainDb) && PreFader == other.PreFader && Muted == other.Muted;
    }
}
