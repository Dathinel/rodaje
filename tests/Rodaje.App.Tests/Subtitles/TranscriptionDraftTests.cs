using Rodaje.App.Views.Subtitles;
using Rodaje.Core.Editing;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Subtitles;

/// <summary>
/// La propuesta del diálogo «Subtítulos automáticos»: una transcripción nueva que falla o se cancela no deja aplicar la
/// anterior (la del idioma de antes).
/// </summary>
public sealed class TranscriptionDraftTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static IReadOnlyList<SpeechSegment> Segments(string text) => [new SpeechSegment(S(1), S(2), text, [new SpeechWord(S(1), S(2), text)])];

    private static IReadOnlyList<SubtitleCue> Cues(string text) => [new SubtitleCue(S(1), S(2), text)];

    [Fact]
    public void Nueva_no_hay_propuesta_y_se_puede_transcribir()
    {
        var draft = new TranscriptionDraft();

        Assert.False(draft.Running);
        Assert.False(draft.CanApply);
        Assert.Null(draft.Segments);
        Assert.Empty(draft.Cues);
        Assert.True(draft.CanTranscribe("auto"));
    }

    [Fact]
    public void Mientras_transcribe_no_se_aplica_ni_se_lanza_otra()
    {
        var draft = new TranscriptionDraft();
        draft.Begin();

        Assert.True(draft.Running);
        Assert.False(draft.CanApply);
        Assert.False(draft.CanTranscribe("es"));
    }

    [Fact]
    public void Al_terminar_bien_se_aplica_y_solo_se_repite_con_otro_idioma()
    {
        var draft = new TranscriptionDraft();
        draft.Begin();
        draft.Complete("es", Segments("Hola"), Cues("Hola"));

        Assert.False(draft.Running);
        Assert.True(draft.CanApply);
        Assert.Equal("es", draft.Language);
        Assert.Equal("Hola", Assert.Single(draft.Cues).Text);
        Assert.False(draft.CanTranscribe("es"));
        Assert.True(draft.CanTranscribe("en"));
    }

    [Fact]
    public void Sin_voz_no_se_aplica()
    {
        var draft = new TranscriptionDraft();
        draft.Begin();
        draft.Complete("es", [], []);

        Assert.False(draft.CanApply);
        Assert.False(draft.CanTranscribe("es"));
    }

    [Fact]
    public void Empezar_otra_descarta_la_anterior()
    {
        var draft = new TranscriptionDraft();
        draft.Begin();
        draft.Complete("es", Segments("Hola"), Cues("Hola"));

        draft.Begin();

        Assert.Null(draft.Segments);
        Assert.Empty(draft.Cues);
        Assert.Equal("", draft.Language);
        Assert.False(draft.CanApply);
    }

    [Fact]
    public void Si_la_de_otro_idioma_falla_no_queda_la_anterior_para_aplicar()
    {
        var draft = new TranscriptionDraft();
        draft.Begin();
        draft.Complete("es", Segments("Hola"), Cues("Hola"));

        // Cambia el idioma, «Transcribir de nuevo» y el módulo falla (o se cancela).
        draft.Begin();
        draft.Fail();

        Assert.False(draft.Running);
        Assert.False(draft.CanApply);
        Assert.Null(draft.Segments);
        Assert.Empty(draft.Cues);
        // Sin propuesta, se puede reintentar cualquier idioma, también el que estaba.
        Assert.True(draft.CanTranscribe("en"));
        Assert.True(draft.CanTranscribe("es"));
    }

    [Fact]
    public void Tras_un_fallo_otra_que_termina_bien_vuelve_a_dejar_aplicar()
    {
        var draft = new TranscriptionDraft();
        draft.Begin();
        draft.Fail();
        draft.Begin();
        draft.Complete("en", Segments("Hello"), Cues("Hello"));

        Assert.True(draft.CanApply);
        Assert.Equal("Hello", Assert.Single(draft.Cues).Text);
        Assert.Equal("en", draft.Language);
    }

    [Fact]
    public void Complete_rechaza_nulos()
    {
        var draft = new TranscriptionDraft();
        draft.Begin();

        Assert.Throws<ArgumentNullException>(() => draft.Complete(null!, [], []));
        Assert.Throws<ArgumentNullException>(() => draft.Complete("es", null!, []));
        Assert.Throws<ArgumentNullException>(() => draft.Complete("es", [], null!));
    }
}
