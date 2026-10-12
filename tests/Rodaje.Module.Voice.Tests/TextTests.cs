using Rodaje.Module.Voice.Synthesis;
using Rodaje.Module.Voice.Text;

namespace Rodaje.Module.Voice.Tests;

public sealed class TextTests
{
    private static string Words(string text, Language language) =>
        string.Join(' ', Script.Parse(text, language, 0, 0).SelectMany(s => s.Tokens).Where(t => t.Kind == TokenKind.Word).Select(t => t.Text));

    [Theory]
    [InlineData("21 años", "veintiuno años")]
    [InlineData("1.234,5", "mil doscientos treinta y cuatro coma cinco")]
    [InlineData("1000000", "un millón")]
    [InlineData("21000", "veintiún mil")]
    [InlineData("el 20%", "el veinte por ciento")]
    [InlineData("$5", "cinco dólares")]
    [InlineData("10:30", "diez treinta")]
    [InlineData("1º", "primero")]
    [InlineData("siglo XVIII", "siglo dieciocho")]
    [InlineData("la IA y el PDF", "la i a y el pe de efe")]
    [InlineData("Sr. Pérez", "señor pérez")]
    [InlineData("EE. UU.", "estados unidos")]
    [InlineData("3 km", "tres kilómetros")]
    public void Spanish_numbers_symbols_and_abbreviations(string text, string expected) => Assert.Equal(expected, Words(text, Language.Spanish));

    [Theory]
    [InlineData("1.200", "mil e duzentos")]
    [InlineData("1.234", "mil duzentos e trinta e quatro")]
    [InlineData("R$10", "dez reais")]
    [InlineData("2,5", "dois vírgula cinco")]
    [InlineData("16", "dezesseis")]
    public void Portuguese_numbers(string text, string expected) => Assert.Equal(expected, Words(text, Language.PortugueseBrazil));

    [Theory]
    [InlineData("1,234.5", "one thousand two hundred thirty four point five")]
    [InlineData("21st", "twenty first")]
    [InlineData("42", "forty two")]
    public void English_numbers(string text, string expected) => Assert.Equal(expected, Words(text, Language.EnglishUs));

    [Fact]
    public void Sentences_split_at_the_end_marks_and_line_breaks_with_the_requested_pause()
    {
        List<Sentence> s = Script.Parse("Hola. ¿Qué tal?\nBien, gracias", Language.Spanish, 0.4, 0);
        Assert.Equal(3, s.Count);
        Assert.All(s, x => Assert.Equal(0.4, x.PauseAfter));
        Assert.Equal("bien , gracias", string.Join(' ', s[2].Tokens.Select(t => t.Text)));
    }

    [Fact]
    public void Comma_pause_splits_clauses()
    {
        List<Sentence> s = Script.Parse("Uno, dos; tres.", Language.Spanish, 0.5, 0.2);
        Assert.Equal([0.2, 0.2, 0.5], s.Select(x => x.PauseAfter));
    }

    [Theory]
    [InlineData("Hola [pausa] mundo", 0.5)]
    [InlineData("Hola [pausa 1.5] mundo", 1.5)]
    [InlineData("Hola [pausa 2,5s] mundo", 2.5)]
    [InlineData("Hola [pause 300ms] mundo", 0.3)]
    [InlineData("Hola [pausa 99] mundo", 10)]
    public void Pause_markup_adds_silence_between_sentences(string text, double pause)
    {
        List<Sentence> s = Script.Parse(text, Language.Spanish, 0.4, 0);
        Assert.Equal(2, s.Count);
        Assert.Equal(pause, s[0].PauseAfter, 6);
        Assert.Equal("mundo", s[1].Tokens.Single().Text);
    }

    [Fact]
    public void A_long_sentence_is_split_into_chunks_that_fit_the_model()
    {
        string text = string.Join(", ", Enumerable.Repeat("el murciélago comía kiwi con cardillo en la universidad", 20)) + ".";
        var plan = Synthesizer.Plan(new SpeechRequest(text, Language.Spanish, "ef_dora"), Phonemes.Phonemizers.For(Language.Spanish, null));
        Assert.True(plan.Count > 1);
        Assert.All(plan, p => Assert.InRange(KokoroEngine.CountTokens(p.Phonemes), 1, KokoroEngine.MaxTokens));
        // Se corta en las comas: ningún trozo empieza a mitad de la frase repetida.
        Assert.All(plan, p => Assert.StartsWith("el", p.Text, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"text":"hola","language":"es-419","voice":"em_alex","speed":5,"pitch":-20}""", 2, -6)]
    [InlineData("""{"text":"hola"}""", 1, 0)]
    public void Parameters_are_clamped(string json, double speed, double pitch)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        SpeechRequest r = VoiceFunctions.ParseRequest(doc.RootElement);
        Assert.Equal(speed, r.Speed);
        Assert.Equal(pitch, r.Pitch);
    }

    [Theory]
    [InlineData("""{"language":"es"}""")]
    [InlineData("""{"text":"   "}""")]
    [InlineData("""{"text":"hola","language":"fr"}""")]
    [InlineData("""{"text":"hola","voice":"zz_nadie"}""")]
    [InlineData("""{"text":"hola","speed":"rápido"}""")]
    public void Bad_parameters_are_rejected(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var ex = Assert.Throws<Rodaje.Modules.Protocol.ModuleCallException>(() => VoiceFunctions.ParseRequest(doc.RootElement));
        Assert.Equal(Rodaje.Modules.Protocol.ErrorCodes.InvalidParameters, ex.Code);
    }
}
