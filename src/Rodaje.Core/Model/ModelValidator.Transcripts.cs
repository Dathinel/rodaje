using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>Reglas de las transcripciones de los medios (<c>transcripts</c>, formato 16, D-061).</summary>
public static partial class ModelValidator
{
    /// <summary>Palabras como mucho en una transcripción: una hora de habla son unas 9 000.</summary>
    public const int MaxTranscriptWords = 200_000;

    /// <summary>Caracteres como mucho del idioma o del modelo de una transcripción.</summary>
    private const int MaxTranscriptInfoLength = 128;

    /// <summary>
    /// Una transcripción por medio, de un medio que existe y no es una imagen fija; hablantes con identificador
    /// único; palabras con texto de una palabra, tiempos dentro de la duración y en orden de inicio, confianza de
    /// 0 a 1 y hablante declarado.
    /// </summary>
    private static void ValidateTranscripts(Project project, List<string> errors, Action<string, string> checkId)
    {
        if (project.Transcripts is not { } transcripts)
        {
            errors.Add("La lista de transcripciones del proyecto es nula.");
            return;
        }

        var transcribed = new HashSet<string>(StringComparer.Ordinal);
        foreach (MediaTranscript? transcript in transcripts)
        {
            if (transcript is null)
            {
                errors.Add("Hay una transcripción vacía (null).");
                continue;
            }

            checkId(transcript.Id, "una transcripción");
            string where = $"La transcripción «{transcript.Id}»";

            MediaRef? media = transcript.MediaId is { Length: > 0 } mediaId ? project.FindMedia(mediaId) : null;
            if (media is null)
            {
                errors.Add($"{where} es del medio «{transcript.MediaId}», que no existe.");
            }
            else if (media.IsStillImage)
            {
                errors.Add($"{where} es de «{media.Id}», que es una imagen fija.");
            }
            else if (!transcribed.Add(media.Id))
            {
                errors.Add($"{where} es de «{media.Id}», que ya tiene otra transcripción.");
            }

            if (transcript.MediaDuration < Time.Zero)
            {
                errors.Add($"{where} tiene una duración de medio negativa.");
            }

            if (!IsDisplayText(transcript.Language, MaxTranscriptInfoLength)
                || !IsDisplayText(transcript.Model, MaxTranscriptInfoLength))
            {
                errors.Add($"{where} tiene un idioma o un modelo nulo, de más de {MaxTranscriptInfoLength} caracteres o con caracteres de control.");
            }

            var speakers = new HashSet<string>(StringComparer.Ordinal);
            if (transcript.Speakers is { } speakerList)
            {
                foreach (TranscriptSpeaker? speaker in speakerList)
                {
                    if (speaker is null)
                    {
                        errors.Add($"{where} tiene un hablante vacío (null).");
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(speaker.Id) || speaker.Id.Any(char.IsControl))
                    {
                        errors.Add($"{where} tiene un hablante sin identificador o con caracteres de control.");
                        break;
                    }

                    if (!speakers.Add(speaker.Id))
                    {
                        errors.Add($"{where} repite el hablante «{speaker.Id}».");
                        break;
                    }

                    if (speaker.Color is not null && !MarkerStyle.IsColor(speaker.Color))
                    {
                        errors.Add($"{where} tiene un hablante con un color que no es #RRGGBB: «{speaker.Color}».");
                        break;
                    }
                }
            }
            else
            {
                errors.Add($"{where} tiene la lista de hablantes nula.");
            }

            if (transcript.Words is not { } words)
            {
                errors.Add($"{where} tiene la lista de palabras nula.");
                continue;
            }

            if (words.Count > MaxTranscriptWords)
            {
                errors.Add($"{where} tiene más de {MaxTranscriptWords} palabras.");
                continue;
            }

            bool hasDuration = transcript.MediaDuration >= Time.Zero;
            SpokenWord? previous = null;
            foreach (SpokenWord? word in words)
            {
                if (word is null)
                {
                    errors.Add($"{where} tiene una palabra vacía (null).");
                    break;
                }

                if (word.Text is not { Length: > 0 and <= MaxWordLength } text
                    || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                {
                    errors.Add($"{where} tiene una palabra vacía, de más de {MaxWordLength} caracteres o con espacios o caracteres de control.");
                    break;
                }

                if (word.Start < Time.Zero || word.End < word.Start || (hasDuration && word.End > transcript.MediaDuration)
                    || (previous is not null && word.Start < previous.Start))
                {
                    errors.Add($"{where} tiene palabras con tiempos negativos, que terminan antes de empezar, que pasan de la duración del medio o fuera de orden.");
                    break;
                }

                if (!double.IsFinite(word.Confidence) || word.Confidence is < 0 or > 1)
                {
                    errors.Add($"{where} tiene una palabra con una confianza fuera de 0 a 1.");
                    break;
                }

                if (word.SpeakerId is { } speaker && transcript.Speakers is not null && !speakers.Contains(speaker))
                {
                    errors.Add($"{where} tiene una palabra del hablante «{speaker}», que no está declarado.");
                    break;
                }

                previous = word;
            }
        }
    }

    /// <summary>Caracteres de las palabras de las transcripciones (cuentan para el tope del proyecto).</summary>
    private static long TranscriptsLength(Project project) =>
        project.Transcripts?.Sum(t => t?.Words?.Sum(w => (long)(w?.Text?.Length ?? 0)) ?? 0) ?? 0;
}
