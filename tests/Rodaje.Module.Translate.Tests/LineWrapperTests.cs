using Rodaje.Module.Translate.Translation;

namespace Rodaje.Module.Translate.Tests;

public sealed class LineWrapperTests
{
    [Fact]
    public void A_short_text_stays_on_one_line()
    {
        Assert.Equal("Good morning, everyone.", LineWrapper.Wrap("  Good   morning,\neveryone. ", 42));
    }

    [Fact]
    public void Two_lines_are_balanced_with_the_shorter_one_on_top()
    {
        string wrapped = LineWrapper.Wrap("Today we are going to edit a video about the beach and the mountains", 42);
        string[] lines = wrapped.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.True(l.Length <= 42, l));
        Assert.True(Math.Abs(lines[0].Length - lines[1].Length) <= 8, wrapped);
        Assert.True(lines[0].Length <= lines[1].Length + 6, wrapped);
    }

    [Fact]
    public void Long_texts_use_more_lines_but_never_pass_the_limit()
    {
        string text = string.Join(' ', Enumerable.Repeat("subtitle words", 20));
        string[] lines = LineWrapper.Wrap(text, 42).Split('\n');
        Assert.True(lines.Length >= 3);
        Assert.All(lines, l => Assert.InRange(l.Length, 1, 42));
        Assert.Equal(text, string.Join(' ', lines));
    }

    [Fact]
    public void A_word_longer_than_the_line_is_cut_and_text_without_spaces_too()
    {
        string url = "https://example.com/" + new string('a', 60);
        string[] lines = LineWrapper.Wrap(url, 42).Split('\n');
        Assert.All(lines, l => Assert.True(LineWrapper.Length(l) <= 42));
        Assert.Equal(url, string.Concat(lines));

        string japanese = string.Concat(Enumerable.Repeat("今日は海に行きます。", 6));
        string[] jp = LineWrapper.Wrap(japanese, 16).Split('\n');
        Assert.All(jp, l => Assert.True(LineWrapper.Length(l) <= 16));
        Assert.Equal(japanese, string.Concat(jp));
    }

    [Fact]
    public void Combined_accents_and_emoji_count_as_one_character()
    {
        string decomposed = "Café";
        Assert.Equal(4, LineWrapper.Length(decomposed));
        Assert.Equal(1, LineWrapper.Length("👍🏽"));
    }

    [Fact]
    public void Empty_or_blank_text_wraps_to_empty()
    {
        Assert.Equal("", LineWrapper.Wrap(" \n\t ", 42));
    }
}
