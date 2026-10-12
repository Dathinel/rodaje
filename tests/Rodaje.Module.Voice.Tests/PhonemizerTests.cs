using Rodaje.Module.Voice.Phonemes;
using Rodaje.Module.Voice.Synthesis;
using Rodaje.Module.Voice.Text;

namespace Rodaje.Module.Voice.Tests;

/// <summary>
/// Corpus de palabras difíciles. Lo esperado es la pronunciación en el alfabeto de los datos de Kokoro (IPA al estilo de
/// espeak-ng, con «ʧ», «A» = eɪ, «I» = aɪ, «W» = aʊ); se revisó a mano y coincide con espeak-ng salvo donde se indica.
/// </summary>
public sealed class PhonemizerTests
{
    private static string Say(string text, Language language) =>
        string.Join(" ", Synthesizer.Plan(new SpeechRequest(text, language, "ef_dora", SentencePause: 0), Phonemizers.For(language, null))
            .Select(p => p.Phonemes).Where(p => p.Length > 0));

    [Theory]
    // Vibrante simple y múltiple, ll, gu, ü, ñ, j, ch
    [InlineData("perro", "pˈero")]
    [InlineData("calle", "kˈaʎe")]
    [InlineData("guitarra", "ɡitˈara")]
    [InlineData("pingüino", "piŋɡwˈino")] // espeak-ng: pˌiŋɡuˈino (hiato); en español es diptongo
    [InlineData("niño", "nˈiɲo")]
    [InlineData("jamón", "xamˈon")]
    [InlineData("mucho", "mˈuʧo")]
    // Diptongos, triptongos e hiatos
    [InlineData("bueno", "bwˈeno")]
    [InlineData("aire", "ˈIɾe")]
    [InlineData("auto", "ˈWto")]
    [InlineData("seis", "sˈAs")]
    [InlineData("hoy", "ˈoɪ")]
    [InlineData("muy", "mˈuj")]
    [InlineData("ciudad", "θjuðˈad")]
    [InlineData("cuidado", "kwiðˈaðo")]
    [InlineData("país", "paˈis")]
    [InlineData("Raúl", "raˈul")]
    [InlineData("leer", "leˈeɾ")]
    [InlineData("caos", "kˈaos")]
    [InlineData("Uruguay", "ˌuɾuɣwˈI")]
    [InlineData("huevo", "wˈeβo")]
    [InlineData("hielo", "jˈelo")]
    // Alófonos de b/d/g y asimilación de la nasal
    [InlineData("envío", "embˈio")]
    [InlineData("lago", "lˈaɣo")]
    [InlineData("cada", "kˈaða")]
    [InlineData("esdrújula", "esðɾˈuxula")]
    [InlineData("fútbol", "fˈutβol")]
    [InlineData("tengo", "tˈɛŋɡo")]
    [InlineData("ángel", "ˈaŋxel")]
    [InlineData("inyección", "ˌiɲekθjˈon")]
    [InlineData("cónyuge", "kˈoɲuxe")]
    [InlineData("el gato", "el ɣˈato")]
    [InlineData("un beso", "ˈum bˈeso")]
    [InlineData("la casa de Juan", "la kˈasa ðe xwˈan")]
    // x, ps, w, acentos secundarios, -mente, átonas
    [InlineData("examen", "eksˈamen")]
    [InlineData("xilófono", "silˈofono")]
    [InlineData("México", "mˈɛxiko")]
    [InlineData("psicología", "sˌikoloxˈia")]
    [InlineData("whisky", "wˈiski")]
    [InlineData("universidad", "ˌuniβˌeɾsiðˈad")]
    [InlineData("anticonstitucional", "ˌantikˌonstitˌuθjonˈal")]
    [InlineData("transatlántico", "tɾˌansatlˈantiko")]
    [InlineData("subrayar", "sˌuβɾaʝˈaɾ")]
    [InlineData("Israel", "ˌisraˈel")]
    [InlineData("reloj", "relˈox")]
    [InlineData("rápidamente", "rˈapiðamˈente")]
    [InlineData("lo que quieras", "lo ke kjˈeɾas")]
    public void Spanish(string text, string expected) => Assert.Equal(expected, Say(text, Language.Spanish));

    [Theory]
    [InlineData("zapato", "sapˈato")]
    [InlineData("cielo", "sjˈelo")]
    [InlineData("calle", "kˈaʝe")]
    [InlineData("corazón", "kˌoɾasˈon")]
    public void Latin_american_spanish_has_seseo_and_yeismo(string text, string expected) => Assert.Equal(expected, Say(text, Language.SpanishLatam));

    [Theory]
    // Vocales átonas finales, t/d ante [i], r fuerte y suave
    [InlineData("obrigado", "ˌobriɡˈadʊ")]
    [InlineData("cidade", "sˌidˈaʤy")]
    [InlineData("noite", "nˈoɪʧy")]
    [InlineData("dia", "ʤˈiæ")]
    [InlineData("diferente", "ʤˌifeɾˈAŋʧy")]
    [InlineData("rato", "xˈatʊ")]
    [InlineData("carro", "kˈaxʊ")]
    [InlineData("caro", "kˈaɾʊ")]
    [InlineData("porta", "pˈɔɾətæ")]
    [InlineData("tarde", "tˈaɾəʤy")]
    [InlineData("nascer", "nasˈer")]
    // Dígrafos y x
    [InlineData("minha", "mˈiɲæ")]
    [InlineData("filha", "fˈiljæ")]
    [InlineData("cachorro", "kˌaʃˈoxʊ")]
    [InlineData("chave", "ʃˈavy")]
    [InlineData("xícara", "ʃˈikæɾæ")]
    [InlineData("exemplo", "ˌezˈAmplʊ")]
    [InlineData("táxi", "tˈaksi")]
    [InlineData("próximo", "prˈɔsimʊ")]
    [InlineData("casa", "kˈazæ")]
    [InlineData("assim", "asˈiŋ")]
    [InlineData("mesmo", "mˈezmʊ")]
    // Nasales y diptongos nasales
    [InlineData("mãe", "mˈɐ̃y")]
    [InlineData("pães", "pˈɐ̃ys")]
    [InlineData("põe", "pˈõj")]
    [InlineData("ações", "asˈõjs")]
    [InlineData("lição", "lisˈɐ̃ʊ̃")]
    [InlineData("também", "tɐ̃mbˈAŋ")]
    [InlineData("homem", "ˈomAŋ")]
    [InlineData("garagem", "ɡˌaɾˈaʒAŋ")]
    [InlineData("gente", "ʒˈAŋʧy")]
    [InlineData("canto", "kˈɐ̃ŋtʊ")]
    [InlineData("campo", "kˈɐ̃mpʊ")]
    [InlineData("banana", "bˌænˈɐ̃næ")]
    [InlineData("irmã", "iɾəmˈɐ̃")]
    [InlineData("ontem", "ˈoŋtAŋ")]
    [InlineData("algum", "Wɡˈũŋ")]
    [InlineData("muito", "mwˈiŋtʊ")]
    // l final, vocales abiertas, qu/gu, acentos
    [InlineData("Brasil", "brazˈiʊ")]
    [InlineData("azul", "azˈuw")]
    [InlineData("café", "kafˈɛ")]
    [InlineData("avó", "avˈɔ")]
    [InlineData("avô", "avˈo")]
    [InlineData("quero", "kˈɛɾʊ")]
    [InlineData("guerra", "ɡˈɛxæ")]
    [InlineData("velho", "vˈɛljʊ")]
    [InlineData("água", "ˈaɡwæ")]
    [InlineData("quatro", "kwˈatrʊ")]
    [InlineData("frequente", "frˌekwˈAŋʧy")]
    [InlineData("português", "pˌoɾətuɡˈes")]
    [InlineData("pássaro", "pˈasæɾʊ")]
    [InlineData("distância", "ʤˌistˈɐ̃ŋsjæ")]
    [InlineData("cuidado", "kˌuɪdˈadʊ")]
    [InlineData("vídeo", "vˈiʤjʊ")]
    public void Brazilian_portuguese(string text, string expected) => Assert.Equal(Phones.Decompose(expected), Say(text, Language.PortugueseBrazil));

    [Theory]
    [InlineData("os amigos", "ʊz ˌæmˈiɡʊs")]
    [InlineData("de onde", "ʤj ˌoŋʤy")]
    public void Portuguese_links_words(string text, string expected) => Assert.Equal(Phones.Decompose(expected), Say(text, Language.PortugueseBrazil));

    [Theory]
    [InlineData("es", "El veloz murciélago hindú comía feliz cardillo y kiwi. ¿Qué tal, Daniel? Tengo 1.234,5 euros, el 20 % y 3 km.")]
    [InlineData("es-419", "Llegó ayer a la Ciudad de México con 2 maletas y un guitarrón; ¡qué lluvia!")]
    [InlineData("pt-BR", "Olá, tudo bem? Eu me chamo Daniel e moro no Brasil desde 2020. A cidade de São Paulo é muito grande, não é?")]
    public void Every_symbol_is_in_the_kokoro_vocabulary(string languageId, string text)
    {
        Assert.True(Languages.TryParse(languageId, out Language language));
        string phonemes = Say(text, language);
        Assert.NotEmpty(phonemes);
        Assert.All(phonemes, c => Assert.True(KokoroEngine.IsKnown(c), $"«{c}» (U+{(int)c:X4}) no está en el vocabulario: {phonemes}"));
    }

    [Fact]
    public void English_uses_the_lexicon_and_simple_suffix_rules()
    {
        EnglishLexicon lexicon = EnglishLexicon.FromEntries(new Dictionary<string, string>
        {
            ["hello"] = "həlˈO", ["world"] = "wˈɜɹld", ["cat"] = "kˈæt", ["walk"] = "wˈɔk", ["run"] = "ɹˈʌn", ["video"] = "vˈɪdiO",
            ["editor"] = "ˈɛdəɾəɹ", ["want"] = "wˈɑnt", ["a"] = "ˈA", ["b"] = "bˈi",
        });
        IPhonemizer en = Phonemizers.For(Language.EnglishUs, lexicon);
        string Say(string t) => en.Phonemize(Script.Parse(t, Language.EnglishUs, 0, 0)[0].Tokens);
        Assert.Equal("həlˈO, wˈɜɹld!", Say("Hello, world!"));
        Assert.Equal("kˈæts", Say("cats"));
        Assert.Equal("wˈɔkt", Say("walked"));
        Assert.Equal("wˈɑntᵻd", Say("wanted"));
        Assert.Equal("ɹˈʌnɪŋ", Say("running"));
        Assert.Equal("vˈɪdiOˈɛdəɾəɹ", Say("videoeditor"));
        Assert.Equal("ˈAbˈi", Say("ab"));
    }
}
