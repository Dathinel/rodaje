using Rodaje.App.Views.Subtitles;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Transcripts;

/// <summary>
/// La transcripción de un medio entero (D-061): lo que devuelve <c>subtitulos</c> sobre todo el audio, pasado a una
/// <see cref="MediaTranscript"/> en tiempo del origen para guardarla en el proyecto y reutilizarla en cualquier clip.
/// </summary>
internal static class MediaTranscriber
{
    /// <summary>Lo que se transcribe: el medio entero, para que valga para todos sus clips.</summary>
    public static (Time Start, Time End) Window(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return (Time.Zero, media.Duration);
    }

    /// <summary>
    /// La transcripción ya guardada del medio, si sirve para <paramref name="language"/> («auto» acepta cualquiera); así
    /// los subtítulos salen sin otra pasada de Whisper.
    /// </summary>
    public static MediaTranscript? Reusable(Project project, string mediaId, string language)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.FindTranscript(mediaId) is { Words.Count: > 0 } stored && (language == "auto" || stored.Language == language) ? stored : null;
    }

    /// <summary>
    /// Pasa el resultado del módulo (segundos desde el inicio del medio) a una transcripción del medio: palabras partidas en
    /// los espacios, acotadas a la duración y en orden; la primera de cada segmento abre párrafo.
    /// </summary>
    public static MediaTranscript FromTranscript(Transcript transcript, MediaRef media, string id, string language, string model, DateTime created)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(media);
        Time duration = media.Duration;
        var words = new List<SpokenWord>();
        foreach (TranscriptSegment segment in transcript.Segments.OrderBy(s => s.Start))
        {
            bool first = true;
            foreach ((double start, double end, string text, double confidence) in AutoSubtitles.WordsOf(segment))
            {
                Time s = Clamp(Time.FromSeconds(Math.Max(0, start)), duration);
                words.Add(new SpokenWord
                {
                    Start = s,
                    End = Clamp(Time.Max(s, Time.FromSeconds(Math.Max(0, end))), duration),
                    Text = text,
                    Confidence = double.IsFinite(confidence) ? Math.Clamp(confidence, 0, 1) : 1,
                    ParagraphStart = first,
                });
                first = false;
            }
        }

        return new MediaTranscript
        {
            Id = id,
            MediaId = media.Id,
            Language = transcript.Language is { Length: > 0 } found ? found : language,
            Model = model,
            Created = created,
            MediaDuration = duration,
            Words = [.. words.OrderBy(w => w.Start)],
        };
    }

    private static Time Clamp(Time t, Time duration) => t > duration ? duration : t;
}
