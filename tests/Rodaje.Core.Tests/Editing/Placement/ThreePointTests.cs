using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Editing.Placement;

public class ThreePointTests
{
    private const string Seq = "seq-1";

    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static Time? M(double? seconds) => seconds is { } s ? S(s) : null;

    /// <summary>V1: a [0,10) y b [10,20) con su audio enlazado en A1; V2: t [12,14). El medio dura 60 s.</summary>
    private static Project Fixture()
    {
        static Clip C(string id, double start, double sourceIn, double length, string? link = null) =>
            new() { Id = id, MediaId = "m", Start = S(start), SourceIn = S(sourceIn), SourceOut = S(sourceIn + length), LinkId = link };
        return new Project
        {
            Id = "p",
            Media = [new MediaRef { Id = "m", Path = "m.mp4", Name = "toma", Duration = S(60), HasVideo = true, HasAudio = true, FrameRate = Rational.Fps30, Width = 1920, Height = 1080, SampleRate = 48000, AudioChannels = 2 }],
            Sequences =
            [
                new Sequence
                {
                    Id = Seq,
                    VideoTracks =
                    [
                        new Track { Id = "v1", Kind = TrackKind.Video, Name = "Video 1", Clips = [C("a", 0, 0, 10, "la"), C("b", 10, 20, 10, "lb")] },
                        new Track { Id = "v2", Kind = TrackKind.Video, Name = "Video 2", Clips = [C("t", 12, 40, 2)] },
                    ],
                    AudioTracks = [new Track { Id = "a1", Kind = TrackKind.Audio, Name = "Audio 1", Clips = [C("a2", 0, 0, 10, "la"), C("b2", 10, 20, 10, "lb")] }],
                },
            ],
        };
    }

    private static ThreePointRequest Request(double? sIn, double? sOut, double? qIn, double? qOut, PlacementMode mode = PlacementMode.Overwrite,
        FourPointFit fit = FourPointFit.Ask, double playhead = 1, string? video = "v1", string? audio = "a1", RippleScope scope = RippleScope.Track) =>
        new("m", M(sIn), M(sOut), M(qIn), M(qOut), S(playhead), video, audio, mode, fit, scope);

    /// <summary>Marcas (en segundos, vacío = sin marca) → tramo del origen, inicio y duración en la secuencia.</summary>
    [Theory]
    [InlineData(5.0, 8.0, 4.0, null, 5, 8, 4, 3)] // 3 puntos: entrada y salida del origen, entrada de la secuencia
    [InlineData(5.0, 8.0, null, 10.0, 5, 8, 7, 3)] // la salida de la secuencia: el clip termina ahí
    [InlineData(5.0, null, 2.0, 6.0, 5, 9, 2, 4)] // la salida del origen se calcula
    [InlineData(null, 20.0, 2.0, 6.0, 16, 20, 2, 4)] // la entrada del origen se calcula hacia atrás
    [InlineData(null, null, 3.0, 5.0, 0, 2, 3, 2)] // sin marcas del origen: desde su inicio
    [InlineData(null, null, null, null, 0, 60, 1, 60)] // sin marcas: el medio entero en el cursor
    [InlineData(5.0, null, null, null, 5, 60, 1, 55)] // solo la entrada del origen
    [InlineData(5.0, 9.0, 2.0, 6.0, 5, 9, 2, 4)] // 4 puntos de igual duración
    public void Three_points_compute_the_fourth(double? sIn, double? sOut, double? qIn, double? qOut, double a, double b, double at, double duration)
    {
        ThreePointPlan plan = TimelineEdits.PlanThreePoint(Fixture(), Request(sIn, sOut, qIn, qOut));

        Assert.Equal((S(a), S(b), S(at), S(duration), (Rational?)null, false), (plan.SourceIn, plan.SourceOut, plan.At, plan.Duration, plan.Rate, plan.NeedsDecision));
    }

    [Theory]
    [InlineData(FourPointFit.Ask, 0, 4, true, null)]
    [InlineData(FourPointFit.FitToFill, 0, 4, false, 2)]
    [InlineData(FourPointFit.Trim, 0, 2, false, null)]
    public void Four_points_with_different_durations_ask_fit_or_trim(FourPointFit fit, double a, double b, bool decide, int? rate)
    {
        ThreePointPlan plan = TimelineEdits.PlanThreePoint(Fixture(), Request(0, 4, 2, 4, fit: fit));

        Assert.Equal((S(a), S(b), decide), (plan.SourceIn, plan.SourceOut, plan.NeedsDecision));
        Assert.Equal(rate is { } r ? new Rational(r, 1) : null, plan.Rate);
        Assert.Equal(S(2), plan.At);
    }

    [Theory]
    [InlineData(5.0, 8.0, null, 2.0)] // hacia atrás desde la salida no cabe antes del inicio
    [InlineData(58.0, null, 2.0, 6.0)] // no hay 4 s de material desde 58 s
    [InlineData(8.0, 5.0, 1.0, null)] // salida del origen antes de la entrada
    [InlineData(null, null, 6.0, 2.0)] // salida de la secuencia antes de la entrada
    public void Impossible_marks_are_rejected_with_a_message(double? sIn, double? sOut, double? qIn, double? qOut)
    {
        EditException error = Assert.Throws<EditException>(() => TimelineEdits.PlanThreePoint(Fixture(), Request(sIn, sOut, qIn, qOut)));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void Overwrite_replaces_the_range_on_the_target_tracks_and_links_video_and_audio()
    {
        Project project = Fixture();
        var command = Edits.ThreePointEdit(Seq, Request(30, 33, 4, null), new SequentialIdGenerator());
        new UndoStack(project).Execute(command);

        Sequence s = project.Sequences[0];
        Assert.Equal([(S(0), S(4)), (S(4), S(7)), (S(7), S(10)), (S(10), S(20))], s.VideoTracks[0].Clips.Select(c => (c.Start, c.End)));
        Assert.Equal([(S(0), S(4)), (S(4), S(7)), (S(7), S(10)), (S(10), S(20))], s.AudioTracks[0].Clips.Select(c => (c.Start, c.End)));
        Clip video = s.GetClip(command.CreatedIds[0]).Clip;
        Clip audio = s.GetClip(command.CreatedIds[1]).Clip;
        Assert.Equal((S(30), S(33)), (video.SourceIn, video.SourceOut));
        Assert.NotNull(video.LinkId);
        Assert.Equal(video.LinkId, audio.LinkId);
        Assert.Equal(S(12), s.VideoTracks[1].Clips[0].Start);
    }

    [Fact]
    public void Insert_pushes_the_target_tracks_and_with_all_tracks_every_unlocked_one()
    {
        Project track = Fixture();
        new UndoStack(track).Execute(Edits.ThreePointEdit(Seq, Request(30, 33, 4, null, PlacementMode.Insert)));
        Assert.Equal(S(23), track.Sequences[0].VideoTracks[0].End);
        Assert.Equal(S(23), track.Sequences[0].AudioTracks[0].End);
        Assert.Equal(S(12), track.Sequences[0].VideoTracks[1].Clips[0].Start);

        Project all = Fixture();
        new UndoStack(all).Execute(Edits.ThreePointEdit(Seq, Request(30, 33, 4, null, PlacementMode.Insert, scope: RippleScope.AllTracks)));
        Assert.Equal(S(15), all.Sequences[0].VideoTracks[1].Clips[0].Start);
    }

    [Fact]
    public void Source_track_selectors_place_only_the_enabled_streams()
    {
        Project project = Fixture();
        var command = Edits.ThreePointEdit(Seq, Request(30, 33, 20, null, video: "v2", audio: null));
        new UndoStack(project).Execute(command);

        (Track _, Clip only) = Assert.Single(command.CreatedIds.Select(id => project.Sequences[0].GetClip(id)));
        Assert.Equal("v2", project.Sequences[0].GetClip(command.CreatedIds[0]).Track.Id);
        Assert.Null(only.LinkId);
        Assert.Throws<EditException>(() => new UndoStack(project).Execute(Edits.ThreePointEdit(Seq, Request(30, 33, 20, null, video: null, audio: null))));
    }

    [Fact]
    public void Fit_to_fill_changes_the_speed_and_fills_the_sequence_range_exactly()
    {
        Project project = Fixture();
        var command = Edits.ThreePointEdit(Seq, Request(30, 33, 2, 4, fit: FourPointFit.FitToFill));
        new UndoStack(project).Execute(command);

        Clip clip = project.Sequences[0].GetClip(command.CreatedIds[0]).Clip;
        Assert.Equal((S(2), S(4)), (clip.Start, clip.End));
        Assert.Equal(new Rational(3, 2), clip.Speed!.Rate);
        Assert.Equal((S(30), S(33)), (clip.SourceIn, clip.SourceOut));
        Assert.Empty(ModelValidator.Validate(project));
    }

    [Fact]
    public void Undecided_four_points_and_failures_leave_the_project_untouched()
    {
        Project project = Fixture();
        string before = ProjectSerializer.Serialize(project);
        var stack = new UndoStack(project);

        Assert.Throws<EditException>(() => stack.Execute(Edits.ThreePointEdit(Seq, Request(30, 33, 2, 4))));
        Assert.Throws<EditException>(() => stack.Execute(Edits.ThreePointEdit(Seq, Request(58, null, 2, 6))));

        Assert.Equal(before, ProjectSerializer.Serialize(project));
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void Each_edit_is_one_undo_step()
    {
        Project project = Fixture();
        string before = ProjectSerializer.Serialize(project);
        var stack = new UndoStack(project);
        stack.Execute(Edits.ThreePointEdit(Seq, Request(30, 33, 4, null, PlacementMode.Insert)));

        Assert.Equal("Insertar", stack.UndoDescription);
        stack.Undo();
        Assert.Equal(before, ProjectSerializer.Serialize(project));
    }
}
