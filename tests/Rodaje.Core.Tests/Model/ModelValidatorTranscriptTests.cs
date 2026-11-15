using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Model.ModelSamples;

namespace Rodaje.Core.Tests.Model;

/// <summary>Reglas del validador para las transcripciones de los medios (formato 16, D-061).</summary>
public class ModelValidatorTranscriptTests
{
    private static MediaTranscript Transcript(string id = "t1", string mediaId = "m1") => new()
    {
        Id = id,
        MediaId = mediaId,
        Language = "es",
        Model = "large-v3-turbo-q5_0",
        Created = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc),
        MediaDuration = Sec(60),
        Speakers = [new TranscriptSpeaker { Id = "sp1", Name = "Ana", Color = "#3A7BD5" }],
        Words =
        [
            new SpokenWord { Start = Sec(0), End = Sec(0.5), Text = "Hola", Confidence = 0.9, SpeakerId = "sp1", ParagraphStart = true },
            new SpokenWord { Start = Sec(0.5), End = Sec(1), Text = "mundo", Confidence = 0.8 },
        ],
    };

    private static Project WithTranscript()
    {
        Project p = ValidProject();
        p.Transcripts.Add(Transcript());
        return p;
    }

    /// <summary>Como <see cref="ModelValidatorTests"/>: un cambio hostil deja exactamente un error.</summary>
    private static void AssertSingleError(Action<Project> breakIt, string expected)
    {
        Project p = WithTranscript();
        breakIt(p);
        Assert.Equal([expected], ModelValidator.Validate(p));
    }

    [Fact]
    public void Valid_transcript_passes()
    {
        Project p = WithTranscript();
        p.Transcripts.Add(Transcript("t2", "m2"));
        Assert.Empty(ModelValidator.Validate(p));
    }

    [Fact]
    public void Null_transcript_or_lists()
    {
        AssertSingleError(p => p.Transcripts.Add(null!), "Hay una transcripción vacía (null).");
        AssertSingleError(p => p.Transcripts = null!, "La lista de transcripciones del proyecto es nula.");
        AssertSingleError(p => p.Transcripts[0].Words = null!, "La transcripción «t1» tiene la lista de palabras nula.");
        AssertSingleError(p => p.Transcripts[0].Speakers = null!, "La transcripción «t1» tiene la lista de hablantes nula.");
    }

    [Fact]
    public void Duplicate_transcript_ids()
    {
        AssertSingleError(p => p.Transcripts.Add(Transcript("t1", "m2")), "El identificador «t1» está repetido (una transcripción).");
        AssertSingleError(p => p.Transcripts[0].Id = "c1", "El identificador «c1» está repetido (una transcripción).");
        AssertSingleError(p => p.Transcripts[0].Id = "  ", "Hay una transcripción sin identificador.");
    }

    [Fact]
    public void Transcript_of_missing_media()
    {
        AssertSingleError(p => p.Transcripts[0].MediaId = "m9", "La transcripción «t1» es del medio «m9», que no existe.");
        AssertSingleError(p => p.Transcripts[0].MediaId = "", "La transcripción «t1» es del medio «», que no existe.");
    }

    [Fact]
    public void Two_transcripts_of_the_same_media()
    {
        AssertSingleError(
            p => p.Transcripts.Add(Transcript("t2", "m1")),
            "La transcripción «t2» es de «m1», que ya tiene otra transcripción.");
    }

    [Fact]
    public void Transcript_of_a_still_image()
    {
        AssertSingleError(
            p =>
            {
                p.Media.Add(new MediaRef { Id = "img", IsStillImage = true, HasVideo = true });
                p.Transcripts[0].MediaId = "img";
            },
            "La transcripción «t1» es de «img», que es una imagen fija.");
    }

    [Fact]
    public void Negative_media_duration()
    {
        AssertSingleError(p => p.Transcripts[0].MediaDuration = Time.FromTicks(-1), "La transcripción «t1» tiene una duración de medio negativa.");
    }

    [Fact]
    public void Language_or_model_not_displayable()
    {
        string expected = "La transcripción «t1» tiene un idioma o un modelo nulo, de más de 128 caracteres o con caracteres de control.";
        AssertSingleError(p => p.Transcripts[0].Language = "es\u0001", expected);
        AssertSingleError(p => p.Transcripts[0].Language = new string('x', 129), expected);
        AssertSingleError(p => p.Transcripts[0].Language = null!, expected);
        AssertSingleError(p => p.Transcripts[0].Model = "whisper\nlarge", expected);
    }

    [Fact]
    public void Speaker_problems()
    {
        AssertSingleError(p => p.Transcripts[0].Speakers.Add(null!), "La transcripción «t1» tiene un hablante vacío (null).");
        AssertSingleError(
            p => p.Transcripts[0].Speakers.Add(new TranscriptSpeaker { Id = " " }),
            "La transcripción «t1» tiene un hablante sin identificador o con caracteres de control.");
        AssertSingleError(
            p => p.Transcripts[0].Speakers.Add(new TranscriptSpeaker { Id = "sp\tx" }),
            "La transcripción «t1» tiene un hablante sin identificador o con caracteres de control.");
        AssertSingleError(
            p => p.Transcripts[0].Speakers.Add(new TranscriptSpeaker { Id = "sp1" }),
            "La transcripción «t1» repite el hablante «sp1».");
        AssertSingleError(
            p => p.Transcripts[0].Speakers[0].Color = "rojo",
            "La transcripción «t1» tiene un hablante con un color que no es #RRGGBB: «rojo».");
        AssertSingleError(
            p => p.Transcripts[0].Speakers[0].Color = "#FFF",
            "La transcripción «t1» tiene un hablante con un color que no es #RRGGBB: «#FFF».");
    }

    [Fact]
    public void Null_or_bad_word_text()
    {
        string expected = $"La transcripción «t1» tiene una palabra vacía, de más de {ModelValidator.MaxWordLength} caracteres o con espacios o caracteres de control.";
        AssertSingleError(p => p.Transcripts[0].Words.Add(null!), "La transcripción «t1» tiene una palabra vacía (null).");
        AssertSingleError(p => p.Transcripts[0].Words[0].Text = "", expected);
        AssertSingleError(p => p.Transcripts[0].Words[0].Text = "dos palabras", expected);
        AssertSingleError(p => p.Transcripts[0].Words[0].Text = "a\u0001b", expected);
        AssertSingleError(p => p.Transcripts[0].Words[0].Text = new string('a', 101), expected);
    }

    [Fact]
    public void Word_times_out_of_range_or_order()
    {
        string expected = "La transcripción «t1» tiene palabras con tiempos negativos, que terminan antes de empezar, que pasan de la duración del medio o fuera de orden.";
        AssertSingleError(p => p.Transcripts[0].Words[0].Start = Sec(-1), expected);
        AssertSingleError(p => p.Transcripts[0].Words[1].End = Sec(61), expected);
        AssertSingleError(p => p.Transcripts[0].Words[0].End = Sec(-0.1), expected);
        AssertSingleError(
            p =>
            {
                p.Transcripts[0].Words[0].Start = Sec(0.9);
                p.Transcripts[0].Words[0].End = Sec(1);
                p.Transcripts[0].Words[1].End = Sec(0.6);
            },
            expected);
    }

    [Fact]
    public void Word_confidence_out_of_range()
    {
        string expected = "La transcripción «t1» tiene una palabra con una confianza fuera de 0 a 1.";
        AssertSingleError(p => p.Transcripts[0].Words[0].Confidence = double.NaN, expected);
        AssertSingleError(p => p.Transcripts[0].Words[0].Confidence = 1.5, expected);
        AssertSingleError(p => p.Transcripts[0].Words[0].Confidence = -0.1, expected);
        AssertSingleError(p => p.Transcripts[0].Words[0].Confidence = double.PositiveInfinity, expected);
    }

    [Fact]
    public void Word_of_undeclared_speaker()
    {
        AssertSingleError(
            p => p.Transcripts[0].Words[0].SpeakerId = "sp9",
            "La transcripción «t1» tiene una palabra del hablante «sp9», que no está declarado.");
        AssertSingleError(
            p => p.Transcripts[0].Words[0].SpeakerId = "",
            "La transcripción «t1» tiene una palabra del hablante «», que no está declarado.");
    }

    [Fact]
    public void Too_many_words()
    {
        AssertSingleError(
            p => p.Transcripts[0].Words =
            [
                .. Enumerable.Range(0, ModelValidator.MaxTranscriptWords + 1).Select(i => new SpokenWord { Text = "x" }),
            ],
            $"La transcripción «t1» tiene más de {ModelValidator.MaxTranscriptWords} palabras.");
    }

    [Fact]
    public void Transcript_words_count_toward_the_text_cap()
    {
        Project p = WithTranscript();
        string word = new('a', 100);
        p.Transcripts[0].Words =
        [
            .. Enumerable.Range(0, ModelValidator.MaxProjectTextLength / 100 + 1).Select(i => new SpokenWord { Text = word }),
        ];

        string error = Assert.Single(ModelValidator.Validate(p));
        Assert.Contains("Los textos del proyecto suman", error, StringComparison.Ordinal);
    }
}
