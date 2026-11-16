using System.Diagnostics;
using Rodaje.Core.Interchange;
using Rodaje.Core.Interchange.Xml;
using Rodaje.Core.Model;
using Xunit.Abstractions;
using Rational = Rodaje.Core.Timing.Rational;
using Time = Rodaje.Core.Timing.Time;

namespace Rodaje.Core.Tests.Interchange;

/// <summary>Lo común del intercambio (fase F3 de D-062): fusión, recorrido, comparación, rutas y XML.</summary>
public sealed class InterchangeCommonTests(ITestOutputHelper output)
{
    private static Time F(long frames) => Time.FromFrames(frames, new Rational(25, 1));

    private static (Project Project, Sequence Main) Draft()
    {
        TimelineDraft draft = RandomTimelines.Create(7);
        return (draft.Project, draft.Sequence);
    }

    [Fact]
    public void Importing_the_same_draft_twice_gives_new_ids_every_time_and_both_validate()
    {
        TimelineDraft draft = RandomTimelines.Create(3);
        var target = new Project { Id = "abierto", Name = "Abierto" };
        var ids = new SequentialIdGenerator();

        InterchangeResult first = SequenceBuilder.Build(draft, target, null, ids, new InterchangeReport());
        target.Media.AddRange(first.Media);
        target.Sequences.AddRange([.. first.Compounds, first.Sequence]);
        InterchangeResult second = SequenceBuilder.Build(draft, target, null, ids, new InterchangeReport());
        target.Sequences.AddRange([.. second.Compounds, second.Sequence]);

        Assert.Empty(second.Media); // los medios son los mismos archivos: se reutilizan
        Assert.Empty(ModelValidator.Validate(target));
        Assert.Null(Fidelity.Difference(draft, new TimelineDraft(target, second.Sequence)));
        Assert.DoesNotContain(draft.Sequence.Id, target.Sequences.Select(s => s.Id));
    }

    [Fact]
    public void Transitions_links_and_groups_follow_their_clips_to_the_new_ids()
    {
        (Project project, Sequence main) = Draft();
        Track track = main.VideoTracks.First(t => t.Transitions.Count > 0);
        foreach (Clip clip in new[] { main.VideoTracks[0], main.AudioTracks[0] }.Select(t => t.Clips.First(c => c.Speed is null)))
        {
            clip.LinkId = "enlace";
            clip.GroupId = "grupo";
        }

        InterchangeResult result = SequenceBuilder.Build(new TimelineDraft(project, main), new Project(), null, new SequentialIdGenerator(), new InterchangeReport());

        Track copy = result.Sequence.VideoTracks[main.VideoTracks.IndexOf(track)];
        Assert.All(copy.Transitions, t => Assert.NotNull(copy.Clips.Find(c => c.Id == t.From)));
        Assert.All(copy.Transitions, t => Assert.NotNull(copy.Clips.Find(c => c.Id == t.To)));
        List<Clip> linked = [.. result.Sequence.AllTracks.SelectMany(t => t.Clips).Where(c => c.LinkId is not null)];
        Assert.Equal(2, linked.Count);
        Assert.Single(linked.Select(c => c.LinkId).Distinct());
        Assert.NotEqual("enlace", linked[0].LinkId);
        Assert.Single(linked.Select(c => c.GroupId).Distinct());
    }

    [Fact]
    public void Media_already_in_the_project_is_reused_through_its_relative_path()
    {
        (Project project, Sequence main) = Draft();
        string folder = Path.Combine(Path.GetTempPath(), "proyecto");
        MediaRef used = project.Media.First(m => main.AllTracks.SelectMany(t => t.Clips).Any(c => c.MediaId == m.Id));
        used.Path = Path.Combine(folder, "material", "toma.mov");
        var target = new Project { Media = [new MediaRef { Id = "ya", Name = "toma", Path = Path.Combine("material", "toma.mov"), Duration = used.Duration, HasVideo = true, HasAudio = true, FrameRate = used.FrameRate, Width = 1920, Height = 1080, SampleRate = 48000, AudioChannels = 2 }] };

        InterchangeResult result = SequenceBuilder.Build(new TimelineDraft(project, main), target, folder, new SequentialIdGenerator(), new InterchangeReport());

        Assert.DoesNotContain(result.Media, m => m.Path == used.Path);
        Assert.Contains(result.Sequence.AllTracks.SelectMany(t => t.Clips), c => c.MediaId == "ya");
    }

    [Fact]
    public void Two_media_of_the_draft_with_the_same_file_become_one()
    {
        (Project project, Sequence main) = Draft();
        MediaRef twin = project.Media[0].Clone();
        twin.Id = "gemelo";
        twin.Path = project.Media[0].Path.ToUpperInvariant();
        project.Media.Add(twin);
        main.VideoTracks[0].Clips.First(c => c.Generator is null && c.SequenceId is null).MediaId = "gemelo";

        InterchangeResult result = SequenceBuilder.Build(new TimelineDraft(project, main), new Project(), null, new SequentialIdGenerator(), new InterchangeReport());

        Assert.Equal(project.Media.Count - 1, result.Media.Count);
    }

    [Fact]
    public void An_overlap_without_transition_moves_the_clip_to_a_new_track_and_is_reported()
    {
        (Project project, Sequence main) = Draft();
        Track track = main.VideoTracks[0];
        int before = main.AllTracks.Sum(t => t.Clips.Count);
        Clip second = track.Clips[1];
        second.Start = track.Clips[0].Start + F(1);
        var report = new InterchangeReport();

        InterchangeResult result = SequenceBuilder.Build(new TimelineDraft(project, main), new Project(), null, new SequentialIdGenerator(), report);

        Assert.Equal(main.VideoTracks.Count + 1, result.Sequence.VideoTracks.Count);
        Assert.Equal(before, result.Sequence.AllTracks.Sum(t => t.Clips.Count));
        Assert.Contains(report.Issues, i => i.Kind == IssueKind.Track && i.Severity == IssueSeverity.Approximate);
    }

    [Fact]
    public void Provisional_media_lasts_what_its_clips_use()
    {
        var media = new MediaRef { Id = "m", Name = "TOMA01", Path = "TOMA01", HasVideo = true };
        var main = new Sequence { Id = "s", Name = "s", FrameRate = new Rational(25, 1) };
        main.VideoTracks.Add(new Track { Id = "v", Kind = TrackKind.Video, Name = "V1", Clips = [new Clip { Id = "c", MediaId = "m", SourceIn = F(90_000), SourceOut = F(90_050) }] });

        InterchangeResult result = SequenceBuilder.Build(new TimelineDraft(new Project { Media = [media], Sequences = [main] }, main), new Project(), null, new SequentialIdGenerator(), new InterchangeReport());

        Assert.Equal(F(90_050), Assert.Single(result.Media).Duration);
    }

    [Fact]
    public void A_compound_cycle_or_an_invalid_draft_throws_a_clear_error()
    {
        (Project project, Sequence main) = Draft();
        Sequence inner = project.Sequences.First(s => s != main);
        inner.VideoTracks[0].Clips[0].MediaId = "";
        inner.VideoTracks[0].Clips[0].SequenceId = inner.Id;
        main.VideoTracks[0].Clips[0].MediaId = "";
        main.VideoTracks[0].Clips[0].SequenceId = inner.Id;

        Assert.Throws<InterchangeException>(() => SequenceBuilder.Build(new TimelineDraft(project, main), new Project(), null, new SequentialIdGenerator(), new InterchangeReport()));

        (Project other, Sequence otherMain) = Draft();
        otherMain.VideoTracks[0].Clips[0].SourceOut = otherMain.VideoTracks[0].Clips[0].SourceIn;
        var error = Assert.Throws<InterchangeException>(() => SequenceBuilder.Build(new TimelineDraft(other, otherMain), new Project(), null, new SequentialIdGenerator(), new InterchangeReport()));
        Assert.StartsWith("El archivo no se puede importar", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Difference_ignores_ids_and_order_but_sees_a_moved_clip()
    {
        TimelineDraft draft = RandomTimelines.Create(11);
        InterchangeResult copy = SequenceBuilder.Build(draft, new Project(), null, new SequentialIdGenerator(), new InterchangeReport());
        var renamed = new TimelineDraft(new Project { Media = [.. copy.Media.AsEnumerable().Reverse()], Sequences = [.. copy.Compounds, copy.Sequence] }, copy.Sequence);
        copy.Sequence.VideoTracks[0].Clips.Reverse();

        Assert.Null(Fidelity.Difference(draft, renamed));

        copy.Sequence.VideoTracks[0].Clips.Sort((a, b) => a.Start.CompareTo(b.Start));
        copy.Sequence.VideoTracks[0].Clips[^1].Start += F(1);
        Assert.Contains("start", Fidelity.Difference(draft, renamed), StringComparison.Ordinal);
    }

    [Fact]
    public void The_walk_fills_gaps_and_flattens_a_compound_with_the_top_track_visible()
    {
        var media = new MediaRef { Id = "m", Name = "m", Path = @"C:\m.mov", Duration = F(1000), HasVideo = true, FrameRate = new Rational(25, 1), Width = 10, Height = 10 };
        var inner = new Sequence { Id = "in", Name = "Dentro" };
        inner.VideoTracks.Add(new Track { Id = "b", Kind = TrackKind.Video, Name = "abajo", Clips = [new Clip { Id = "low", MediaId = "m", Start = F(0), SourceIn = F(100), SourceOut = F(200) }] });
        inner.VideoTracks.Add(new Track { Id = "a", Kind = TrackKind.Video, Name = "arriba", Clips = [new Clip { Id = "high", MediaId = "m", Start = F(40), SourceIn = F(500), SourceOut = F(520) }] });
        var main = new Sequence { Id = "s", Name = "s" };
        main.VideoTracks.Add(new Track { Id = "v", Kind = TrackKind.Video, Name = "V1", Clips = [new Clip { Id = "comp", SequenceId = "in", Start = F(10), SourceIn = F(30), SourceOut = F(90) }] });
        var project = new Project { Media = [media], Sequences = [inner, main] };

        IReadOnlyList<WalkTrack> plain = TimelineWalk.Of(project, main, false, InterchangeLimits.Default, new InterchangeReport());
        IReadOnlyList<WalkTrack> flat = TimelineWalk.Of(project, main, true, InterchangeLimits.Default, new InterchangeReport());

        Assert.Equal([typeof(WalkGap), typeof(WalkClip)], plain[0].Items.Select(i => i.GetType()));
        Assert.Same(inner, ((WalkClip)plain[0].Items[1]).Compound);
        List<WalkClip> pieces = [.. flat[0].Items.OfType<WalkClip>()];
        Assert.Equal(
            [(F(10), F(10), F(130)), (F(20), F(20), F(500)), (F(40), F(30), F(160))],
            pieces.Select(p => (p.Start, p.Duration, p.Clip.SourceIn)));
        Assert.All(pieces, p => Assert.Same(media, p.Media));
    }

    [Fact]
    public void Flattening_stops_at_the_budget_and_at_cycles()
    {
        (Project project, Sequence main) = Draft();
        Assert.Throws<InterchangeException>(() => TimelineWalk.Of(project, main, false, InterchangeLimits.Default with { MaxFlattenedClips = 5 }, new InterchangeReport()));

        Sequence inner = project.Sequences.First(s => s != main);
        Clip loop = inner.VideoTracks[0].Clips[0];
        loop.MediaId = "";
        loop.SequenceId = main.Id;
        main.VideoTracks[0].Clips[0].MediaId = "";
        main.VideoTracks[0].Clips[0].SequenceId = inner.Id;
        Assert.Throws<InterchangeException>(() => TimelineWalk.Of(project, main, true, InterchangeLimits.Default, new InterchangeReport()));
    }

    [Theory]
    [InlineData("file:///C:/Mis%20videos/Toma%C3%B1a.mov", @"C:\Mis videos\Tomaña.mov")]
    [InlineData("file://localhost/C:/a.mov", @"C:\a.mov")]
    [InlineData(@"C:\a\..\b.mov", @"C:\b.mov")]
    [InlineData("material/b.mov", @"C:\base\material\b.mov")]
    [InlineData("/Volumes/Disco/b.mov", "/Volumes/Disco/b.mov")]
    [InlineData(@"\\servidor\share\b.mov", null)]
    [InlineData("file://servidor/share/b.mov", null)]
    [InlineData("https://ejemplo.com/b.mov", null)]
    [InlineData("", null)]
    public void Imported_paths_are_normalized_and_network_ones_are_left_alone(string raw, string? expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(expected, SequenceBuilder.NormalizePath(raw, @"C:\base"));
    }

    /// <summary>En la CI de Linux también: una ruta de Windows sale como file:///C:/… y vuelve igual.</summary>
    [Fact]
    public void A_windows_path_goes_to_a_file_uri_and_back_on_any_system()
    {
        const string path = @"C:\Mis videos\Tomaña #1.mov";

        string uri = SequenceBuilder.ToFileUri(path);

        Assert.Equal("file:///C:/Mis%20videos/Toma%C3%B1a%20%231.mov", uri);
        Assert.Equal(path, SequenceBuilder.NormalizePath(uri, null));
    }

    [Fact]
    public void The_xml_reader_reads_fcpxml_with_its_doctype_entities_and_cdata()
    {
        XmlNode root = XmlLite.Parse(
            "\uFEFF<?xml version=\"1.0\"?>\n<!DOCTYPE fcpxml>\n<!-- hola -->\n<fcpxml version='1.10'><gap name=\"a &amp; b &#241;&#x00F1;\" /><note><![CDATA[<x>]]> y &lt;</note></fcpxml>",
            InterchangeLimits.Default);

        Assert.Equal("fcpxml", root.Name);
        Assert.Equal("1.10", root["version"]);
        Assert.Equal("a & b ññ", root.Element("gap")!["name"]);
        Assert.Equal("<x> y <", root.Element("note")!.Text);
    }

    [Theory]
    [InlineData("<!DOCTYPE a [<!ENTITY lol \"lol\">]><a>&lol;</a>")]
    [InlineData("<!DOCTYPE a SYSTEM \"file:///C:/Windows/win.ini\"><a>&xxe;</a>")]
    [InlineData("<a><b></a></b>")]
    [InlineData("<a x=\"1\" x=\"2\"/>")]
    [InlineData("<a>")]
    [InlineData("<a/><b/>")]
    [InlineData("<a>&#0;</a>")]
    [InlineData("<a>&#xD800;</a>")]
    [InlineData("texto<a/>")]
    public void The_xml_reader_refuses_entities_and_broken_documents(string xml)
    {
        Assert.Throws<InterchangeException>(() => XmlLite.Parse(xml, InterchangeLimits.Default));
    }

    [Fact]
    public void The_xml_reader_stops_at_its_budgets()
    {
        string deep = string.Concat(Enumerable.Repeat("<a>", 10_000)) + string.Concat(Enumerable.Repeat("</a>", 10_000));
        string wide = "<a>" + string.Concat(Enumerable.Repeat("<b/>", 300)) + "</a>";

        Assert.Throws<InterchangeException>(() => XmlLite.Parse(deep, InterchangeLimits.Default));
        Assert.Throws<InterchangeException>(() => XmlLite.Parse(wide, InterchangeLimits.Default with { MaxObjects = 200 }));
        Assert.Throws<InterchangeException>(() => XmlLite.Parse($"<a b=\"{new string('x', 2000)}\"/>", InterchangeLimits.Default with { MaxTextLength = 1000 }));
    }

    [Fact]
    public void The_xml_writer_escapes_and_reads_back()
    {
        string text = new XmlLiteWriter("fcpxml")
            .Start("fcpxml").Attr("version", "1.10")
            .Start("event").Attr("name", "\"Día\" <1> & 2\n").Element("note", "a<b").End()
            .Start("gap").End()
            .End().ToString();

        XmlNode root = XmlLite.Parse(text, InterchangeLimits.Default);

        Assert.Equal("\"Día\" <1> & 2\n", root.Element("event")!["name"]);
        Assert.Equal("a<b", root.Element("event")!.Element("note")!.Text);
        Assert.NotNull(root.Element("gap"));
    }

    /// <summary>Corpus del diseño: 10 000 clips en 8 pistas y 400 medios (semilla 1). Mide contra el reloj: solo en nocturna.</summary>
    [Fact]
    [Trait("Categoria", "Rendimiento")]
    public void Walking_and_importing_ten_thousand_clips_stays_in_budget()
    {
        TimelineDraft draft = RandomTimelines.Create(1, RandomTimelineOptions.Large);
        TimelineDraft warmup = RandomTimelines.Create(2);
        TimelineWalk.Of(warmup.Project, warmup.Sequence, true, InterchangeLimits.Default, new InterchangeReport());
        SequenceBuilder.Build(warmup, new Project(), null, new SequentialIdGenerator(), new InterchangeReport());
        ModelValidator.Validate(warmup.Project);
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        IReadOnlyList<WalkTrack> walk = TimelineWalk.Of(draft.Project, draft.Sequence, true, InterchangeLimits.Default, new InterchangeReport());
        TimeSpan walking = clock.Elapsed;
        clock.Restart();
        SequenceBuilder.Build(draft, new Project(), null, new SequentialIdGenerator(), new InterchangeReport());
        TimeSpan building = clock.Elapsed;
        clock.Restart();
        ModelValidator.Validate(draft.Project);
        TimeSpan validating = clock.Elapsed;
        long megabytes = (GC.GetTotalAllocatedBytes(precise: true) - allocated) >> 20;

        Assert.Equal(10_000, walk.Sum(t => t.Items.OfType<WalkClip>().Count()));
        output.WriteLine($"Recorrer: {walking.TotalMilliseconds:0} ms; importar: {building.TotalMilliseconds:0} ms; validar: {validating.TotalMilliseconds:0} ms; asignado: {megabytes} MB");

        // Objetivos del diseño (exportar < 300 ms, importar < 1 s) con el margen ×3 de las pruebas de rendimiento.
        Assert.True(walking < TimeSpan.FromMilliseconds(900), $"Recorrer: {walking.TotalMilliseconds:0} ms");
        Assert.True(building < TimeSpan.FromSeconds(3), $"Importar: {building.TotalMilliseconds:0} ms (validar el proyecto solo: {validating.TotalMilliseconds:0} ms)");
        Assert.True(megabytes < 300, $"Memoria asignada: {megabytes} MB");
    }
}
