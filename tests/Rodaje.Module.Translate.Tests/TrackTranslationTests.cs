using System.Text.Json;
using Rodaje.App.Services.Translate;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Module.Translate.Tests;

/// <summary>«Traducir pista» en la app (sin WinUI): qué se pide, qué se acepta y la pista nueva con los mismos tiempos.</summary>
public sealed class TrackTranslationTests
{
    private static Clip Text(string id, double start, double seconds, string text, string? link = null) => new()
    {
        Id = id,
        Start = Time.FromSeconds(start),
        SourceIn = Time.Zero,
        SourceOut = Time.FromSeconds(seconds),
        LinkId = link,
        Generator = new ClipGenerator { Text = text, Style = new TextStyle { Size = 48 } },
    };

    private static Project Sample()
    {
        Track video = new() { Id = "v1", Kind = TrackKind.Video, Name = "Video" };
        Track subtitles = new()
        {
            Id = "v2",
            Kind = TrackKind.Video,
            Name = "Subtítulos",
            Role = TrackRoles.Subtitles,
            Clips = [Text("s1", 1, 2, "Hola a todos.", "enlace"), Text("s2", 3.5, 1.5, "¿Empezamos?"), Text("s3", 6, 3, "Vamos\na la playa.")],
        };
        Track titles = new() { Id = "v3", Kind = TrackKind.Video, Name = "Texto", Clips = [Text("t1", 0, 5, "Mi viaje", "enlace")] };
        return new Project
        {
            Id = "p",
            Sequences = [new Sequence { Id = "seq", VideoTracks = [video, subtitles, titles], AudioTracks = [new Track { Id = "a1", Kind = TrackKind.Audio }] }],
        };
    }

    private static JsonElement Json(string json)
    {
        using JsonDocument d = JsonDocument.Parse(json);
        return d.RootElement.Clone();
    }

    [Fact]
    public void Subtitle_tracks_come_first_and_the_selection_picks_its_track()
    {
        Sequence s = Sample().Sequences[0];
        Assert.Equal(["v2", "v3"], SubtitleTrackTranslation.TranslatableTracks(s).Select(t => t.Id));
        Assert.Equal("v2", SubtitleTrackTranslation.DefaultTrack(s, [])!.Id);
        Assert.Equal("v3", SubtitleTrackTranslation.DefaultTrack(s, ["t1"])!.Id);
        Assert.Equal(["s1", "s2", "s3"], SubtitleTrackTranslation.Collect(s.VideoTracks[1]).Select(t => t.ClipId));
    }

    [Fact]
    public void Requests_are_split_in_calls_of_at_most_400()
    {
        List<SubtitleText> texts = [.. Enumerable.Range(0, 900).Select(i => new SubtitleText($"c{i}", $"Texto {i}"))];
        IReadOnlyList<JsonElement> calls = SubtitleTrackTranslation.Requests(texts, null, "en");
        Assert.Equal([400, 400, 100], calls.Select(c => c.GetProperty("segments").GetArrayLength()));
        Assert.Equal("auto", calls[0].GetProperty("source").GetString());
        Assert.Equal("en", calls[0].GetProperty("target").GetString());
        Assert.Equal(42, calls[0].GetProperty("maxLineChars").GetInt32());
        Assert.Equal("c400", calls[1].GetProperty("segments")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void The_result_must_bring_exactly_the_requested_ids()
    {
        string[] ids = ["s1", "s2"];
        Dictionary<string, string> ok = SubtitleTrackTranslation.ParseResult(
            Json("""{"segments":[{"id":"s2","text":"Shall we start?\r\n "},{"id":"s1","text":"Hello\u0007 everyone."}],"untranslated":["s2","zz"]}"""), ids, out IReadOnlyList<string> untranslated);
        Assert.Equal("Hello  everyone.", ok["s1"]);
        Assert.Equal("Shall we start?", ok["s2"]);
        Assert.Equal(["s2"], untranslated);

        Assert.Throws<TranslationException>(() => SubtitleTrackTranslation.ParseResult(Json("""{"segments":[{"id":"s1","text":"a"}]}"""), ids, out _));
        Assert.Throws<TranslationException>(() => SubtitleTrackTranslation.ParseResult(Json("""{"segments":[{"id":"s1","text":"a"},{"id":"s1","text":"b"}]}"""), ids, out _));
        Assert.Throws<TranslationException>(() => SubtitleTrackTranslation.ParseResult(Json("""{"segments":[{"id":"s1","text":"a"},{"id":"x","text":"b"}]}"""), ids, out _));
        Assert.Throws<TranslationException>(() => SubtitleTrackTranslation.ParseResult(Json("""{"segments":[{"id":"s1"},{"id":"s2","text":"b"}]}"""), ids, out _));
        Assert.Throws<TranslationException>(() => SubtitleTrackTranslation.ParseResult(Json("""{"bytes":12}"""), ids, out _));
        Assert.Throws<TranslationException>(() => SubtitleTrackTranslation.ParseResult(null, ids, out _));
    }

    [Fact]
    public void The_new_track_sits_above_with_the_same_times_and_style_and_undoes_in_one_step()
    {
        Project project = Sample();
        Sequence s = project.Sequences[0];
        string before = JsonSerializer.Serialize(s.VideoTracks.Select(t => t.Clips.Select(c => (c.Id, c.Start.Ticks, c.Generator?.Text)).ToList()).ToList());
        Dictionary<string, string> texts = new() { ["s1"] = "Hello everyone.", ["s2"] = "Shall we start?", ["s3"] = "Let's go\nto the beach." };
        string name = SubtitleTrackTranslation.TrackName(s, s.VideoTracks[1], SubtitleTrackTranslation.Languages[1]);
        Assert.Equal("Subtítulos (Inglés)", name);

        SequenceEditCommand command = SubtitleTrackTranslation.Command("seq", "v2", texts, name, new SequentialIdGenerator());
        command.Apply(project);

        Assert.Equal(["v1", "v2", command.CreatedIds[^1], "v3"], s.VideoTracks.Select(t => t.Id));
        Track translated = s.VideoTracks[2];
        Assert.Equal(TrackRoles.Subtitles, translated.Role);
        Assert.Equal(name, translated.Name);
        Assert.Equal(["Hello everyone.", "Shall we start?", "Let's go\nto the beach."], translated.Clips.Select(c => c.Generator!.Text));
        for (int i = 0; i < 3; i++)
        {
            Clip original = s.VideoTracks[1].Clips[i];
            Clip copy = translated.Clips[i];
            Assert.NotEqual(original.Id, copy.Id);
            Assert.Equal(original.Start, copy.Start);
            Assert.Equal(original.End, copy.End);
            Assert.Equal(48, copy.Generator!.Style!.Size);
            Assert.Null(copy.LinkId);
            Assert.NotSame(original.Generator, copy.Generator);
        }

        Assert.Equal(["Hola a todos.", "¿Empezamos?", "Vamos\na la playa."], s.VideoTracks[1].Clips.Select(c => c.Generator!.Text));
        Assert.Equal(translated.Clips.Select(c => c.Id).Append(translated.Id), command.CreatedIds);

        command.Revert(project);
        Assert.Equal(before, JsonSerializer.Serialize(s.VideoTracks.Select(t => t.Clips.Select(c => (c.Id, c.Start.Ticks, c.Generator?.Text)).ToList()).ToList()));
        Assert.Equal("Subtítulos (Inglés) 2", SubtitleTrackTranslation.TrackName(WithTrackNamed(s, "Subtítulos (Inglés)"), s.VideoTracks[1], SubtitleTrackTranslation.Languages[1]));
    }

    [Fact]
    public void A_missing_or_locked_source_track_does_not_change_anything()
    {
        Project project = Sample();
        Dictionary<string, string> texts = new() { ["s1"] = "Hi" };
        Assert.Throws<EditException>(() => SubtitleTrackTranslation.Command("seq", "nope", texts, "X").Apply(project));
        Assert.Throws<EditException>(() => SubtitleTrackTranslation.Command("seq", "v2", new Dictionary<string, string>(), "X").Apply(project));
        Assert.Equal(3, project.Sequences[0].VideoTracks.Count);
    }

    private static Sequence WithTrackNamed(Sequence s, string name)
    {
        s.VideoTracks.Add(new Track { Id = "extra", Kind = TrackKind.Video, Name = name });
        return s;
    }
}
