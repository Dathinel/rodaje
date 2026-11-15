using System.Text.Json.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>
/// Transcripción de un medio entero (formato 16, D-061), en tiempo del origen: cualquier clip de ese medio, con
/// cualquier recorte o velocidad, muestra su parte proyectando las palabras por su <see cref="ClipTimeMap"/>.
/// </summary>
public sealed class MediaTranscript
{
    public string Id { get; set; } = "";

    public string MediaId { get; set; } = "";

    /// <summary>Código del idioma («es», «en»…).</summary>
    public string Language { get; set; } = "";

    /// <summary>Modelo que la hizo («large-v3-turbo-q5_0»).</summary>
    public string Model { get; set; } = "";

    /// <summary>Cuándo se transcribió, en UTC.</summary>
    public DateTime Created { get; set; }

    /// <summary>Duración del medio al transcribirlo: si al reenlazar cambia, la transcripción puede no coincidir.</summary>
    public Time MediaDuration { get; set; }

    public List<TranscriptSpeaker> Speakers { get; set; } = [];

    /// <summary>Palabras en orden de inicio.</summary>
    public List<SpokenWord> Words { get; set; } = [];

    public MediaTranscript Clone() => new()
    {
        Id = Id,
        MediaId = MediaId,
        Language = Language,
        Model = Model,
        Created = Created,
        MediaDuration = MediaDuration,
        Speakers = Speakers.ConvertAll(s => s.Clone()),
        Words = Words.ConvertAll(w => w.Clone()),
    };

    public bool ContentEquals(MediaTranscript other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Id == other.Id && MediaId == other.MediaId && Language == other.Language && Model == other.Model
            && Created == other.Created && MediaDuration == other.MediaDuration
            && Speakers.Count == other.Speakers.Count && Words.Count == other.Words.Count
            && Speakers.Zip(other.Speakers).All(p => p.First.ContentEquals(p.Second))
            && Words.Zip(other.Words).All(p => p.First.ContentEquals(p.Second));
    }
}

/// <summary>Una palabra de una <see cref="MediaTranscript"/>; claves cortas en el JSON porque son casi todo su peso.</summary>
public sealed class SpokenWord
{
    [JsonPropertyName("s")]
    public Time Start { get; set; }

    [JsonPropertyName("e")]
    public Time End { get; set; }

    [JsonPropertyName("t")]
    public string Text { get; set; } = "";

    /// <summary>Probabilidad que dio el modelo, de 0 a 1.</summary>
    [JsonPropertyName("c")]
    public double Confidence { get; set; }

    [JsonPropertyName("sp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SpeakerId { get; set; }

    /// <summary>La palabra empieza un párrafo.</summary>
    [JsonPropertyName("p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ParagraphStart { get; set; }

    public SpokenWord Clone() => (SpokenWord)MemberwiseClone();

    public bool ContentEquals(SpokenWord other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Start == other.Start && End == other.End && Text == other.Text && Confidence.Equals(other.Confidence)
            && SpeakerId == other.SpeakerId && ParagraphStart == other.ParagraphStart;
    }
}

/// <summary>Quien habla en una <see cref="MediaTranscript"/>; se asigna a mano (sin diarización en D-061).</summary>
public sealed class TranscriptSpeaker
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary><c>#RRGGBB</c>, como <see cref="Clip.Color"/>; nulo: el color por omisión.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }

    public TranscriptSpeaker Clone() => (TranscriptSpeaker)MemberwiseClone();

    public bool ContentEquals(TranscriptSpeaker other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Id == other.Id && Name == other.Name && Color == other.Color;
    }
}
