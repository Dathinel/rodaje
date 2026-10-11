using System.Text.Json.Nodes;
using Rodaje.Core.Commands;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Commands;
using Rodaje.Core.Tests.Timing;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Model.ModelSamples;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>Formato 4: velocidad de los clips, sus reglas, la migración desde 3 y los arreglos del validador.</summary>
public class FormatV4Tests
{
    private static string ExamplePath =>
        Path.Combine(ProjectSchemaTests.FindRepoRoot(), "tests", "Rodaje.Core.Tests", "Serialization", "ejemplos", "proyecto-v4.rodaje");

    private static string Example => File.ReadAllText(ExamplePath).ReplaceLineEndings("\n");

    private static Clip ClipOf(Project p, string id) => p.Sequences[0].GetClip(id).Clip;

    /// <summary>Le da a un clip la velocidad y la duración más larga que cabe en su tramo del origen.</summary>
    private static void Retime(Clip clip, ClipSpeed speed)
    {
        clip.Speed = speed;
        ClipTimeMap.Fit(clip.Start, clip.SourceIn, clip.SourceOut, speed).ApplyTo(clip);
    }

    private static void AssertSingleError(Action<Project> breakIt, string expected)
    {
        Project p = ValidProject();
        breakIt(p);
        string error = Assert.Single(ModelValidator.Validate(p));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Example_LoadsAndIsCanonical()
    {
        Project project = ProjectSerializer.Deserialize(Example);

        // Al guardar se escribe la versión actual; el resto del archivo no cambia.
        string saved = Example.Replace("\"schemaVersion\": 4", "\"schemaVersion\": 5", StringComparison.Ordinal);
        Assert.Equal(saved, ProjectSerializer.Serialize(project) + "\n");
        Assert.Equal(saved, ProjectSerializer.Serialize(project.Clone()) + "\n");

        Clip frozen = ClipOf(project, "clip-3");
        Assert.True(frozen.Speed!.Freeze);
        Assert.Equal(Time.FromTicks(1_411_200_000), frozen.Duration);
        Clip reverse = ClipOf(project, "clip-4");
        Assert.Equal(FrameSampling.Blend, reverse.Speed!.FrameSampling);
        Assert.Equal(reverse.SourceOut - Time.FromTicks(1), reverse.TimeMap.Source(reverse.Start));
        Clip curved = ClipOf(project, "clip-7");
        Assert.Equal(3, curved.Speed!.Curve!.Points.Count);
        Assert.False(curved.Speed.PreservePitch);
        Assert.Equal(0.5, curved.TimeMap.RateAt(Time.Zero), 12);
        Assert.Null(ClipOf(project, "clip-1").Speed);
    }

    [Fact]
    public void ClipWithoutSpeed_WritesNoDurationNorSpeed()
    {
        string json = ProjectSerializer.Serialize(ValidProject());
        Assert.DoesNotContain("\"speed\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"duration\": 3", json, StringComparison.Ordinal);

        // Una duración sin velocidad se ignora: manda el tramo del origen.
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        root["sequences"]![0]!["videoTracks"]![0]!["clips"]![0]!["duration"] = 5;
        Assert.Equal(Sec(10), ProjectSerializer.Deserialize(root.ToJsonString()).Sequences[0].VideoTracks[0].Clips[0].Duration);
    }

    [Fact]
    public void Migrate_V3_RemovesTheReservedSpeedEffect()
    {
        JsonObject root = JsonNode.Parse(Example)!.AsObject();
        root["schemaVersion"] = 3;
        JsonObject track = root["sequences"]![0]!["videoTracks"]![0]!.AsObject();
        track["effects"]!.AsArray().Add(new JsonObject { ["id"] = "s1", ["type"] = BuiltInEffects.Speed });
        track["clips"]![0]!["effects"]!.AsArray().Insert(0, new JsonObject { ["id"] = "s2", ["type"] = BuiltInEffects.Speed });
        track["clips"]![0]!["effects"]!.AsArray().Add(new JsonObject { ["id"] = "s3", ["type"] = BuiltInEffects.Speed });
        track["clips"]![1]!["effects"] = null;
        root["sequences"]![0]!["audioTracks"]![0]!["clips"]![0]!["effects"]!.AsArray().Add(JsonValue.Create(3));

        ProjectMigrator.RemoveSpeedEffects(root);

        Assert.Equal(4, (int)root["schemaVersion"]!);
        string migrated = root.ToJsonString();
        Assert.DoesNotContain(BuiltInEffects.Speed, migrated, StringComparison.Ordinal);
        Assert.Contains("\"effect-1\"", migrated, StringComparison.Ordinal);
    }

    [Fact]
    public void ReservedSpeedEffect_IsHiddenAndRejected()
    {
        Assert.True(EffectRegistry.Find(BuiltInEffects.Speed)!.Hidden);
        Assert.Equal(8, EffectRegistry.Find(BuiltInEffects.Speed)!.Code);
        Assert.False(EffectRegistry.Find(BuiltInEffects.Transform)!.Hidden);
        AssertSingleError(p => ClipOf(p, "c2").Effects.Add(new EffectInstance { Id = "sp", Type = BuiltInEffects.Speed }), "reservado");
    }

    [Fact]
    public void ValidSpeeds_PassAndSurviveTheRoundTrip()
    {
        Project p = ValidProject();
        Retime(ClipOf(p, "c2"), new ClipSpeed { Rate = new Rational(2, 1) });
        Retime(ClipOf(p, "c3"), new ClipSpeed { Curve = RetimeMathTests.Ramp(Sec(2).Ticks), Reverse = true });
        Retime(ClipOf(p, "c5"), new ClipSpeed { Rate = new Rational(1001, 1000), PreservePitch = false, Offset = Time.FromTicks(-7) });
        Retime(ClipOf(p, "c1"), new ClipSpeed());
        ClipOf(p, "c4").Speed = new ClipSpeed();
        ClipOf(p, "c4").Duration = ClipOf(p, "c4").SourceDuration;

        Assert.Empty(ModelValidator.Validate(p));
        Project back = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(p));
        Assert.Equal(ProjectSerializer.Serialize(p), ProjectSerializer.Serialize(back));
        Assert.Equal(Sec(2.5), ClipOf(back, "c2").Duration);
    }

    public static TheoryData<string, Action<Clip>> BrokenSpeeds => new()
    {
        { "fuera de 0,1× a 100×", c => c.Speed!.Rate = new Rational(1, 11) },
        { "fuera de 0,1× a 100×", c => c.Speed!.Rate = default },
        { "junto con una velocidad constante", c => c.Speed!.Curve = RetimeMathTests.Ramp(1000) },
        { "debe empezar en 0", c => { c.Speed!.Rate = new Rational(1, 1); c.Speed.Curve = new SpeedCurve { Span = Time.FromTicks(5), Points = [new(1, 100), new(10_000, 100)] }; } },
        { "invertido o con otra velocidad", c => { c.Speed!.Rate = new Rational(1, 1); c.Speed.Freeze = true; c.Speed.Reverse = true; } },
        { "muestreo de cuadros desconocido", c => c.Speed!.FrameSampling = (FrameSampling)9 },
        { "largo máximo", c => c.Speed!.Offset = Time.FromTicks(RetimeMath.MaxExtent) },
        { "no corresponde a su velocidad", c => c.SourceOut += Time.FromTicks(1) },
        { "duración cero o negativa", c => c.Duration = Time.Zero },
        { "tiempos fuera de rango", c => { c.SourceIn = Time.FromTicks(long.MaxValue - 10); c.SourceOut = Time.FromTicks(long.MaxValue); } },
    };

    [Theory]
    [MemberData(nameof(BrokenSpeeds))]
    public void BrokenSpeeds_AreReported(string expected, Action<Clip> breakIt)
    {
        Project p = ValidProject();
        Clip clip = ClipOf(p, "c2");
        Retime(clip, new ClipSpeed { Rate = new Rational(2, 1) });
        Assert.Empty(ModelValidator.Validate(p));

        breakIt(clip);

        Assert.Contains(ModelValidator.Validate(p), e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void FreezeInAnAudioTrack_IsRejected() =>
        AssertSingleError(p =>
        {
            Clip clip = ClipOf(p, "c5");
            clip.Speed = new ClipSpeed { Freeze = true };
            clip.SourceOut = clip.SourceIn + Time.FromTicks(1);
            clip.Duration = Sec(3);
        }, "congelado en una pista de audio");

    [Fact]
    public void LinkedClips_NeedTheSameSpeed()
    {
        AssertSingleError(p => Retime(ClipOf(p, "c1"), new ClipSpeed { Rate = new Rational(2, 1) }), "no tienen la misma velocidad");
        AssertSingleError(p =>
        {
            Retime(ClipOf(p, "c1"), new ClipSpeed { Rate = new Rational(2, 1) });
            Retime(ClipOf(p, "c4"), new ClipSpeed { Rate = new Rational(2, 1), Reverse = true });
        }, "no tienen la misma velocidad");

        Project same = ValidProject();
        Retime(ClipOf(same, "c1"), new ClipSpeed { Rate = new Rational(2, 1) });
        Retime(ClipOf(same, "c4"), new ClipSpeed { Rate = new Rational(4, 2), PreservePitch = false });
        Assert.Empty(ModelValidator.Validate(same));
    }

    [Theory]
    [InlineData("c\n2")]
    [InlineData("c\u00002")]
    [InlineData("c\u007f")]
    public void IdsWithControlCharacters_AreRejected(string id) =>
        AssertSingleError(p => ClipOf(p, "c2").Id = id, "caracteres de control");

    [Theory]
    [InlineData(1e308)]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ParametersOutsideTheRegistryRange_AreRejected(double value)
    {
        AssertSingleError(p => ClipOf(p, "c1").Effects[0].Parameters["opacity"].Value = value, "fuera de su rango");
        AssertSingleError(p => ClipOf(p, "c1").Effects[0].Parameters["opacity"].Keyframes[1] = new Keyframe(Sec(1), value), "fuera de su rango");
    }

    [Fact]
    public void ParametersOfUnknownTypesOrNames_AreNotRangeChecked()
    {
        Project p = ValidProject();
        ClipOf(p, "c1").Effects[0].Parameters["otro"] = new Parameter { Value = 1e308 };
        ClipOf(p, "c2").Effects.Add(new EffectInstance { Id = "u", Type = "otro.efecto", Parameters = { ["opacity"] = new Parameter { Value = 9 } } });
        ClipOf(p, "c2").Effects.Add(new EffectInstance { Id = "x", Type = BuiltInEffects.Transform, Parameters = { ["x"] = new Parameter { Value = 1e300 } } });
        Assert.Empty(ModelValidator.Validate(p));
    }

    [Fact]
    public void TransitionParametersOutsideTheRange_AreRejected()
    {
        Project p = ValidProject();
        Track v1 = p.Sequences[0].VideoTracks[0];
        ClipOf(p, "c2").Start = Sec(10);
        v1.Transitions.Add(new TransitionInstance
        {
            Id = "t1", Type = BuiltInTransitions.Wipe, From = "c1", To = "c2", Duration = Sec(1),
            Parameters = { [BuiltInTransitions.WipeParameters.Angle] = 720 },
        });
        string error = Assert.Single(ModelValidator.Validate(p));
        Assert.Contains("fuera de su rango", error, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectEdits_ClampToTheRegistryRange()
    {
        Project project = CommandFixtures.Create();
        var stack = new UndoStack(project);
        string seq = CommandFixtures.SequenceId;

        stack.Execute(Edits.SetParameter(seq, "clip-2", "fx-1", "opacity", 5, Sec(10)));
        stack.Execute(Edits.SetKeyframe(seq, "clip-2", "fx-1", "opacity", Sec(11), -3));
        stack.Execute(Edits.SetParameter(seq, "clip-2", "fx-1", "scale", 1e9, Sec(10)));
        stack.Execute(Edits.ResetParameters(seq, "clip-2", "fx-1", new Dictionary<string, double> { ["x"] = 4 }));

        EffectInstance fx = ClipOf(project, "clip-2").Effects[0];
        Assert.Equal(1, fx.Parameters["opacity"].Keyframes[0].Value);
        Assert.Equal(0, fx.Parameters["opacity"].Keyframes[1].Value);
        Assert.Equal(100, fx.Parameters["scale"].Value);
        Assert.Equal(4, fx.Parameters["x"].Value);
        Assert.Empty(ModelValidator.Validate(project));
    }

    [Fact]
    public void Snapshot_SeesSpeedAndDuration()
    {
        Project p = ValidProject();
        Clip clip = ClipOf(p, "c2");
        Retime(clip, new ClipSpeed { Curve = RetimeMathTests.Ramp(Sec(5).Ticks) });
        var snapshot = SequenceSnapshot.Capture(p.Sequences[0]);
        Assert.True(snapshot.Matches(p.Sequences[0]));

        clip.Speed!.Curve!.Points[1] = new SpeedPoint(5_000, 401);
        Assert.False(snapshot.Matches(p.Sequences[0]));
        snapshot.Restore(p.Sequences[0]);
        Assert.Equal(400, ClipOf(p, "c2").Speed!.Curve!.Points[1].Hundredths);

        ClipOf(p, "c2").Duration += Time.FromTicks(1);
        Assert.False(snapshot.Matches(p.Sequences[0]));
        snapshot.Restore(p.Sequences[0]);
        ClipOf(p, "c2").Speed = null;
        Assert.False(snapshot.Matches(p.Sequences[0]));
    }

    [Fact]
    public void ClipSpeed_CloneAndComparisons()
    {
        var speed = new ClipSpeed { Curve = RetimeMathTests.Ramp(100), Offset = Time.FromTicks(3), FrameSampling = FrameSampling.OpticalFlow };
        ClipSpeed copy = speed.Clone();
        var clip = new Clip { Speed = speed };
        Clip clipCopy = clip.Clone();

        Assert.NotSame(speed.Curve, copy.Curve);
        Assert.NotSame(speed.Curve, clipCopy.Speed!.Curve);
        Assert.True(speed.ContentEquals(copy));
        Assert.False(speed.IsIdentity);
        Assert.True(new ClipSpeed().IsIdentity);
        Assert.True(speed.SameTiming(new ClipSpeed { Curve = RetimeMathTests.Ramp(100) }));
        Assert.False(speed.ContentEquals(new ClipSpeed { Curve = RetimeMathTests.Ramp(100) }));
        Assert.False(speed.SameTiming(new ClipSpeed()));
        Assert.False(new ClipSpeed().SameTiming(speed));
        Assert.Throws<ArgumentNullException>(() => speed.SameTiming(null!));
        Assert.Throws<ArgumentNullException>(() => speed.ContentEquals(null!));
        Assert.Throws<ArgumentNullException>(() => speed.Curve!.ContentEquals(null!));
        Assert.Equal(clip.SourceDuration, Time.Zero);
    }
}
