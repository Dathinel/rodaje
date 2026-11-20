using Rodaje.App.Services;
using Rodaje.App.Services.Interchange;
using Rodaje.App.Services.Relink;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Interchange;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;

namespace Rodaje.App.Tests.Interchange;

/// <summary>El servicio de intercambio sin WinUI (D-062): el paso del historial, los medios que faltan y el sondeo.</summary>
public sealed class InterchangeServiceTests
{
    private static readonly Rational Rate = new(25, 1);

    private static Time F(long frames) => Time.FromFrames(frames, Rate);

    private static EditorSession NewSession() => new(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());

    /// <summary>Lo que daría leer una EDL de un clip de video y otro de audio del carrete TOMA01, sin ver su archivo.</summary>
    private static TimelineDraft EdlLike(string path)
    {
        var media = new MediaRef { Id = "m", Name = "TOMA01", Path = path, Reel = "TOMA01", HasVideo = true, HasAudio = true, FrameRate = Rate };
        var sequence = new Sequence { Id = "s", Name = "Montaje", FrameRate = Rate };
        sequence.VideoTracks.Add(new Track { Id = "v", Kind = TrackKind.Video, Name = "V1", Clips = [new Clip { Id = "c1", MediaId = "m", Start = F(0), SourceIn = F(90_250), SourceOut = F(90_300) }] });
        sequence.AudioTracks.Add(new Track { Id = "a", Kind = TrackKind.Audio, Name = "A1", Clips = [new Clip { Id = "c2", MediaId = "m", Start = F(0), SourceIn = F(90_250), SourceOut = F(90_300) }] });
        return new TimelineDraft(new Project { Media = [media], Sequences = [sequence] }, sequence);
    }

    private static InterchangeResult Result(EditorSession session, string path = @"C:\no-existe\TOMA01.mov") =>
        SequenceBuilder.Build(EdlLike(path), session.Project, null, GuidIdGenerator.Instance, new InterchangeReport());

    [Fact]
    public void Importing_is_one_history_step_and_undo_removes_everything()
    {
        EditorSession session = NewSession();
        int media = session.Project.Media.Count;
        int sequences = session.Project.Sequences.Count;
        int videoTracks = session.Sequence.VideoTracks.Count;
        var service = new InterchangeService(new FakeProber());

        var notes = new List<EditorNotification>();
        session.Notified += (_, n) => notes.Add(n);
        Assert.True(session.Execute(service.ImportCommand(Result(session), session.SequenceId, F(10))), string.Join(" | ", notes.Select(n => n.Message)));

        Assert.Equal(1, session.History.Count);
        Assert.Equal(media + 1, session.Project.Media.Count);
        Assert.Equal(sequences + 1, session.Project.Sequences.Count);
        Assert.Equal(videoTracks + 1, session.Sequence.VideoTracks.Count);
        Clip compound = session.Sequence.VideoTracks[^1].Clips.Single();
        Assert.Equal((F(10), F(50)), (compound.Start, compound.Duration));
        Assert.NotNull(compound.LinkId);
        Assert.Equal(compound.LinkId, session.Sequence.AudioTracks[^1].Clips.Single().LinkId);
        Assert.Empty(ModelValidator.Validate(session.Project));

        Assert.True(session.Undo());

        Assert.Equal((media, sequences, videoTracks), (session.Project.Media.Count, session.Project.Sequences.Count, session.Sequence.VideoTracks.Count));
    }

    [Fact]
    public void Importing_the_same_file_twice_does_not_collide_and_reuses_the_media()
    {
        EditorSession session = NewSession();
        var service = new InterchangeService(new FakeProber());

        Assert.True(session.Execute(service.ImportCommand(Result(session), session.SequenceId, F(0))));
        InterchangeResult second = Result(session);
        Assert.True(session.Execute(service.ImportCommand(second, session.SequenceId, F(100))));

        Assert.Empty(second.Media);
        Assert.Single(session.Project.Media);
        Assert.Empty(ModelValidator.Validate(session.Project));
    }

    /// <summary>
    /// R8: el aviso «Faltan medios» de la importación se quedaba puesto al deshacerla. Ahora caduca cuando esos medios
    /// ya no están en el proyecto (deshacer o abrir otro) y se repone al rehacer.
    /// </summary>
    [Fact]
    public void The_missing_media_notice_is_retracted_on_undo_shown_again_on_redo_and_retracted_on_open() => UiThread.Run(async () =>
    {
        EditorSession session = new(new FakePlayback(), new FakeProber(), new FakeDialogs { Choice = UnsavedChangesChoice.Discard }, new SequentialIdGenerator());
        var shown = new List<EditorNotification>();
        var retracted = new List<EditorNotification>();
        session.Notified += (_, n) => shown.Add(n);
        session.NotificationRetracted += (_, n) => retracted.Add(n);

        InterchangeResult result = Result(session);
        IReadOnlyList<MediaRef> missing = InterchangeService.Missing(result);
        Assert.Single(missing);
        Assert.True(session.Execute(new InterchangeService(new FakeProber()).ImportCommand(result, session.SequenceId, F(0))));

        MediaRelinkService.NotifyMissing(session, missing, new NotificationAction("Volver a enlazar", () => { }));
        EditorNotification notice = Assert.Single(shown);
        Assert.Equal(NotificationSeverity.Warning, notice.Severity);
        Assert.Empty(retracted);

        Assert.True(session.Undo());
        Assert.Same(notice, Assert.Single(retracted));

        Assert.True(session.Redo());
        Assert.Equal(2, shown.Count);
        Assert.Same(notice, shown[1]);
        Assert.Single(retracted);

        Assert.True(await session.NewProjectAsync());
        Assert.Equal(2, retracted.Count);
        Assert.Same(notice, retracted[1]);
    });

    [Fact]
    public void Media_that_is_not_on_disk_goes_to_relink()
    {
        EditorSession session = NewSession();

        Assert.Single(InterchangeService.Missing(Result(session)));
        Assert.Single(InterchangeService.Missing(Result(session), _ => true)); // está, pero sigue provisional: se reenlaza
        Assert.Single(InterchangeService.Missing(Result(session, "TOMA01"), _ => true)); // solo el carrete: no es una ruta
        Assert.Single(InterchangeService.Missing(Result(session, "//servidor/share/TOMA01.mov"), _ => true)); // de red: nunca se toca sola
    }

    [Fact]
    public async Task A_file_that_is_too_short_is_reported_and_left_for_relink()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rodaje-intercambio-{Guid.NewGuid():N}.mov");
        await File.WriteAllBytesAsync(path, [0]);
        try
        {
            EditorSession session = NewSession();
            MediaInfo info = MediaSamples.Video(path, 5, 1920, 1080, Rate, true) with { Tags = new MediaTags(null, null) { Timecode = "01:00:00:00" } };
            InterchangeResult result = Result(session, path);

            await new InterchangeService(new FixedProber(info)).ProbeAsync(result);

            Assert.Equal(0, result.Media.Single().Width);
            Assert.Contains(result.Report.Issues, i => i.Kind == IssueKind.Media);
            Assert.Single(InterchangeService.Missing(result, _ => true));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Probing_an_existing_file_gives_it_its_data_and_moves_the_clips_to_its_timecode()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rodaje-intercambio-{Guid.NewGuid():N}.mov");
        await File.WriteAllBytesAsync(path, [0]);
        try
        {
            EditorSession session = NewSession();
            MediaInfo info = MediaSamples.Video(path, 60, 1920, 1080, Rate, true) with { Tags = new MediaTags(null, null) { Timecode = "01:00:00:00" } };
            var service = new InterchangeService(new FixedProber(info));
            InterchangeResult result = Result(session, path);

            await service.ProbeAsync(result);

            MediaRef media = Assert.Single(result.Media);
            Assert.Equal(("01:00:00:00", 1920, Time.FromSeconds(60)), (media.Timecode, media.Width, media.Duration));
            Assert.All(result.Sequence.AllTracks.SelectMany(t => t.Clips), c => Assert.Equal((F(250), F(300)), (c.SourceIn, c.SourceOut)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_report_text_uses_the_sequence_timecode()
    {
        var report = new InterchangeReport();
        report.Add(IssueSeverity.Lost, IssueKind.Color, "Se pierde el color.", "toma", "c", F(25));
        var sequence = new Sequence { FrameRate = Rate, StartTimecode = "10:00:00:00" };

        string text = InterchangeService.ReportText(report, sequence, new InterchangeOptions());

        Assert.Equal("10:00:01:00  Perdido: «toma» Se pierde el color.", text.TrimEnd());
    }

    private sealed class FixedProber(MediaInfo info) : IMediaProber
    {
        public MediaProbeResult Probe(string path) => new(path, info with { Path = path }, null);
    }
}
