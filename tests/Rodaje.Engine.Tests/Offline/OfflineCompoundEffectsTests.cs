using System.Text.RegularExpressions;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Tests.Timeline;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Offline;

/// <summary>
/// Un compuesto con efectos, transformación o transición propios se compone aparte: una capa compuesta con sus capas de
/// dentro, igual en la exportación y en la vista previa; en audio, sus pistas se mezclan y después suenan con su volumen.
/// </summary>
public sealed class OfflineCompoundEffectsTests(ITestOutputHelper output)
{
    private static readonly int ColorCode = EffectRegistry.Find(BuiltInEffects.Color)!.Code;

    [Fact]
    public void A_compound_with_color_and_half_opacity_is_one_layer_holding_the_loose_layers()
    {
        (TimelineBuilder loose, FakeTimelineBackend looseBackend, FakeEffectContent looseContent) = Full();
        (TimelineBuilder nested, FakeTimelineBackend nestedBackend, FakeEffectContent nestedContent) = Full();
        Clip compound = WithLook(Compound(nested, Every(nested.Sequence)));

        using OfflineTimeline expected = OfflineProjects.Open(loose, looseBackend, new OfflineTimelineOptions { Content = looseContent });
        using OfflineTimeline actual = OfflineProjects.Open(nested, nestedBackend, new OfflineTimelineOptions { Content = nestedContent });
        Assert.Equal(expected.FrameCount, actual.FrameCount);
        int compared = 0;
        for (long f = 0; f < expected.FrameCount; f += 3)
        {
            using CompositionFrame a = expected.Render(f, CancellationToken.None);
            using CompositionFrame b = actual.Render(f, CancellationToken.None);
            if (a.Layers.Count == 0)
            {
                Assert.Empty(b.Layers);
                continue;
            }

            LayerFrame layer = Assert.Single(b.Layers);
            Assert.Equal(compound.Id, layer.ClipId);
            Assert.Equal(0.5, layer.Transform.Opacity);
            Assert.Contains(layer.Effects, e => e.Code == ColorCode);
            var inside = new CompositionFrame(b.TimelineTime, b.Width, b.Height, layer.Nested!);
            Assert.Equal(WithoutTracks(OfflineProjects.Describe(a)),
                WithoutTracks(OfflineProjects.Describe(inside)).Replace(compound.Id + CompoundOps.IdSeparator, "", StringComparison.Ordinal));
            compared++;
        }

        Assert.True(compared > 30);
    }

    [Fact]
    public void The_preview_shows_the_same_compound_layers_as_the_export()
    {
        (TimelineBuilder b, FakeTimelineBackend backend, FakeEffectContent content) = Full();
        WithLook(Compound(b, Every(b.Sequence)));
        using OfflineTimeline export = OfflineProjects.Open(b, backend, new OfflineTimelineOptions { Content = content });
        using TimelineSession preview = TimelineSessionTests.Open(b, backend,
            new TimelineSessionOptions { Content = content, TimeProvider = new ManualTimeProvider() });
        int nestedFrames = 0;
        for (long f = 0; f < preview.FrameCount; f += 7)
        {
            preview.SeekToFrame(f);
            using CompositionFrame shown = Settle(preview);
            using CompositionFrame expected = export.Render(f, CancellationToken.None);
            Assert.Equal(OfflineProjects.Describe(expected), OfflineProjects.Describe(shown));
            nestedFrames += shown.Layers.Count(l => l.Nested is { Count: > 1 });
        }

        Assert.True(nestedFrames > 5);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_transition_on_the_compound_mixes_the_compound_layer_like_the_loose_clip(bool compoundFirst)
    {
        TimelineBuilder loose = TwoClips(out FakeTimelineBackend looseBackend);
        WithLook(loose.Sequence.VideoTracks[0].Clips.Single(c => c.Id == "A"));
        AddCrossfade(loose, compoundFirst ? "A" : "B", compoundFirst ? "B" : "A");

        TimelineBuilder nested = TwoClips(out FakeTimelineBackend nestedBackend);
        Clip compound = WithLook(Compound(nested, ["A"]));
        AddCrossfade(nested, compoundFirst ? compound.Id : "B", compoundFirst ? "B" : compound.Id);
        Assert.Empty(ModelValidator.Validate(nested.Project));

        using OfflineTimeline expected = OfflineProjects.Open(loose, looseBackend);
        using OfflineTimeline actual = OfflineProjects.Open(nested, nestedBackend);
        Rational rate = loose.Sequence.FrameRate;
        int mixed = 0;
        for (long f = S(2.5).ToFrames(rate); f < S(3.5).ToFrames(rate); f++)
        {
            using CompositionFrame a = expected.Render(f, CancellationToken.None);
            using CompositionFrame b = actual.Render(f, CancellationToken.None);
            LayerFrame reference = Assert.Single(a.Layers);
            LayerFrame layer = Assert.Single(b.Layers);
            LayerFrame clipA = compoundFirst ? reference : reference.Transition?.Incoming ?? reference;
            LayerFrame group = compoundFirst ? layer : layer.Transition?.Incoming ?? layer;
            Assert.Equal(clipA.Transform, group.Transform);
            Assert.Equal(clipA.Effects.Select(e => e.Code), group.Effects.Select(e => e.Code));
            LayerFrame inner = Assert.Single(group.Nested!);
            Assert.Equal(clipA.Frame.Index, inner.Frame.Index);
            Assert.Equal(compound.Id + CompoundOps.IdSeparator + "A", inner.ClipId.Replace(VideoLayerPlan.CompanionSuffix, "", StringComparison.Ordinal));
            Assert.Equal(reference.Transition is null, layer.Transition is null);
            if (reference.Transition is { } t && layer.Transition is { } u)
            {
                Assert.Equal(t.Code, u.Code);
                Assert.Equal(t.Progress, u.Progress);
                LayerFrame other = compoundFirst ? u.Incoming : layer;
                LayerFrame otherReference = compoundFirst ? t.Incoming : reference;
                Assert.Equal(otherReference.Frame.Index, other.Frame.Index);
                Assert.Equal(otherReference.Transform, other.Transform);
                mixed++;
            }
        }

        Assert.Equal(S(1).ToFrames(rate), mixed);
    }

    [Fact]
    public void A_compound_volume_sounds_like_the_same_volume_on_each_loose_clip()
    {
        var backend = new FakeTimelineBackend();
        TimelineBuilder loose = Sound(backend);
        foreach (Clip clip in loose.Sequence.AudioTracks.SelectMany(t => t.Clips))
        {
            clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Audio, (BuiltInEffects.AudioParameters.GainDb, -6)));
        }

        TimelineBuilder nested = Sound(backend);
        Compound(nested, Every(nested.Sequence));
        Clip compound = nested.Sequence.AudioTracks.SelectMany(t => t.Clips).Single(c => c.IsCompound);
        compound.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Audio, (BuiltInEffects.AudioParameters.GainDb, -6)));

        AssertSoundsAlike(Mix(loose, backend), Mix(nested, backend));
    }

    [Fact]
    public void The_track_volume_inside_a_compound_is_applied()
    {
        var backend = new FakeTimelineBackend();
        TimelineBuilder loose = Sound(backend);
        foreach (Clip clip in loose.Sequence.AudioTracks[0].Clips)
        {
            clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Audio, (BuiltInEffects.AudioParameters.GainDb, -12)));
        }

        TimelineBuilder nested = Sound(backend);
        Compound(nested, Every(nested.Sequence));
        Clip compound = nested.Sequence.AudioTracks.SelectMany(t => t.Clips).Single(c => c.IsCompound);
        Sequence inner = nested.Project.FindSequence(compound.SequenceId!)!;
        inner.AudioTracks[0].Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Audio, (BuiltInEffects.AudioParameters.GainDb, -12)));

        float[] expected = Mix(loose, backend);
        float[] actual = Mix(nested, backend);
        AssertSoundsAlike(expected, actual);
        Assert.NotEqual(Mix(Sound(backend), backend), actual);
    }

    [Fact]
    public void A_compound_without_its_own_effects_is_still_unfolded()
    {
        (TimelineBuilder b, _, _) = Full();
        Compound(b, Every(b.Sequence));
        TimelinePlan plan = b.Plan();
        Assert.DoesNotContain(plan.Layers, l => l.IsNested || l.Groups.Count > 0);
        Assert.DoesNotContain(plan.Audio.Tracks.SelectMany(t => t.Clips), c => c.Bus is not null);
    }

    [Fact]
    public void A_compound_layer_adds_little_to_each_preview_tick()
    {
        double plain = TickBytes(withCompound: false);
        double compound = TickBytes(withCompound: true);
        output.WriteLine($"Tick: {plain:0} B por refresco con dos capas sueltas; {compound:0} B con ellas en un compuesto con color y opacidad.");
        Assert.True(compound - plain <= 1024, $"{compound - plain:0} B más por refresco.");
    }

    private static double TickBytes(bool withCompound)
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("a", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("b", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(60), "A");
        b.AddVideo(1, "b", Time.Zero, S(1), S(60), "B");
        if (withCompound)
        {
            WithLook(Compound(b, ["A", "B"]));
        }

        using TimelineSession preview = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { TimeProvider = new ManualTimeProvider() });
        preview.SeekToFrame(30);
        Settle(preview).Dispose();
        for (int i = 0; i < 100; i++)
        {
            preview.Tick().Dispose();
        }

        const int Ticks = 1000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Ticks; i++)
        {
            preview.Tick().Dispose();
        }

        return (double)(GC.GetAllocatedBytesForCurrentThread() - before) / Ticks;
    }

    private static (TimelineBuilder, FakeTimelineBackend, FakeEffectContent) Full()
    {
        var backend = new FakeTimelineBackend();
        var content = new FakeEffectContent();
        return (OfflineProjects.Full(Rational.Fps30, backend, content), backend, content);
    }

    private static TimelineBuilder TwoClips(out FakeTimelineBackend backend)
    {
        backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("a", Rational.Fps30, 900, backend: backend);
        b.AddMedia("c", Rational.Fps30, 900, backend: backend);
        b.AddVideo(0, "a", Time.Zero, S(1), S(3), "A");
        b.AddVideo(0, "c", S(3), S(10), S(3), "B");
        return b;
    }

    private static void AddCrossfade(TimelineBuilder b, string from, string to)
    {
        Track track = b.Sequence.VideoTracks[0];
        if (track.Clips.Single(c => c.Id == from).Start > track.Clips.Single(c => c.Id == to).Start)
        {
            // B va primero: se cambian de lugar para que el compuesto (o A) entre.
            Clip first = track.Clips.Single(c => c.Id == to);
            Clip second = track.Clips.Single(c => c.Id == from);
            (first.Start, second.Start) = (S(3), Time.Zero);
        }

        TimelineEffectsTests.AddTransition(b, BuiltInTransitions.Crossfade, from, to, S(1));
        track.SortClips();
    }

    private static TimelineBuilder Sound(FakeTimelineBackend backend)
    {
        var b = new TimelineBuilder(1, 2);
        b.AddMedia("a", Rational.Fps30, 900, backend: backend);
        b.AddMedia("b", Rational.Fps30, 900, video: false, backend: backend);
        b.AddVideo(0, "a", S(0.5), S(1), S(6));
        b.AddAudio(0, "a", S(0.5), S(1), S(6));
        b.AddAudio(1, "b", S(2), S(3), S(5));
        return b;
    }

    private static float[] Mix(TimelineBuilder b, FakeTimelineBackend backend)
    {
        using OfflineAudio audio = new(b.Project, b.Sequence.Id, @"C:\proyecto", backend);
        var mix = new float[48_000 * 2 * 8];
        audio.Seek(S(0));
        audio.Read(mix);
        return mix;
    }

    private static void AssertSoundsAlike(float[] expected, float[] actual)
    {
        Assert.Contains(expected, s => s != 0);
        double worst = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(expected[i] - actual[i]));
        }

        Assert.True(worst < 1e-5, $"Diferencia máxima {worst}.");
    }

    /// <summary>Color y opacidad 0,5: lo que el compuesto lleva como propio.</summary>
    private static Clip WithLook(Clip clip)
    {
        clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Color, (BuiltInEffects.ColorParameters.Exposure, 0.5)));
        clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.Opacity, 0.5)));
        return clip;
    }

    private static List<string> Every(Sequence sequence) => [.. sequence.AllTracks.SelectMany(t => t.Clips).Select(c => c.Id)];

    /// <summary>El compuesto de video que queda.</summary>
    private static Clip Compound(TimelineBuilder builder, List<string> clips)
    {
        SequenceEditCommand create = Edits.CreateCompound(builder.Sequence.Id, clips, "Compuesto");
        new UndoStack(builder.Project).Execute(create);
        Assert.Empty(ModelValidator.Validate(builder.Project));
        return builder.Sequence.VideoTracks.SelectMany(t => t.Clips).Single(c => c.Id == create.CreatedIds[0]);
    }

    /// <summary>Sin los índices de pista: dentro del compuesto las capas cuelgan de su pista.</summary>
    private static string WithoutTracks(string description) => Regex.Replace(description, @"(\| |<- )\d+:", "$1");

    private static CompositionFrame Settle(TimelineSession session)
    {
        for (int i = 0; ; i++)
        {
            CompositionFrame frame = session.Tick();
            if (!session.IsSeekPending)
            {
                return frame;
            }

            frame.Dispose();
            Assert.True(i < 20, "La vista previa no llegó a la composición completa.");
        }
    }
}
