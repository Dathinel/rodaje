using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Model.ModelSamples;

namespace Rodaje.Core.Tests.Model;

public class ModelValidatorTests
{
    private static Sequence Seq(Project p) => p.Sequences[0];

    private static Clip ClipOf(Project p, string id) => Seq(p).GetClip(id).Clip;

    /// <summary>Aplica un cambio a un proyecto válido y comprueba que aparece exactamente un error.</summary>
    private static void AssertSingleError(Action<Project> breakIt, string expected)
    {
        Project p = ValidProject();
        breakIt(p);
        Assert.Equal([expected], ModelValidator.Validate(p));
    }

    [Fact]
    public void ValidProject_HasNoErrors()
    {
        Project p = ValidProject();
        Assert.Empty(ModelValidator.Validate(p));
        Assert.Empty(ModelValidator.ValidateSequence(p, Seq(p)));
        ModelValidator.ThrowIfInvalid(p);
    }

    [Fact]
    public void EmptyProject_IsValidIfItHasAnId()
    {
        Assert.Empty(ModelValidator.Validate(new Project { Id = "p" }));
        Assert.Equal(["Hay un proyecto sin identificador."], ModelValidator.Validate(new Project()));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Project p = ValidProject();
        Assert.Throws<ArgumentNullException>(() => ModelValidator.Validate(null!));
        Assert.Throws<ArgumentNullException>(() => ModelValidator.ThrowIfInvalid(null!));
        Assert.Throws<ArgumentNullException>(() => ModelValidator.ValidateSequence(null!, Seq(p)));
        Assert.Throws<ArgumentNullException>(() => ModelValidator.ValidateSequence(p, null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(-1)]
    public void WrongSchemaVersion(int version) =>
        AssertSingleError(p => p.SchemaVersion = version, $"La versión de formato es {version} y se esperaba 2.");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyIds_ForEveryEntityKind(string empty)
    {
        AssertSingleError(p => p.Id = empty, "Hay un proyecto sin identificador.");
        AssertSingleError(p => p.Media.Add(new MediaRef { Id = empty, Duration = Sec(1) }), "Hay un medio sin identificador.");
        AssertSingleError(p => Seq(p).Id = empty, "Hay una secuencia sin identificador.");
        AssertSingleError(p => Seq(p).VideoTracks[1].Id = empty, "Hay una pista sin identificador.");
        AssertSingleError(p => ClipOf(p, "c3").Id = empty, "Hay un clip sin identificador.");
        AssertSingleError(p => ClipOf(p, "c1").Effects[0].Id = empty, "Hay un efecto sin identificador.");
    }

    [Fact]
    public void NullId_IsReportedAsMissing()
    {
        AssertSingleError(p => ClipOf(p, "c3").Id = null!, "Hay un clip sin identificador.");
    }

    [Fact]
    public void DuplicateIds_AcrossEntityKinds()
    {
        AssertSingleError(p => p.Media.Add(new MediaRef { Id = "m1", Duration = Sec(1) }), "El identificador «m1» está repetido (un medio).");
        AssertSingleError(p => p.Media.Add(new MediaRef { Id = "p1", Duration = Sec(1) }), "El identificador «p1» está repetido (un medio).");
        AssertSingleError(
            p => p.Sequences.Add(new Sequence { Id = "m2" }),
            "El identificador «m2» está repetido (una secuencia).");
        AssertSingleError(p => Seq(p).VideoTracks[1].Id = "s1", "El identificador «s1» está repetido (una pista).");
        AssertSingleError(p => Seq(p).AudioTracks[0].Id = "c3", "El identificador «c3» está repetido (una pista).");
        AssertSingleError(p => ClipOf(p, "c3").Id = "m1", "El identificador «m1» está repetido (un clip).");
        AssertSingleError(p => ClipOf(p, "c5").Id = "c1", "El identificador «c1» está repetido (un clip).");
        // El efecto de c1 toma el id «c2» antes de que se revise el clip c2, así que el repetido es el clip
        AssertSingleError(p => ClipOf(p, "c1").Effects[0].Id = "c2", "El identificador «c2» está repetido (un clip).");
        AssertSingleError(p => ClipOf(p, "c1").Effects[0].Id = "v1", "El identificador «v1» está repetido (un efecto).");
    }

    [Fact]
    public void DuplicateEffectIds()
    {
        AssertSingleError(
            p => ClipOf(p, "c2").Effects.Add(new EffectInstance { Id = "fx1", Type = "x" }),
            "El identificador «fx1» está repetido (un efecto).");
        AssertSingleError(
            p => ClipOf(p, "c1").Effects.Add(new EffectInstance { Id = "fx1", Type = "x" }),
            "El identificador «fx1» está repetido (un efecto).");
    }

    [Fact]
    public void MediaWithNonPositiveDuration()
    {
        AssertSingleError(
            p => p.Media.Add(new MediaRef { Id = "m3", Duration = Time.Zero }),
            "El medio «m3» tiene una duración no positiva.");
        AssertSingleError(
            p => p.Media.Add(new MediaRef { Id = "m3", Duration = Time.FromTicks(-1) }),
            "El medio «m3» tiene una duración no positiva.");
        Project ok = ValidProject();
        ok.Media.Add(new MediaRef { Id = "m3", Duration = Time.FromTicks(1) });
        Assert.Empty(ModelValidator.Validate(ok));
    }

    [Theory]
    [InlineData("C:\\videos\\toma\0.mp4")]
    [InlineData("toma|1.mp4")]
    [InlineData("<toma>.mp4")]
    [InlineData("toma\u0007.mp4")]
    public void MediaWithInvalidPathCharacters(string path)
    {
        AssertSingleError(
            p => p.Media.Add(new MediaRef { Id = "m3", Duration = Sec(1), Path = path }),
            "El medio «m3» tiene una ruta con caracteres que no puede tener una ruta de archivo.");
        Project ok = ValidProject();
        ok.Media.Add(new MediaRef { Id = "m3", Duration = Sec(1), Path = @"D:\Programas de uso libre\toma ñandú (1).mp4" });
        Assert.Empty(ModelValidator.Validate(ok));
    }

    [Fact]
    public void SequenceWithNonPositiveSizeOrSampleRate()
    {
        const string expected = "La secuencia «s1» tiene dimensiones o frecuencia de muestreo no positivas.";
        AssertSingleError(p => Seq(p).Width = 0, expected);
        AssertSingleError(p => Seq(p).Height = -1080, expected);
        AssertSingleError(p => Seq(p).SampleRate = 0, expected);
        AssertSingleError(p => (Seq(p).Width, Seq(p).Height, Seq(p).SampleRate) = (0, 0, 0), expected);
    }

    [Fact]
    public void SequenceWithInvalidFrameRate()
    {
        // default(Rational) es 0/0: no se puede construir con el constructor, pero sí llegar por JSON
        AssertSingleError(p => Seq(p).FrameRate = default, "La secuencia «s1» tiene una tasa de cuadros no válida.");
    }

    [Fact]
    public void TrackInTheWrongList()
    {
        AssertSingleError(
            p => Seq(p).VideoTracks[1].Kind = TrackKind.Audio,
            "La pista «V2» de la secuencia «s1» está en la lista equivocada (es de tipo Audio).");
        AssertSingleError(
            p => Seq(p).AudioTracks.Add(new Track { Id = "a2", Name = "A2", Kind = TrackKind.Video }),
            "La pista «A2» de la secuencia «s1» está en la lista equivocada (es de tipo Video).");
    }

    [Fact]
    public void ClipWithMissingMedia() =>
        AssertSingleError(p => ClipOf(p, "c3").MediaId = "zz", "El clip «c3» de la pista «V2» usa el medio «zz», que no existe.");

    [Fact]
    public void ClipBeyondMediaDuration()
    {
        AssertSingleError(
            p => ClipOf(p, "c3").SourceOut = Sec(61),
            "El clip «c3» de la pista «V2» termina en 00:01:01.000 y el medio solo dura 00:01:00.000.");

        // Terminar justo en el final del medio es válido
        Project ok = ValidProject();
        ClipOf(ok, "c3").SourceOut = Sec(60);
        Assert.Empty(ModelValidator.Validate(ok));
    }

    [Fact]
    public void VideoClipOnMediaWithoutVideo() =>
        AssertSingleError(
            p => ClipOf(p, "c2").MediaId = "m2",
            "El clip «c2» de la pista «V1» está en una pista de video pero su medio no tiene imagen.");

    [Fact]
    public void AudioClipOnMediaWithoutAudio() =>
        AssertSingleError(
            p =>
            {
                p.Media.Add(new MediaRef { Id = "m3", Duration = Sec(60), HasVideo = true });
                ClipOf(p, "c5").MediaId = "m3";
            },
            "El clip «c5» de la pista «A1» está en una pista de audio pero su medio no tiene sonido.");

    [Fact]
    public void NegativeSourceIn() =>
        AssertSingleError(p => ClipOf(p, "c3").SourceIn = Sec(-1), "El clip «c3» de la pista «V2» tiene un punto de entrada negativo.");

    [Fact]
    public void ZeroOrNegativeDuration()
    {
        const string expected = "El clip «c3» de la pista «V2» tiene duración cero o negativa.";
        AssertSingleError(p => ClipOf(p, "c3").SourceOut = Sec(30), expected);
        AssertSingleError(p => ClipOf(p, "c3").SourceOut = Sec(29), expected);
    }

    [Fact]
    public void NegativeStart() =>
        AssertSingleError(p => ClipOf(p, "c3").Start = Sec(-1), "El clip «c3» de la pista «V2» empieza antes del inicio de la línea de tiempo.");

    [Fact]
    public void OverlappingClips()
    {
        AssertSingleError(p => ClipOf(p, "c2").Start = Sec(9), "El clip «c2» de la pista «V1» se solapa con «c1» o está fuera de orden.");

        // Tocarse sin solaparse es válido
        Project ok = ValidProject();
        ClipOf(ok, "c2").Start = Sec(10);
        Assert.Empty(ModelValidator.Validate(ok));
    }

    [Fact]
    public void ClipsOutOfOrder() =>
        AssertSingleError(
            p => Seq(p).VideoTracks[0].Clips.Reverse(),
            "El clip «c1» de la pista «V1» se solapa con «c2» o está fuera de orden.");

    [Fact]
    public void UnorderedKeyframes()
    {
        const string expected = "Los keyframes de «opacity» en el efecto «fx1» no están en orden estricto.";
        AssertSingleError(p => ClipOf(p, "c1").Effects[0].Parameters["opacity"].Keyframes.Reverse(), expected);

        // Dos keyframes en el mismo instante tampoco valen, y se informa una sola vez por parámetro
        AssertSingleError(
            p => ClipOf(p, "c1").Effects[0].Parameters["opacity"].Keyframes =
                [new Keyframe(Sec(1), 0), new Keyframe(Sec(1), 1), new Keyframe(Sec(0), 2)],
            expected);
    }

    [Fact]
    public void TwoClipsOfOneLinkOnTheSameTrack()
    {
        AssertSingleError(p => ClipOf(p, "c2").LinkId = "L1", "El enlace «L1» tiene dos clips en la misma pista «V1».");

        // Clips enlazados en pistas distintas sí son válidos
        Project ok = ValidProject();
        ClipOf(ok, "c3").LinkId = "L1";
        ClipOf(ok, "c2").LinkId = "L2";
        ClipOf(ok, "c5").LinkId = "L2";
        Assert.Empty(ModelValidator.Validate(ok));
    }

    [Fact]
    public void SeveralProblems_AreAllReported()
    {
        Project p = ValidProject();
        p.SchemaVersion = 9;
        ClipOf(p, "c3").MediaId = "zz";
        ClipOf(p, "c3").Start = Sec(-1);
        IReadOnlyList<string> errors = ModelValidator.Validate(p);
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void OtherSequences_AreValidatedToo()
    {
        Project p = ValidProject();
        p.Sequences.Add(new Sequence
        {
            Id = "s2",
            VideoTracks = [new Track { Id = "s2v1", Name = "V1", Clips = [MakeClip("d1", "nada", 0, 0, 1)] }],
        });
        Assert.Equal(["El clip «d1» de la pista «V1» usa el medio «nada», que no existe."], ModelValidator.Validate(p));
    }

    [Fact]
    public void ThrowIfInvalid_ListsEveryError()
    {
        Project p = ValidProject();
        p.SchemaVersion = 1;
        ClipOf(p, "c3").SourceIn = Sec(-1);

        var ex = Assert.Throws<InvalidOperationException>(() => ModelValidator.ThrowIfInvalid(p));
        Assert.Equal(
            "El proyecto no es válido:\n- La versión de formato es 1 y se esperaba 2.\n- El clip «c3» de la pista «V2» tiene un punto de entrada negativo.",
            ex.Message);
    }

    [Fact]
    public void ValidateSequence_OnlyChecksThatSequence()
    {
        Project p = ValidProject();
        p.SchemaVersion = 7;
        p.Id = "";
        p.Media.Add(new MediaRef { Id = "m3", Duration = Time.Zero });
        p.Sequences.Add(new Sequence { Id = "s2", Width = 0 });
        Seq(p).Id = ""; // su propio id lo revisa Validate, no ValidateSequence

        Assert.Empty(ModelValidator.ValidateSequence(p, Seq(p)));
        Assert.Equal(
            ["La secuencia «s2» tiene dimensiones o frecuencia de muestreo no positivas."],
            ModelValidator.ValidateSequence(p, p.Sequences[1]));
    }

    [Fact]
    public void ValidateSequence_IgnoresIdsOutsideTheSequence()
    {
        Project p = ValidProject();
        ClipOf(p, "c3").Id = "m1"; // choca con un medio, pero eso solo lo ve Validate
        Assert.Empty(ModelValidator.ValidateSequence(p, Seq(p)));
        Assert.Single(ModelValidator.Validate(p));
    }

    [Fact]
    public void ValidateSequence_ReportsIdsAndRulesInsideTheSequence()
    {
        Project p = ValidProject();
        ClipOf(p, "c3").Id = "v1";
        Seq(p).AudioTracks[0].Id = " ";
        ClipOf(p, "c2").Start = Sec(9);

        Assert.Equal(
            [
                "El clip «c2» de la pista «V1» se solapa con «c1» o está fuera de orden.",
                "El identificador «v1» está repetido (un clip).",
                "Hay una pista sin identificador.",
            ],
            ModelValidator.ValidateSequence(p, Seq(p)));
    }

    [Fact]
    public void ValidateSequence_WorksForASequenceNotInTheProject()
    {
        Project p = ValidProject();
        var loose = new Sequence
        {
            Id = "suelta",
            AudioTracks = [new Track { Id = "a", Kind = TrackKind.Audio, Name = "A", Clips = [MakeClip("x", "m2", 0, 0, 31)] }],
        };
        Assert.Equal(
            ["El clip «x» de la pista «A» termina en 00:00:31.000 y el medio solo dura 00:00:30.000."],
            ModelValidator.ValidateSequence(p, loose));
    }

    // Valores extremos: el validador recibe tiempos leídos de un archivo y nunca debe lanzar.

    private const string C1OutOfRange = "El clip «c1» de la pista «V1» tiene tiempos fuera de rango.";

    [Fact]
    public void ExtremeStart_IsReportedWithoutOverflow()
    {
        Project p = ValidProject();
        ClipOf(p, "c1").Start = Time.FromTicks(long.MaxValue - 10);

        IReadOnlyList<string> errors = ModelValidator.Validate(p);

        Assert.Contains(C1OutOfRange, errors);
        // El final de c1 se sigue calculando (en 128 bits) y c2 queda fuera de orden detrás de él.
        Assert.Contains("El clip «c2» de la pista «V1» se solapa con «c1» o está fuera de orden.", errors);
    }

    [Fact]
    public void ExtremeSourceIn_IsReportedWithoutOverflow()
    {
        Project p = ValidProject();
        ClipOf(p, "c1").SourceIn = Time.FromTicks(long.MinValue);

        IReadOnlyList<string> errors = ModelValidator.Validate(p);

        Assert.Contains(C1OutOfRange, errors);
        Assert.Contains("El clip «c1» de la pista «V1» tiene un punto de entrada negativo.", errors);
    }

    [Fact]
    public void ExtremeSourceOutBelowSourceIn_IsReportedWithoutOverflow()
    {
        Project p = ValidProject();
        ClipOf(p, "c1").SourceIn = Time.FromTicks(long.MaxValue);
        ClipOf(p, "c1").SourceOut = Time.FromTicks(long.MinValue);

        IReadOnlyList<string> errors = ModelValidator.Validate(p);

        Assert.Contains(C1OutOfRange, errors);
        Assert.Contains("El clip «c1» de la pista «V1» tiene duración cero o negativa.", errors);
    }

    [Fact]
    public void EndPastLongMax_IsOutOfRange()
    {
        // La duración cabe en 64 bits, pero el final (inicio + duración) no.
        Project p = ValidProject();
        Clip c2 = ClipOf(p, "c2");
        c2.SourceIn = Time.Zero;
        c2.SourceOut = Time.FromTicks(long.MaxValue);

        IReadOnlyList<string> errors = ModelValidator.Validate(p);

        Assert.Contains("El clip «c2» de la pista «V1» tiene tiempos fuera de rango.", errors);
    }

    [Fact]
    public void EndExactlyLongMax_IsNotOutOfRange()
    {
        // Límite exacto: inicio 0 y duración long.MaxValue terminan justo en long.MaxValue, que cabe.
        Project p = ValidProject();
        Seq(p).VideoTracks[1].Clips[0].Start = Time.Zero;
        Seq(p).VideoTracks[1].Clips[0].SourceIn = Time.Zero;
        Seq(p).VideoTracks[1].Clips[0].SourceOut = Time.FromTicks(long.MaxValue);
        p.Media[0].Duration = Time.FromTicks(long.MaxValue);

        Assert.Empty(ModelValidator.Validate(p));
        Assert.Equal(long.MaxValue, Seq(p).VideoTracks[1].Clips[0].End.Ticks);
    }

    [Fact]
    public void ExtremeMediaDuration_IsReportedWithoutOverflow()
    {
        Project p = ValidProject();
        p.Media[0].Duration = Time.FromTicks(long.MinValue);

        IReadOnlyList<string> errors = ModelValidator.Validate(p);

        Assert.Contains("El medio «m1» tiene una duración no positiva.", errors);
    }

    [Theory]
    [InlineData(long.MinValue, long.MinValue, long.MinValue)]
    [InlineData(long.MaxValue, long.MaxValue, long.MaxValue)]
    [InlineData(long.MinValue, long.MaxValue, long.MinValue)]
    [InlineData(long.MaxValue, long.MinValue, long.MaxValue)]
    [InlineData(-1, long.MinValue, long.MaxValue)]
    public void AnyCombinationOfExtremes_NeverThrows(long start, long sourceIn, long sourceOut)
    {
        Project p = ValidProject();
        foreach (Clip clip in Seq(p).AllTracks.SelectMany(t => t.Clips))
        {
            clip.Start = Time.FromTicks(start);
            clip.SourceIn = Time.FromTicks(sourceIn);
            clip.SourceOut = Time.FromTicks(sourceOut);
        }

        Assert.NotEmpty(ModelValidator.Validate(p));
        Assert.NotEmpty(ModelValidator.ValidateSequence(p, Seq(p)));
    }
}
