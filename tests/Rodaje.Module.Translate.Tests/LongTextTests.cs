using System.Text.Json;
using Rodaje.App.Services.Translate;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Module.Translate.Tests;

/// <summary>Textos de más de 500 caracteres en «Traducir pista»: se parten (sin romper emojis) y vuelven juntos.</summary>
public sealed class LongTextTests
{
    private static Clip Text(string id, double start, string text) => new()
    {
        Id = id,
        Start = Time.FromSeconds(start),
        SourceIn = Time.Zero,
        SourceOut = Time.FromSeconds(2),
        Generator = new ClipGenerator { Text = text, Style = new TextStyle { Size = 48 } },
    };

    private static Project Sample(params Clip[] clips) => new()
    {
        Id = "p",
        Sequences =
        [
            new Sequence
            {
                Id = "seq",
                VideoTracks = [new Track { Id = "v1", Kind = TrackKind.Video, Name = "Subtítulos", Role = TrackRoles.Subtitles, Clips = [.. clips] }],
                AudioTracks = [new Track { Id = "a1", Kind = TrackKind.Audio }],
            },
        ],
    };

    [Fact]
    public void Five_hundred_and_one_characters_are_split_not_cut()
    {
        string text = new string('a', 300) + " " + new string('b', 200);
        Assert.Equal(501, text.Length);
        IReadOnlyList<SubtitleText> texts = SubtitleTrackTranslation.Collect(Sample(Text("s1", 0, text)).Sequences[0].VideoTracks[0]);

        Assert.Equal(["s1", "s1#2"], texts.Select(t => t.ClipId));
        Assert.Equal(new string('a', 300), texts[0].Text);
        Assert.Equal(new string('b', 200), texts[1].Text);
        Assert.All(texts, t => Assert.True(t.Text.Length <= SubtitleTrackTranslation.MaxTextLength));
    }

    [Fact]
    public void Exactly_five_hundred_characters_go_whole()
    {
        string text = new('a', 500);
        IReadOnlyList<SubtitleText> texts = SubtitleTrackTranslation.Collect(Sample(Text("s1", 0, text)).Sequences[0].VideoTracks[0]);
        Assert.Equal([new SubtitleText("s1", text)], texts);
    }

    [Fact]
    public void An_emoji_on_the_edge_is_not_split_and_the_request_is_valid_json()
    {
        string text = new string('a', 499) + "😀" + new string('c', 50);
        Assert.True(char.IsHighSurrogate(text[499]));
        IReadOnlyList<SubtitleText> texts = SubtitleTrackTranslation.Collect(Sample(Text("s1", 0, text)).Sequences[0].VideoTracks[0]);

        Assert.Equal(new string('a', 499), texts[0].Text);
        Assert.Equal("😀" + new string('c', 50), texts[1].Text);
        Assert.Equal(text, string.Concat(texts.Select(t => t.Text)));

        JsonElement call = Assert.Single(SubtitleTrackTranslation.Requests(texts, "es", "en"));
        Assert.Equal(["s1", "s1#2"], call.GetProperty("segments").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        Assert.Equal("😀" + new string('c', 50), call.GetProperty("segments")[1].GetProperty("text").GetString());
    }

    [Fact]
    public void Splits_prefer_the_end_of_a_sentence_and_never_exceed_the_limit()
    {
        string sentence = "Esta es una frase de prueba bastante larga. ";
        string text = string.Concat(Enumerable.Repeat(sentence, 40)).Trim();
        IReadOnlyList<string> parts = SubtitleTrackTranslation.Split(text, SubtitleTrackTranslation.MaxTextLength);

        Assert.True(parts.Count >= 4);
        Assert.All(parts, p => Assert.InRange(p.Length, 1, SubtitleTrackTranslation.MaxTextLength));
        Assert.All(parts, p => Assert.EndsWith(".", p, StringComparison.Ordinal));
        Assert.Equal(text, string.Join(' ', parts));
        Assert.Equal([""], SubtitleTrackTranslation.Split("", 500));
        Assert.Equal(["x"], SubtitleTrackTranslation.Split("x", 500));
    }

    [Fact]
    public void The_parts_come_back_joined_in_order_in_the_translated_clip()
    {
        string text = new string('a', 300) + " " + new string('b', 200);
        Project project = Sample(Text("s1", 0, text), Text("s2", 3, "Corto."));
        Track track = project.Sequences[0].VideoTracks[0];
        string[] ids = [.. SubtitleTrackTranslation.Collect(track).Select(t => t.ClipId)];
        Assert.Equal(["s1", "s1#2", "s2"], ids);

        Dictionary<string, string> result = SubtitleTrackTranslation.ParseResult(
            JsonDocument.Parse("""{"segments":[{"id":"s1","text":"AAA"},{"id":"s1#2","text":"BBB"},{"id":"s2","text":"Short."}],"untranslated":["s1#2"]}""").RootElement.Clone(),
            ids, out IReadOnlyList<string> untranslated);
        Assert.Equal(["s1"], untranslated);

        SequenceEditCommand command = SubtitleTrackTranslation.Command("seq", "v1", result, "Subtítulos (Inglés)", new SequentialIdGenerator());
        command.Apply(project);
        Track translated = project.Sequences[0].VideoTracks[1];
        Assert.Equal(["AAA\nBBB", "Short."], translated.Clips.Select(c => c.Generator!.Text));
    }

    [Fact]
    public void A_joined_translation_that_would_be_invalid_is_kept_within_the_limits()
    {
        Project project = Sample(Text("s1", 0, "x"));
        Dictionary<string, string> result = new() { ["s1"] = string.Join('\n', Enumerable.Repeat("línea", ModelValidator.MaxTextLines)) };
        for (int k = 2; k <= 25; k++)
        {
            result[SubtitleTrackTranslation.PartId("s1", k)] = new string('z', 499) + "😀";
        }

        SubtitleTrackTranslation.Command("seq", "v1", result, "T", new SequentialIdGenerator()).Apply(project);
        string text = project.Sequences[0].VideoTracks[1].Clips[0].Generator!.Text;
        Assert.DoesNotContain('\n', text);
        Assert.True(text.Length <= ModelValidator.MaxTextLength);
        Assert.False(char.IsHighSurrogate(text[^1]));
    }
}
