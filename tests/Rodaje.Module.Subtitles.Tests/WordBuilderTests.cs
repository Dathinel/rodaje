namespace Rodaje.Module.Subtitles.Tests;

public sealed class WordBuilderTests
{
    private static TokenTiming T(string text, double start, double end, double dtw = -1, double p = 0.9) => new(text, start, end, dtw, p);

    [Fact]
    public void Tokens_join_into_words_at_leading_spaces_and_use_dtw_starts()
    {
        List<TranscriptWord> words = WordBuilder.Build(
            [T("[_BEG_]", 0, 0), T(" Hola", 0.0, 0.4, 0.12), T(",", 0.4, 0.45, 0.41), T(" mun", 0.5, 0.7, 0.55), T("do", 0.7, 0.9, 0.72, 0.5), T("<|endoftext|>", 1, 1)],
            0, 1.2, "Hola, mundo");
        Assert.Equal(2, words.Count);
        Assert.Equal(new TranscriptWord(0.12, 0.45, "Hola,", 0.9), words[0]);
        Assert.Equal("mundo", words[1].Text);
        Assert.Equal(0.55, words[1].Start);
        Assert.Equal(0.9, words[1].End);
        Assert.Equal(0.7, words[1].P);
    }

    [Fact]
    public void Without_dtw_the_token_timestamps_are_used_and_times_stay_inside_the_segment_and_ordered()
    {
        List<TranscriptWord> words = WordBuilder.Build([T(" a", 0.9, 1.3), T(" b", 0.8, 1.0), T(" c", 5, 6)], 1.0, 2.0, "a b c");
        Assert.Equal([1.0, 1.0, 2.0], words.Select(w => w.Start));
        Assert.All(words, w => Assert.InRange(w.End, w.Start, 2.0));
    }

    [Fact]
    public void Broken_utf8_tokens_take_the_text_from_the_segment()
    {
        List<TranscriptWord> words = WordBuilder.Build([T(" est�", 0, 0.2, 0.05), T("�", 0.2, 0.3, 0.2), T(" bien", 0.3, 0.6, 0.31)], 0, 1, "está bien");
        Assert.Equal(["está", "bien"], words.Select(w => w.Text));
    }

    [Fact]
    public void Corpus_alignment_counts_errors_and_matches_words()
    {
        (double wer, int[] match) = Corpus.Align(["a", "b", "c", "d"], ["a", "x", "c", "d", "e"]);
        Assert.Equal(0.5, wer);
        Assert.Equal([0, -1, 2, 3], match);
        Assert.Equal(["hola", "qué", "tal", "1990"], Corpus.Normalize("¡Hola! ¿Qué tal? 1990."));
    }
}
