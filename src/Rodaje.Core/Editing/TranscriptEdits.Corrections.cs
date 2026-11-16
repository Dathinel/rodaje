using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

public static partial class TranscriptEdits
{
    public static MediaTranscript CorrectWord(MediaTranscript transcript, int index, string replacement)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(replacement);
        SpokenWord old = Word(transcript, index);
        string[] parts = replacement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        long total = Math.Max(1, parts.Sum(p => (long)p.Length));
        long done = 0;
        var added = new List<SpokenWord>(parts.Length);
        foreach (string part in parts)
        {
            SpokenWord word = old.Clone();
            word.Start = old.Start + Time.FromTicks((long)((Int128)(old.End - old.Start).Ticks * done / total));
            done += part.Length;
            word.End = old.Start + Time.FromTicks((long)((Int128)(old.End - old.Start).Ticks * done / total));
            word.Text = part;
            word.ParagraphStart = added.Count == 0 && old.ParagraphStart;
            added.Add(word);
        }

        transcript.Words.RemoveAt(index);
        transcript.Words.InsertRange(index, added);
        if (added.Count == 0 && old.ParagraphStart && index < transcript.Words.Count)
            transcript.Words[index].ParagraphStart = true;
        return transcript;
    }

    public static MediaTranscript SplitParagraph(MediaTranscript transcript, int index)
    {
        Word(transcript, index).ParagraphStart = true;
        return transcript;
    }

    public static MediaTranscript AddSpeaker(MediaTranscript transcript, TranscriptSpeaker speaker)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(speaker);
        if (transcript.Speakers.Any(s => s.Id == speaker.Id)) throw new EditException("Ese hablante ya existe.");
        transcript.Speakers.Add(speaker.Clone());
        return transcript;
    }

    public static MediaTranscript UpdateSpeaker(MediaTranscript transcript, string speakerId, string name, string? color)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        TranscriptSpeaker speaker = transcript.Speakers.Find(s => s.Id == speakerId)
            ?? throw new EditException("Ese hablante ya no existe.");
        speaker.Name = name;
        speaker.Color = color;
        return transcript;
    }

    public static MediaTranscript AssignSpeaker(MediaTranscript transcript, int first, int count, string? speakerId)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        if (first < 0 || count < 1 || first > transcript.Words.Count - count)
            throw new EditException("El rango ya no está en la transcripción.");
        if (speakerId is not null && !transcript.Speakers.Any(s => s.Id == speakerId))
            throw new EditException("Ese hablante ya no existe.");
        for (int i = first; i < first + count; i++) transcript.Words[i].SpeakerId = speakerId;
        return transcript;
    }

    private static SpokenWord Word(MediaTranscript transcript, int index)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        return index >= 0 && index < transcript.Words.Count ? transcript.Words[index]
            : throw new EditException("Esa palabra ya no está en la transcripción.");
    }
}
