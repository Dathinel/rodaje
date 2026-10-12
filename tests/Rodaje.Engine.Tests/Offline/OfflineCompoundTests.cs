using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Tests.Timeline;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Offline;

/// <summary>Un clip compuesto se ve y suena igual que los clips sueltos que contiene, en la exportación y en la vista previa.</summary>
public sealed class OfflineCompoundTests
{
    [Fact]
    public void A_compound_of_every_track_renders_exactly_like_the_loose_clips()
    {
        (TimelineBuilder loose, FakeTimelineBackend looseBackend, FakeEffectContent looseContent) = Full();
        (TimelineBuilder nested, FakeTimelineBackend nestedBackend, FakeEffectContent nestedContent) = Full();
        string compound = Compound(nested, Every(nested.Sequence));

        using OfflineTimeline expected = OfflineProjects.Open(loose, looseBackend, new OfflineTimelineOptions { Content = looseContent });
        using OfflineTimeline actual = OfflineProjects.Open(nested, nestedBackend, new OfflineTimelineOptions { Content = nestedContent });

        Assert.Equal(expected.FrameCount, actual.FrameCount);
        for (long f = 0; f < expected.FrameCount; f++)
        {
            using CompositionFrame a = expected.Render(f, CancellationToken.None);
            using CompositionFrame b = actual.Render(f, CancellationToken.None);
            Assert.Equal(OfflineProjects.Describe(a), OfflineProjects.Describe(b).Replace(compound + CompoundOps.IdSeparator, "", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_trimmed_and_moved_compound_in_the_preview_shows_its_window_of_the_inner_sequence()
    {
        (TimelineBuilder loose, FakeTimelineBackend looseBackend, FakeEffectContent looseContent) = Full();
        (TimelineBuilder nested, FakeTimelineBackend nestedBackend, FakeEffectContent nestedContent) = Full();
        string compound = Compound(nested, Every(nested.Sequence));
        var stack = new UndoStack(nested.Project);
        stack.Execute(Edits.Trim(nested.Sequence.Id, compound, TrimEdge.In, S(1)));
        stack.Execute(Edits.Trim(nested.Sequence.Id, compound, TrimEdge.Out, S(-1)));
        stack.Execute(Edits.Move(nested.Sequence.Id, compound, S(3), null, PlacementMode.Strict));

        using OfflineTimeline reference = OfflineProjects.Open(loose, looseBackend, new OfflineTimelineOptions { Content = looseContent });
        using TimelineSession preview = TimelineSessionTests.Open(nested, nestedBackend,
            new TimelineSessionOptions { Content = nestedContent, TimeProvider = new ManualTimeProvider() });
        Rational rate = nested.Sequence.FrameRate;
        Time shift = S(2);
        int compared = 0;
        for (long f = Time.FromSeconds(3).ToFrames(rate); f < preview.FrameCount; f += 7)
        {
            // Lo que se ve en t es lo que la secuencia anidada muestra en t − 2 s (entra 1 s recortado, empieza en 3 s).
            long inner = (Time.FromFrames(f, rate) - shift).ToFrames(rate);
            preview.SeekToFrame(f);
            using CompositionFrame shown = Settle(preview);
            using CompositionFrame expected = reference.Render(inner, CancellationToken.None);
            Assert.Equal(Layers(expected), Layers(shown).Replace(compound + CompoundOps.IdSeparator, "", StringComparison.Ordinal));
            compared++;
        }

        Assert.True(compared > 10);
    }

    [Fact]
    public void A_compound_sounds_exactly_like_the_loose_clips()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 2);
        b.AddMedia("a", Rational.Fps30, 900, backend: backend);
        b.AddMedia("b", Rational.Fps30, 900, video: false, backend: backend);
        b.AddVideo(0, "a", S(0.5), S(1), S(6));
        b.AddAudio(0, "a", S(0.5), S(1), S(6));
        b.AddAudio(1, "b", S(2), S(3), S(5));
        using OfflineAudio expected = new(b.Project, b.Sequence.Id, @"C:\proyecto", backend);
        var mix = new float[48_000 * 2 * 8];
        expected.Seek(S(0));
        expected.Read(mix);

        Compound(b, Every(b.Sequence));
        using OfflineAudio actual = new(b.Project, b.Sequence.Id, @"C:\proyecto", backend);
        var compoundMix = new float[mix.Length];
        actual.Seek(S(0));
        actual.Read(compoundMix);

        Assert.Equal(mix, compoundMix);
        Assert.Contains(mix, s => s != 0);
    }

    private static (TimelineBuilder, FakeTimelineBackend, FakeEffectContent) Full()
    {
        var backend = new FakeTimelineBackend();
        var content = new FakeEffectContent();
        return (OfflineProjects.Full(Rational.Fps30, backend, content), backend, content);
    }

    private static List<string> Every(Sequence sequence) => [.. sequence.AllTracks.SelectMany(t => t.Clips).Select(c => c.Id)];

    private static string Compound(TimelineBuilder builder, List<string> clips)
    {
        SequenceEditCommand create = Edits.CreateCompound(builder.Sequence.Id, clips, "Compuesto");
        new UndoStack(builder.Project).Execute(create);
        Assert.Empty(ModelValidator.Validate(builder.Project));
        return create.CreatedIds[0];
    }

    /// <summary>Las capas sin el instante: el compuesto muestra la secuencia anidada desplazada.</summary>
    private static string Layers(CompositionFrame frame)
    {
        string text = OfflineProjects.Describe(frame);
        return text[text.IndexOf(' ', StringComparison.Ordinal)..];
    }

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
