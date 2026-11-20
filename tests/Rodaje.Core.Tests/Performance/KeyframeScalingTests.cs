using System.Diagnostics;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Xunit.Abstractions;
using static Rodaje.Core.Tests.Commands.CommandFixtures;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Core.Tests.Performance;

/// <summary>Cómo crece el coste con la cantidad de keyframes de un título «tambaleando» (posición, giro, escala y opacidad).</summary>
public class KeyframeScalingTests(ITestOutputHelper output)
{
    private static readonly string[] Names = [T.X, T.Rotation, T.Scale, T.Opacity];

    private static (UndoStack Stack, Project Project, string Effect) Setup(int length)
    {
        Project project = Create();
        var title = new Clip { Id = "titulo", SourceIn = S(0), SourceOut = S(length), Start = S(100), Generator = new ClipGenerator { Text = "Hola", Style = new TextStyle() } };
        Seq(project).VideoTracks[0].Clips.Add(title);
        var stack = new UndoStack(project);
        var add = Edits.AddEffect(SequenceId, "titulo", BuiltInEffects.Transform);
        stack.Execute(add);
        return (stack, project, add.CreatedIds[0]);
    }

    private static double Wobble(int i, string name) => name switch
    {
        T.X => 20 * Math.Sin(i * 0.9),
        T.Rotation => 8 * Math.Sin(i * 1.3),
        T.Scale => 1 + (0.05 * Math.Sin(i * 0.7)),
        _ => 0.8 + (0.2 * Math.Sin(i * 1.7)),
    };

    [Theory]
    [InlineData(50)]
    [InlineData(200)]
    [InlineData(1000)]
    [InlineData(5000)]
    public void Medir(int total)
    {
        int perParam = total / Names.Length;
        (UndoStack stack, Project project, string fx) = Setup(perParam + 5);
        long mem0 = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < perParam; i++)
        {
            foreach (string name in Names)
            {
                stack.Execute(Edits.SetKeyframe(SequenceId, "titulo", fx, name, S(i), Wobble(i, name), Interpolation.EaseInOut));
            }
        }

        double addMs = sw.Elapsed.TotalMilliseconds;
        long mem = GC.GetTotalMemory(true) - mem0;
        sw.Restart();
        int steps = 0;
        while (stack.CanUndo && steps < 200) { stack.Undo(); steps++; }
        while (stack.CanRedo && steps < 400) { stack.Redo(); steps++; }
        double undoMs = sw.Elapsed.TotalMilliseconds / Math.Max(1, steps);

        EffectInstance effect = Seq(project).VideoTracks[0].Clips[^1].Effects[0];
        sw.Restart();
        double acc = 0;
        const int Evals = 20000;
        for (int n = 0; n < Evals; n++)
        {
            Time t = S((n % (perParam * 100)) / 100.0);
            foreach (string name in Names)
            {
                acc += effect.Parameters[name].Evaluate(t);
            }
        }

        double evalUs = sw.Elapsed.TotalMilliseconds * 1000 / (Evals * Names.Length);
        sw.Restart();
        string json = ProjectSerializer.Serialize(project);
        double saveMs = sw.Elapsed.TotalMilliseconds;
        // Cotas anchas (CI en Debug): antes de buscar por bisección, evaluar con 5000 keyframes costaba ~3,7 us en este equipo y crecía con N.
        Assert.True(evalUs < 25, $"evaluar: {evalUs} us");
        Assert.True(addMs / total < 2, $"añadir: {addMs / total} ms por keyframe");
        output.WriteLine($"N={total}: añadir total {addMs:F0} ms ({addMs / total:F3} ms/keyframe), deshacer/rehacer {undoMs:F3} ms/paso, evaluar {evalUs:F3} us, guardar {saveMs:F1} ms ({json.Length / 1024} KB), memoria {mem / 1024 / 1024} MB ({acc:F0})");
    }

    [Fact]
    public void La_busqueda_por_biseccion_coincide_con_la_lineal()
    {
        var p = new Parameter();
        for (int i = 0; i < 300; i++)
        {
            p.Keyframes.Add(new Keyframe(Time.FromTicks(i * 7L), i % 5, Interpolation.EaseInOut));
            if (i % 40 == 0)
            {
                p.Keyframes.Add(new Keyframe(Time.FromTicks(i * 7L), 9)); // duplicado: la búsqueda lineal daba el primero
            }
        }

        for (long t = -3; t < 2200; t++)
        {
            Time time = Time.FromTicks(t);
            Assert.Equal(p.Keyframes.FindIndex(k => k.Time > time) is var a and >= 0 ? a : p.Keyframes.Count, Parameter.FirstAfter(p.Keyframes, time));
            Assert.Equal(p.Keyframes.FindIndex(k => k.Time == time), Parameter.IndexAt(p.Keyframes, time));
        }
    }
}
