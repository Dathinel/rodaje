using CsCheck;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Commands;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Properties;

/// <summary>
/// Tarea 41: sobrescribir sobre el principio de un cuadro congelado lo recorta sin cambiar su origen ni la curva de
/// los efectos de pista. La propiedad 8 lo tomaba por un clip que no se había movido y comparaba su curva en otro punto.
/// </summary>
public class OverwriteFrozenClipTests
{
    /// <summary>La semilla que hizo fallar la propiedad 8 en la CI: «Overwrite cambia el gainDb del efecto de pista».</summary>
    [Fact]
    public void LaSemillaDeLaTarea41_CumpleTodasLasReglas() =>
        Gen.Select(RandomTimeline.GenProject, RandomTimeline.GenOp.Array[1, 40]).Sample(
            s => EditPropertiesTests.RunScenario(s.Item1, s.Item2),
            seed: "5ZOfD5KG8C16",
            iter: 1,
            threads: 1);

    [Fact]
    public void SobrescribirElPrincipioDeUnCongelado_LoRecortaSinTocarLaCurvaDeLaPista()
    {
        Project project = Create();
        Sequence sequence = Seq(project);
        Track track = sequence.GetTrack("track-v1");
        track.Effects.Add(new EffectInstance
        {
            Id = "effect-pista",
            Type = "rodaje.vignette",
            Parameters = new Dictionary<string, Parameter>
            {
                ["gainDb"] = new Parameter { Value = -6, Keyframes = [new Keyframe(S(0), 19), new Keyframe(S(30), -6)] },
            },
        });

        var stack = new UndoStack(project);
        SequenceEditCommand freeze = Edits.FreezeFrame(SequenceId, "clip-2", S(12), S(3));
        stack.Execute(freeze);
        string frozenId = freeze.CreatedIds[0];
        Clip frozen = track.Clips.Single(c => c.Id == frozenId);
        Assert.Equal(S(12), frozen.Start);
        Assert.Equal(S(3), frozen.Duration);
        (Time sourceIn, Time sourceOut) = (frozen.SourceIn, frozen.SourceOut);

        List<Keyframe> keyframes = [.. track.Effects[0].Parameters["gainDb"].Keyframes];
        string beforeOverwrite = ProjectDump.Of(project);

        // Tapa medio segundo antes del congelado y medio segundo de su principio.
        stack.Execute(Edits.Overwrite(SequenceId, "media-1", S(40), S(41), "track-v1", S(11.5)));

        // El congelado se recorta: empieza más tarde, dura menos y sigue mostrando el mismo cuadro.
        Assert.Equal(S(12.5), frozen.Start);
        Assert.Equal(S(2.5), frozen.Duration);
        Assert.Equal(sourceIn, frozen.SourceIn);
        Assert.Equal(sourceOut, frozen.SourceOut);

        // Sobrescribir no corre nada, así que la curva de la pista (en tiempo de la secuencia) queda igual.
        Assert.Equal(keyframes, track.Effects[0].Parameters["gainDb"].Keyframes);

        Assert.True(stack.Undo());
        Assert.Equal(beforeOverwrite, ProjectDump.Of(project));
    }
}
