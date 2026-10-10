using Rodaje.App.Services;
using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.TimelineLogic;

public class PlanningTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Pista principal: a [0,10) y b [10,20) con audio enlazado; en la biblioteca, «m» (solo audio) y «muda» (sin audio).</summary>
    private static async Task<EditorSession> TwoClips()
    {
        var prober = new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10).Audio("m.mp3", 30).Video("muda.mp4", 4, audio: false).Video("n.mp4", 6);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")]);
        await session.ImportMediaAsync([Path.GetFullPath("m.mp3"), Path.GetFullPath("muda.mp4"), Path.GetFullPath("n.mp4")]);
        return session;
    }

    private static MediaRef Media(EditorSession session, string name) => session.Project.Media.First(m => m.Name == name);

    private static string Layout(EditorSession session, Track track) =>
        string.Join(" ", track.Clips.Select(c => FormattableString.Invariant($"{session.Project.GetMedia(c.MediaId).Name}@{c.Start.ToSeconds():0.##}-{c.End.ToSeconds():0.##}")));

    private static DragOptions Options(EditorSession session, bool snapping = false) => new()
    {
        Snapping = snapping,
        MagneticTrackId = session.MainTrack!.Id,
        RippleScope = RippleScope.Track,
        PixelsPerSecond = 100,
    };

    private static TrackRow Row(EditorSession session, Track track) => TrackLayout.Build(session.Sequence, session.MainTrack).Find(track.Id)!;

    private static DropPlan Drop(EditorSession session, string media, Track track, double at, bool insert = false, bool snapping = false) =>
        TimelinePlanning.PlanDrop(session.Project, session.Sequence, Media(session, media), Row(session, track), S(at), insert, Options(session, snapping), session.Position)!;

    [Fact]
    public void SoltarEnLaPrincipal_InsertaEnElCorteYMantieneElAudioEnSincronia() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        Track main = session.MainTrack!;
        Track a1 = session.Sequence.AudioTracks[0];

        DropPlan plan = Drop(session, "n", main, 8);
        Assert.Equal((S(10), PlacementMode.Insert, a1.Id), (plan.At, plan.Mode, plan.LinkedTrackId));
        Assert.True(session.Execute(plan.Build()));
        Assert.Equal("a@0-10 n@10-16 b@16-26", Layout(session, main));
        Assert.Equal("a@0-10 n@10-16 b@16-26", Layout(session, a1));

        // Un video sin sonido: el audio de lo que sigue se corre igual.
        plan = Drop(session, "muda", main, 1);
        Assert.Null(plan.LinkedTrackId);
        Assert.True(session.Execute(plan.Build()));
        Assert.Equal("muda@0-4 a@4-14 n@14-20 b@20-30", Layout(session, main));
        Assert.Equal("a@4-14 n@14-20 b@20-30", Layout(session, a1));
    });

    [Fact]
    public void SoltarEnPistaLibre_TapaOCorreYBuscaUnaPistaDeAudioLibre() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        Track v2 = session.Sequence.VideoTracks[1];
        Track a2 = session.Sequence.AudioTracks[1];

        DropPlan plan = Drop(session, "n", v2, 3);
        Assert.Equal((S(3), PlacementMode.Overwrite, a2.Id), (plan.At, plan.Mode, plan.LinkedTrackId));
        Assert.True(session.Execute(plan.Build()));
        Assert.Equal("n@3-9", Layout(session, v2));
        Assert.Equal("n@3-9", Layout(session, a2));

        plan = Drop(session, "n", v2, 5, insert: true);
        Assert.Equal(PlacementMode.Insert, plan.Mode);
        Assert.Null(plan.LinkedTrackId);
    });

    [Fact]
    public void SoltarConAjuste_SePegaAlBordeCercano() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        DropPlan plan = Drop(session, "muda", session.Sequence.VideoTracks[1], 10.05, snapping: true);
        Assert.Equal(S(10), plan.At);
    });

    [Fact]
    public void SoltarDondeNoCabe() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        Track a1 = session.Sequence.AudioTracks[0];

        // Un medio sin imagen sobre video va a la última pista de audio.
        DropPlan music = Drop(session, "m", session.Sequence.VideoTracks[1], 2);
        Assert.Equal(session.Sequence.AudioTracks[^1].Id, music.TrackId);

        // Un video sin sonido no va en una pista de audio.
        Assert.Null(TimelinePlanning.PlanDrop(session.Project, session.Sequence, Media(session, "muda"), Row(session, a1), S(1), false, Options(session), null));
        Assert.Null(TimelinePlanning.PlanDrop(session.Project, session.Sequence, Media(session, "muda"), null, S(1), false, Options(session), null));

        session.Execute(Edits.SetTrackState(session.SequenceId, session.Sequence.VideoTracks[1].Id, locked: true));
        Assert.Null(TimelinePlanning.PlanDrop(session.Project, session.Sequence, Media(session, "muda"), Row(session, session.Sequence.VideoTracks[1]), S(1), false, Options(session), null));
    });

    [Fact]
    public void ElTramoDeUnaRegionNueva() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        Sequence sequence = session.Sequence;
        Track main = session.MainTrack!;

        Assert.Equal((S(1), S(2)), TimelinePlanning.RegionSpan(sequence, [main.Clips[1].Id], (S(1), S(2)), main, S(3)));
        Assert.Equal((S(10), S(20)), TimelinePlanning.RegionSpan(sequence, [main.Clips[1].Id], (S(2), S(2)), main, S(3)));
        Assert.Equal((S(0), S(20)), TimelinePlanning.RegionSpan(sequence, [main.Clips[1].Id, main.Clips[0].Id, "nada"], null, main, S(3)));
        Assert.Equal((S(0), S(10)), TimelinePlanning.RegionSpan(sequence, [], null, main, S(3)));
        Assert.Equal((S(40), S(45)), TimelinePlanning.RegionSpan(sequence, [], null, main, S(40)));
    });

    [Fact]
    public void NombresYPruebaEnSeco() => UiThread.Run(async () =>
    {
        Assert.Equal("Marcador 1", TimelinePlanning.NextName("Marcador", []));
        Assert.Equal("Marcador 3", TimelinePlanning.NextName("Marcador", ["Marcador 1", "Marcador 2", "Otro"]));
        Assert.Equal("Región 2", TimelinePlanning.NextName("Región", ["Región 1", "Región 3"]));

        EditorSession session = await TwoClips();
        string before = Layout(session, session.MainTrack!);
        Assert.False(TimelinePlanning.Succeeds(session.Project, Edits.Move(session.SequenceId, session.MainTrack!.Clips[0].Id, S(5))));
        Assert.True(TimelinePlanning.Succeeds(session.Project, Edits.Move(session.SequenceId, session.MainTrack!.Clips[1].Id, S(25))));
        Assert.False(TimelinePlanning.Succeeds(session.Project, Edits.Move(session.SequenceId, "nada", S(25))));
        Assert.Equal(before, Layout(session, session.MainTrack!));
    });

    [Fact]
    public void Decoraciones()
    {
        var clip = new Clip
        {
            Id = "c",
            MediaId = "m",
            SourceIn = S(2),
            SourceOut = S(6),
            Start = S(10),
            Effects =
            [
                new EffectInstance
                {
                    Id = "fx",
                    Type = BuiltInEffects.Transform,
                    Parameters = new()
                    {
                        ["opacity"] = new Parameter { Keyframes = [new Keyframe(S(1), 0), new Keyframe(S(3), 1), new Keyframe(S(5), 1)] },
                        ["x"] = new Parameter { Keyframes = [new Keyframe(S(3), 0), new Keyframe(S(9), 1)] },
                    },
                },
                new EffectInstance
                {
                    Id = "vol",
                    Type = ClipDecorations.VolumeEffect,
                    Parameters = new() { [ClipDecorations.GainParameter] = new Parameter { Keyframes = [new Keyframe(S(2), 0), new Keyframe(S(6), 2)] } },
                },
            ],
        };

        Assert.Equal([S(10), S(11), S(13), S(14)], ClipDecorations.KeyframeTimes(clip));
        Assert.Equal(0, ClipDecorations.GainAt(clip, S(10)), 6);
        Assert.Equal(1, ClipDecorations.GainAt(clip, S(12)), 6);
        Assert.True(ClipDecorations.HasAnimatedGain(clip));
        Assert.Equal(1, ClipDecorations.GainAt(new Clip { SourceOut = S(1) }, S(0)));

        var track = new Track
        {
            Clips =
            [
                new Clip { Id = "1", SourceOut = S(2), Start = S(0) },
                new Clip { Id = "2", SourceOut = S(2), Start = S(2) },
                new Clip { Id = "3", SourceOut = S(2), Start = S(5) },
                new Clip { Id = "4", SourceOut = S(2), Start = S(7) },
            ],
        };
        Assert.Equal([S(2), S(7)], ClipDecorations.AdjacentCuts(track));

        float peak = ClipDecorations.PlaceholderPeak("media-1", S(3));
        Assert.Equal(peak, ClipDecorations.PlaceholderPeak("media-1", S(3) + Time.FromTicks(1)));
        Assert.InRange(peak, 0.05f, 1f);
        Assert.NotEqual(
            Enumerable.Range(0, 20).Select(i => ClipDecorations.PlaceholderPeak("media-1", S(i * 0.04))),
            Enumerable.Range(0, 20).Select(i => ClipDecorations.PlaceholderPeak("media-2", S(i * 0.04))));
    }

    [Fact]
    public void LasPreferenciasDeLaLineaDeTiempoSeGuardanYSeCorrigen()
    {
        var state = new TimelineState
        {
            MagneticMainTrack = false,
            Snapping = false,
            AutoRipple = true,
            RippleMode = RippleMode.AllTracksAndMarkers,
            AutoCrossfade = true,
            LockEnvelopes = true,
            IgnoreGroups = true,
            PixelsPerSecond = 300,
        };
        var prefs = new TimelinePreferences();
        prefs.CopyFrom(state);
        var restored = new TimelineState();
        prefs.ApplyTo(restored);

        Assert.Equal(
            (false, false, true, RippleMode.AllTracksAndMarkers, true, true, true, 300.0),
            (restored.MagneticMainTrack, restored.Snapping, restored.AutoRipple, restored.RippleMode, restored.AutoCrossfade, restored.LockEnvelopes, restored.IgnoreGroups, restored.PixelsPerSecond));
        Assert.Equal(RippleScope.AllTracksAndMarkers, restored.RippleScope);

        var settings = new AppSettings { Timeline = new TimelinePreferences { RippleMode = (RippleMode)42, PixelsPerSecond = double.NaN } };
        settings.Normalize();
        Assert.Equal(RippleMode.AffectedTracks, settings.Timeline.RippleMode);
        Assert.Equal(TimelineState.DefaultPixelsPerSecond, settings.Timeline.PixelsPerSecond);
        settings.Timeline = null!;
        settings.Normalize();
        Assert.NotNull(settings.Timeline);
    }
}
