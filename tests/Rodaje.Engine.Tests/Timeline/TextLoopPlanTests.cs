using System.Diagnostics;
using Rodaje.Core.Model;
using Rodaje.Core.Text;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;

namespace Rodaje.Engine.Tests.Timeline;

public sealed class TextLoopPlanTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(TextLoopKind.Wobble)]
    [InlineData(TextLoopKind.Shake)]
    [InlineData(TextLoopKind.Pulse)]
    [InlineData(TextLoopKind.Float)]
    public void Preview_and_export_use_identical_loop_transforms_on_every_frame(TextLoopKind kind)
    {
        var backend = new FakeTimelineBackend();
        var titles = new TimelineTitleTests.FakeTitles { AlwaysReady = true };
        var builder = new TimelineBuilder(1, 0);
        TextStyle style = TimelineTitleTests.Animated();
        style.Loop = new StyleLoop { Kind = kind };
        builder.AddTitle(0, Time.FromSeconds(0.5), Time.FromSeconds(2), "Loop", style);
        using TimelineSession preview = TimelineSessionTests.Open(builder, backend, new TimelineSessionOptions { Titles = titles });
        using OfflineTimeline offline = OfflineProjects.Open(builder, backend, new OfflineTimelineOptions { Titles = titles });
        for (long frame = 0; frame < offline.FrameCount; frame++)
        {
            preview.SeekToFrame(frame);
            for (int attempt = 0; ; attempt++)
            {
                backend.RunDecoders();
                using CompositionFrame expected = preview.Tick();
                if (!preview.IsSeekPending)
                {
                    using CompositionFrame actual = offline.Render(frame, CancellationToken.None);
                    Assert.Equal(OfflineProjects.Describe(expected), OfflineProjects.Describe(actual));
                    break;
                }

                Assert.True(attempt < 20);
            }
        }
    }

    private static Clip Title(TextLoopKind? kind) => new()
    {
        Id = "loop",
        Start = Time.FromSeconds(7),
        SourceIn = Time.FromSeconds(2),
        SourceOut = Time.FromSeconds(12),
        Generator = new ClipGenerator
        {
            Text = "Movimiento",
            Style = new TextStyle { Loop = kind is { } value ? new StyleLoop { Kind = value } : null },
        },
        Effects =
        [
            new EffectInstance
            {
                Id = "transform",
                Type = BuiltInEffects.Transform,
                Parameters =
                {
                    ["x"] = new Parameter { Value = 123 },
                    ["y"] = new Parameter { Value = -45 },
                    ["scale"] = new Parameter { Value = 1.7 },
                    ["rotation"] = new Parameter { Value = 37 },
                    ["opacity"] = new Parameter { Value = 0.7 },
                },
            },
        ],
    };

    [Theory]
    [InlineData(TextLoopKind.Wobble)]
    [InlineData(TextLoopKind.Shake)]
    [InlineData(TextLoopKind.Pulse)]
    [InlineData(TextLoopKind.Float)]
    public void TransformAt_composes_reference_delta_in_clip_space_and_copies_style(TextLoopKind kind)
    {
        Clip clip = Title(kind);
        GeneratedClipPlan plan = GeneratedClipPlan.From(clip, 960, 540)!;
        GeneratedClipPlan plain = GeneratedClipPlan.From(Title(null), 960, 540)!;
        StyleLoop loop = clip.Generator!.Style!.Loop!.Clone();
        clip.Generator.Style.Loop.Amount = 2;
        foreach (double t in new[] { 0.0, 0.17, 1.25, 8.1 })
        {
            Time timeline = clip.Start + Time.FromSeconds(t);
            LayerTransform original = plain.TransformAt(timeline);
            UnitPose delta = TextLoops.Delta(loop, (timeline - clip.Start).ToSeconds());
            var expected = TitleMotion.Compose(original.X, original.Y, original.Scale, original.RotationDegrees, original.Opacity,
                delta.X * 0.5, delta.Y * 0.5, delta.Scale, delta.Rotation, delta.Opacity);
            LayerTransform actual = plan.TransformAt(timeline);
            Assert.Equal(expected.X, actual.X, 9);
            Assert.Equal(expected.Y, actual.Y, 9);
            Assert.Equal(expected.Scale, actual.Scale, 9);
            Assert.Equal(expected.Rotation, actual.RotationDegrees, 9);
            Assert.Equal(expected.Opacity, actual.Opacity, 9);
            Assert.Equal(actual, plan.TransformAt(timeline));
        }
    }

    [Fact]
    public void A_split_piece_continues_the_loop_phase_of_the_whole_title()
    {
        Clip whole = Title(TextLoopKind.Float);
        Clip piece = Title(TextLoopKind.Float);
        piece.Generator!.Edges.Before = Time.FromSeconds(0.5);
        GeneratedClipPlan a = GeneratedClipPlan.From(whole, 1920, 1080)!;
        GeneratedClipPlan b = GeneratedClipPlan.From(piece, 1920, 1080)!;
        foreach (double t in new[] { 0.0, 0.3, 1.1 })
        {
            Assert.Equal(a.TransformAt(whole.Start + Time.FromSeconds(t + 0.5)).Y, b.TransformAt(piece.Start + Time.FromSeconds(t)).Y, 9);
        }

        Assert.NotEqual(a.TransformAt(whole.Start).Y, b.TransformAt(piece.Start).Y);
    }

    [Fact]
    public void Loop_runs_during_entry_exit_and_when_edges_are_disabled()
    {
        Clip clip = Title(TextLoopKind.Float);
        clip.Generator!.Style!.In = new StyleAnimation { DurationSeconds = 1, Curves = { ["opacity"] = [new(0, 0), new(1, 1)] } };
        clip.Generator.Style.Out = new StyleAnimation { DurationSeconds = 1, Curves = { ["opacity"] = [new(0, 1), new(1, 0)] } };
        GeneratedClipPlan plan = GeneratedClipPlan.From(clip, 1920, 1080)!;
        foreach (double t in new[] { 0.25, 9.75 })
        {
            Time timeline = clip.Start + Time.FromSeconds(t);
            LayerTransform original = EffectPlan.Transform(plan.Effects, plan.TimeMap.EffectTime(timeline));
            UnitPose delta = TextLoops.Delta(clip.Generator.Style.Loop!, t);
            // El bucle va por fuera de la entrada y la salida, como quedarían al convertirlas en keyframes del clip.
            LayerTransform expected = plan.Animation!.Apply(original, timeline - clip.Start);
            expected = TitleAnimation.Compose(expected, delta.X, delta.Y, delta.Scale, delta.Rotation, delta.Opacity);
            Assert.Equal(expected, plan.TransformAt(timeline));
        }

        clip.Generator.Edges.In = false;
        clip.Generator.Edges.Out = false;
        Assert.Null(GeneratedClipPlan.From(clip, 1920, 1080)!.Animation);
        Assert.NotEqual(GeneratedClipPlan.From(Title(null), 1920, 1080)!.TransformAt(clip.Start + Time.FromSeconds(0.25)),
            GeneratedClipPlan.From(clip, 1920, 1080)!.TransformAt(clip.Start + Time.FromSeconds(0.25)));
    }

    [Trait("Categoria", "Rendimiento")]
    [Theory]
    [InlineData(TextLoopKind.Wobble)]
    [InlineData(TextLoopKind.Shake)]
    [InlineData(TextLoopKind.Pulse)]
    [InlineData(TextLoopKind.Float)]
    public void Hundred_thousand_calls_allocate_nothing_and_cost_less_than_three_times_plain(TextLoopKind kind)
    {
        GeneratedClipPlan plain = GeneratedClipPlan.From(Title(null), 1920, 1080)!;
        GeneratedClipPlan loop = GeneratedClipPlan.From(Title(kind), 1920, 1080)!;
        // Calentar ambos caminos, el JIT y los contadores antes de medir.
        _ = Measure(plain);
        _ = Measure(loop);
        long plainTicks = long.MaxValue;
        long loopTicks = long.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            var a = Measure(plain);
            var b = Measure(loop);
            plainTicks = Math.Min(plainTicks, a.Ticks);
            loopTicks = Math.Min(loopTicks, b.Ticks);
            Assert.InRange(a.Bytes, 0, 65536);
            Assert.InRange(b.Bytes, 0, 65536);
        }

        output.WriteLine($"{kind}: {loopTicks / (double)plainTicks:0.000}x; 100000 llamadas, margen total 64 KB");
        Assert.True(loopTicks < plainTicks * 8, $"Con bucle: {loopTicks}; sin bucle: {plainTicks}");
    }

    private static (long Ticks, long Bytes) Measure(GeneratedClipPlan plan)
    {
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        double sum = 0;
        for (int i = 0; i < 100_000; i++)
        {
            LayerTransform transform = plan.TransformAt(Time.FromTicks(plan.Start.Ticks + i * 7001L));
            sum += transform.X + transform.Y + transform.Scale + transform.RotationDegrees;
        }

        long ticks = Stopwatch.GetTimestamp() - start;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Assert.True(double.IsFinite(sum));
        return (ticks, allocated);
    }
}
