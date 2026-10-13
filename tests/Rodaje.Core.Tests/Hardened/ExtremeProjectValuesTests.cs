using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Hardened;

/// <summary>
/// Proyectos con números extremos pero que el formato admite (tarea 220): una tasa de secuencia cualquiera y tiempos de
/// hasta <see cref="long.MaxValue"/>. O el validador los rechaza con su mensaje, o la edición funciona; nunca una
/// <see cref="OverflowException"/> ni una <see cref="ArgumentOutOfRangeException"/> que cerraría la app.
/// </summary>
public sealed class ExtremeProjectValuesTests
{
    private const long Big = 1L << 62;

    public static TheoryData<long, long> RejectedRates => new()
    {
        { 1, Big },
        { 1, long.MaxValue },
        { Big, 1 },
        { long.MaxValue, 1 },
        { 1, 2 },
        { Big - 1, Big },
        { 1001, 1 },
    };

    public static TheoryData<long, long> AcceptedRates => new()
    {
        { 1, 1 },
        { 1000, 1 },
        { 24000, 1001 },
        { Big, Big - 1 },
        { (1000L << 40) - 1, 1L << 40 },
    };

    [Theory]
    [MemberData(nameof(RejectedRates))]
    public void ASequenceRateOutsideOneToAThousandFps_IsRejected(long num, long den)
    {
        Project project = OneClip(new Rational(num, den), start: 0, length: Time.TicksPerSecond);

        string error = Assert.Single(ModelValidator.Validate(project));
        Assert.Contains("va de 1 a 1000", error, StringComparison.Ordinal);
    }

    /// <summary>Con la tasa en rango, empujar un cuadro y llevar el final al cuadro más cercano caben en un <c>long</c>.</summary>
    [Theory]
    [MemberData(nameof(AcceptedRates))]
    public void ASequenceRateFromOneToAThousandFps_IsAcceptedAndItsFrameMathFits(long num, long den)
    {
        var rate = new Rational(num, den);
        Project project = OneClip(rate, start: Sequence.MaxTime.Ticks - Time.TicksPerSecond, length: Time.TicksPerSecond);

        Assert.Empty(ModelValidator.Validate(project));
        Assert.True(Time.FromFrames(int.MaxValue, rate) > Time.Zero);
        Assert.True(Time.FromFrames(int.MinValue, rate) < Time.Zero);
        Assert.True(Sequence.MaxTime.SnapToFrame(rate) >= Sequence.MaxTime - Time.FromTicks(Time.TicksPerSecond));
    }

    [Fact]
    public void AClipThatEndsAfterTheEndOfTheTimeline_IsRejected_AndOneThatEndsThere_IsNot()
    {
        Assert.Empty(ModelValidator.Validate(OneClip(Rational.Fps25, Sequence.MaxTime.Ticks - 10, 10)));

        // Antes se aceptaba (cabía en un long) y empujarlo un cuadro lanzaba OverflowException.
        string error = Assert.Single(ModelValidator.Validate(OneClip(Rational.Fps25, long.MaxValue - 10, 10)));
        Assert.Contains("termina después del final de la línea de tiempo", error, StringComparison.Ordinal);
        Assert.Single(ModelValidator.Validate(OneClip(Rational.Fps25, Sequence.MaxTime.Ticks - 10, 11)));
    }

    [Fact]
    public void AMarkerOrARegionAfterTheEndOfTheTimeline_IsRejected()
    {
        Project project = OneClip(Rational.Fps25, 0, 10);
        Sequence sequence = project.Sequences[0];
        sequence.Markers.Add(new Marker { Id = "mk", Time = Sequence.MaxTime });
        sequence.Regions.Add(new Region { Id = "rg", Start = Time.Zero, End = Sequence.MaxTime });
        Assert.Empty(ModelValidator.Validate(project));

        sequence.Markers[0].Time = Time.FromTicks(long.MaxValue);
        sequence.Regions[0].End = Time.FromTicks(long.MaxValue);

        IReadOnlyList<string> errors = ModelValidator.Validate(project);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Contains("después del final de la línea de tiempo", e, StringComparison.Ordinal));
    }

    /// <summary>
    /// Un desplazamiento que no cabe (mover, recortar o deslizar por casi todo el rango) lanzaba OverflowException desde
    /// la edición: ahora es una edición imposible más, y el proyecto queda como estaba.
    /// </summary>
    [Fact]
    public void AnEditWhoseTimesOverflow_IsAnEditException_AndChangesNothing()
    {
        Project project = OneClip(Rational.Fps25, 0, Time.TicksPerSecond);
        var history = new UndoStack(project);
        string before = ProjectSerializer.Serialize(project);
        var huge = Time.FromTicks(long.MaxValue);

        SequenceEditCommand[] edits =
        [
            Edits.MoveClips("s", ["c"], huge),
            Edits.Move("s", "c", huge),
            Edits.Trim("s", "c", TrimEdge.Out, huge),
            Edits.RippleTrim("s", "c", TrimEdge.Out, huge, RippleScope.AllTracks),
            Edits.Slip("s", "c", huge),
            Edits.Overwrite("s", "m", Time.Zero, Time.FromTicks(Time.TicksPerSecond), "v", huge),
        ];

        foreach (SequenceEditCommand edit in edits)
        {
            EditException ex = Assert.Throws<EditException>(() => history.Execute(edit));
            Assert.Equal(before, ProjectSerializer.Serialize(project));
            Assert.False(history.CanUndo, ex.Message);
        }
    }

    /// <summary>Invertir un clip de más de 6,9 horas lanzaba ArgumentOutOfRangeException (el largo máximo con velocidad).</summary>
    [Fact]
    public void ReversingAClipLongerThanTheMaximumWithSpeed_IsAnEditException()
    {
        long tenHours = 10L * 3600 * Time.TicksPerSecond;
        Project project = OneClip(Rational.Fps25, 0, tenHours);
        string before = ProjectSerializer.Serialize(project);

        EditException ex = Assert.Throws<EditException>(() => new UndoStack(project).Execute(Edits.SetReverse("s", "c", true)));

        Assert.Contains("demasiado largo para invertirlo", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, ProjectSerializer.Serialize(project));
    }

    /// <summary>
    /// Estirar con Alt un clip corto mucho más allá de 6,9 horas: la velocidad se queda en 0,1× y el ajuste fino al largo
    /// pedido lanzaba ArgumentOutOfRangeException; ahora se queda en lo que da 0,1×.
    /// </summary>
    [Fact]
    public void StretchingAShortClipBeyondTheMaximumWithSpeed_StopsAtTheSlowestSpeed()
    {
        Project project = OneClip(Rational.Fps25, 0, Time.TicksPerSecond);
        var history = new UndoStack(project);

        history.Execute(Edits.RateStretch("s", "c", TrimEdge.Out, Time.FromTicks(10L * 3600 * Time.TicksPerSecond)));

        Clip clip = project.Sequences[0].VideoTracks[0].Clips[0];
        Assert.Equal(RetimeMath.MinRate, clip.Speed!.Rate);
        Assert.Equal(Time.FromTicks(10 * Time.TicksPerSecond), clip.Duration);
        Assert.Empty(ModelValidator.Validate(project));
    }

    /// <summary>Un medio con video y sonido de toda la duración del rango y un clip suyo en la pista «v» de la secuencia «s».</summary>
    private static Project OneClip(Rational rate, long start, long length)
    {
        var project = new Project { Id = "p", Name = "extremo" };
        project.Media.Add(new MediaRef
        {
            Id = "m", Name = "m", Path = "m.mp4", Duration = Time.FromTicks(long.MaxValue), HasVideo = true, HasAudio = true, FrameRate = Rational.Fps25,
            Width = 1920, Height = 1080, SampleRate = 48000, AudioChannels = 2,
        });
        var sequence = new Sequence { Id = "s", Name = "S", FrameRate = rate };
        sequence.VideoTracks.Add(new Track { Id = "v", Kind = TrackKind.Video, Name = "V1" });
        sequence.VideoTracks[0].Clips.Add(new Clip { Id = "c", MediaId = "m", Start = Time.FromTicks(start), SourceIn = Time.Zero, SourceOut = Time.FromTicks(length) });
        project.Sequences.Add(sequence);
        return project;
    }
}
