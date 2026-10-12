using CsCheck;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Xunit.Abstractions;
using static Rodaje.Core.Tests.Properties.V1Support;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;

namespace Rodaje.Core.Tests.Properties;

/// <summary>
/// Ganancia maestra (<see cref="Sequence.MasterGainDb"/>) y normalización por clip (<see cref="Edits.ShiftGain"/>): límites,
/// redondeo, guardar y abrir, forma de la envolvente, ida y vuelta y deshacer.
/// </summary>
/// <remarks>Para reproducir un fallo, pasar la semilla que reporta CsCheck en <c>seed</c> o en <c>CsCheck_Seed</c>.</remarks>
public class AudioGainPropertiesTests(ITestOutputHelper output)
{
    private const string Seq = BuiltProject.SequenceId;

    private const double Min = Sequence.MinMasterGainDb;

    private const double Max = Sequence.MaxMasterGainDb;

    /// <summary>Ganancias pedidas: casi siempre razonables, a veces enormes, diminutas o no finitas.</summary>
    private static readonly Gen<double> GenGain = Gen.Frequency(
        (10, Gen.Double[-80, 40]),
        (2, Gen.Double),
        (1, Gen.OneOfConst(double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.0, 1e-300, -60.004, -59.995, 24.004, 23.995, 0.005, -0.005)));

    /// <summary>
    /// Fijar la ganancia maestra deja el valor pedido acotado de −60 a +24 dB y redondeado a centésimas, el proyecto es
    /// válido, guardar y abrir la conserva exacta y deshacer vuelve byte a byte; un valor no finito se rechaza sin cambiar nada.
    /// </summary>
    [Fact]
    public void GananciaMaestra_AcotadaRedondeadaYEstable() =>
        Gen.Select(RandomTimeline.GenProject, GenGain.Array[1, 4]).Sample(
            s =>
            {
                BuiltProject built = RandomTimeline.Build(s.Item1);
                Project project = built.Project;
                var stack = new UndoStack(project);
                foreach (double gain in s.Item2)
                {
                    string before = ProjectSerializer.Serialize(project);
                    try
                    {
                        stack.Execute(Edits.SetMasterGain(Seq, gain, "Normalizar la mezcla", built.Ids));
                    }
                    catch (EditException)
                    {
                        Assert.False(double.IsFinite(gain), Inv($"Rechazó {gain}"));
                        Assert.Equal(before, ProjectSerializer.Serialize(project));
                        continue;
                    }

                    Assert.True(double.IsFinite(gain));
                    double value = built.Sequence.MasterGainDb;
                    Assert.Equal(Math.Round(Math.Clamp(gain, Min, Max), 2), value);
                    Assert.InRange(value, Min, Max);
                    Assert.Empty(ModelValidator.Validate(project));
                    string saved = ProjectSerializer.Serialize(project);
                    Project reopened = ProjectSerializer.Deserialize(saved);
                    Assert.Equal(value, reopened.GetSequence(Seq).MasterGainDb);
                    Assert.Equal(saved, ProjectSerializer.Serialize(reopened));
                    stack.Undo();
                    Assert.Equal(before, ProjectSerializer.Serialize(project));
                    stack.Redo();
                }
            },
            writeLine: output.WriteLine,
            iter: 800,
            print: s => Inv($"{s.Item1}\nganancias [{string.Join(", ", s.Item2)}]"));

    /// <summary>Una ganancia maestra escrita a mano fuera de rango o no finita no pasa el validador; dentro del rango, sí.</summary>
    [Fact]
    public void GananciaMaestra_ElValidadorRechazaLoDeFuera() =>
        GenGain.Sample(
            gain =>
            {
                var project = new Project { Id = "p", Sequences = [new Sequence { Id = "s", MasterGainDb = gain }] };
                bool valid = double.IsFinite(gain) && gain >= Min && gain <= Max;
                Assert.Equal(valid, ModelValidator.Validate(project).Count == 0);
            },
            writeLine: output.WriteLine,
            iter: 20000);

    /// <summary>Cuánto subir o bajar cada dueño (clip o pista de audio) y qué keyframes de volumen ponerle antes.</summary>
    internal sealed record ShiftSpec(int[] Owners, double[] Deltas, int[] KeyAt, double[] KeyValues)
    {
        public override string ToString() => Inv($"dueños [{string.Join(",", Owners)}] deltas [{string.Join(",", Deltas)}] keyframes en [{string.Join(",", KeyAt)}] valores [{string.Join(",", KeyValues)}]");
    }

    private static readonly Gen<ShiftSpec> GenShift = Gen.Int[1, 4].SelectMany(n => Gen.Int[0, 6].SelectMany(k => Gen.Select(
        Gen.Int[0, 100].Array[n], Gen.Frequency((8, Gen.Double[-20, 20]), (1, Gen.Double[-90, 90])).Array[n], Gen.Int[0, 999].Array[k], Gen.Double[-70, 30].Array[k],
        (o, d, at, v) => new ShiftSpec(o, d, at, v))));

    /// <summary>
    /// Normalizar (subir o bajar el volumen de varios clips o pistas de audio en un paso) suma lo mismo al valor y a cada
    /// keyframe, acotado de −60 a +24 dB, sin tocar instantes, interpolaciones ni los demás parámetros; la envolvente conserva
    /// su forma si nada se acota; lo que no es dueño no cambia; deshacer vuelve byte a byte, y bajar lo mismo deshace la subida.
    /// </summary>
    [Fact]
    public void NormalizarClips_SumaLoMismoYConservaLaForma()
    {
        int applied = 0, total = 0;
        Gen.Select(RandomTimeline.GenProject, GenShift).Sample(
            s =>
            {
                total++;
                BuiltProject built = RandomTimeline.Build(s.Item1 with { LockBits = s.Item1.LockBits | 0x0554 });
                Project project = built.Project;
                Sequence sequence = built.Sequence;
                List<string> candidates = [.. sequence.AudioTracks.SelectMany(t => t.Clips.Select(c => c.Id).Prepend(t.Id))];
                if (candidates.Count == 0)
                {
                    return;
                }

                ShiftSpec spec = s.Item2;
                var stack = new UndoStack(project);
                AddKeys(stack, built, candidates[spec.Owners[0] % candidates.Count], spec);
                var deltas = new Dictionary<string, double>(StringComparer.Ordinal);
                for (int i = 0; i < spec.Owners.Length; i++)
                {
                    deltas[candidates[spec.Owners[i] % candidates.Count]] = spec.Deltas[i];
                }

                Project old = project.Clone();
                string before = ProjectSerializer.Serialize(project);
                try
                {
                    stack.Execute(Edits.ShiftGain(Seq, deltas, "Normalizar", built.Ids));
                }
                catch (EditException)
                {
                    Assert.Equal(before, ProjectSerializer.Serialize(project));
                    return;
                }

                applied++;
                Assert.Empty(ModelValidator.Validate(project));
                Sequence previous = old.GetSequence(Seq);
                foreach (Track track in sequence.AllTracks)
                {
                    Track was = previous.GetTrack(track.Id);
                    CheckOwner(was.Effects, track.Effects, deltas.GetValueOrDefault(track.Id, double.NaN));
                    foreach (Clip clip in track.Clips)
                    {
                        CheckOwner(was.Clips.Single(c => c.Id == clip.Id).Effects, clip.Effects, deltas.GetValueOrDefault(clip.Id, double.NaN));
                    }
                }

                stack.Undo();
                Assert.Equal(before, ProjectSerializer.Serialize(project));

                // Subir y bajar lo mismo, si nada llega a los topes, deja los mismos números.
                stack.Redo();
                if (!Clamped(previous, deltas))
                {
                    stack.Execute(Edits.ShiftGain(Seq, deltas.ToDictionary(p => p.Key, p => -p.Value, StringComparer.Ordinal), "Normalizar", built.Ids));
                    foreach ((string owner, _) in deltas)
                    {
                        Parameter? a = Gain(Owner(previous, owner)), b = Gain(Owner(sequence, owner));
                        Assert.NotNull(b);
                        Assert.True(Math.Abs((a?.Value ?? 0) - b.Value) < 1e-9);
                        Assert.Equal(a?.Keyframes.Count ?? 0, b.Keyframes.Count);
                        for (int i = 0; i < b.Keyframes.Count; i++)
                        {
                            Assert.True(Math.Abs(a!.Keyframes[i].Value - b.Keyframes[i].Value) < 1e-9);
                        }
                    }
                }
            },
            writeLine: output.WriteLine,
            iter: 2000,
            print: s => Inv($"{s.Item1}\n{s.Item2}"));
        output.WriteLine($"{applied} de {total} normalizaciones aplicadas");
        Assert.True(applied * 3 >= total, $"Pocas normalizaciones: {applied} de {total}");
    }

    /// <summary>Keyframes de volumen en el primer dueño, en instantes de su origen (o de la secuencia, si es una pista).</summary>
    private static void AddKeys(UndoStack stack, BuiltProject built, string owner, ShiftSpec spec)
    {
        if (spec.KeyAt.Length == 0)
        {
            return;
        }

        Clip? clip = built.Sequence.FindClip(owner)?.Clip;
        var add = Edits.AddEffect(Seq, owner, BuiltInEffects.Audio, ids: built.Ids);
        try
        {
            stack.Execute(add);
        }
        catch (EditException)
        {
            return;
        }

        for (int i = 0; i < spec.KeyAt.Length; i++)
        {
            Time at = clip is null
                ? Time.FromTicks(spec.KeyAt[i] * 7_056_000L)
                : clip.SourceIn + Time.FromTicks((clip.SourceOut - clip.SourceIn).Ticks * spec.KeyAt[i] / 1000);
            try
            {
                stack.Execute(Edits.SetKeyframe(Seq, owner, add.CreatedIds[0], A.GainDb, at, spec.KeyValues[i], (Interpolation)(i % 5), built.Ids));
            }
            catch (EditException)
            {
                // Fuera del clip congelado u otro instante no editable: ese keyframe no se pone.
            }
        }
    }

    private static List<EffectInstance> Owner(Sequence sequence, string id) =>
        sequence.FindClip(id)?.Clip.Effects ?? sequence.GetTrack(id).Effects;

    private static Parameter? Gain(List<EffectInstance> effects) =>
        effects.Find(e => e.Type == BuiltInEffects.Audio)?.Parameters.GetValueOrDefault(A.GainDb);

    private static bool Clamped(Sequence previous, Dictionary<string, double> deltas) => deltas.Any(p =>
    {
        Parameter? gain = Gain(Owner(previous, p.Key));
        IEnumerable<double> values = gain is null ? [0] : gain.Keyframes.Select(k => k.Value).Append(gain.Value);
        return values.Any(v => v + p.Value < Min + 1e-6 || v + p.Value > Max - 1e-6);
    });

    /// <summary>
    /// Un dueño con <paramref name="delta"/> (NaN si no lo es): su primer efecto de audio suma el delta acotado al valor y a
    /// cada keyframe, sin tocar el resto; en un no dueño los efectos quedan iguales.
    /// </summary>
    private static void CheckOwner(List<EffectInstance> before, List<EffectInstance> after, double delta)
    {
        string Text(IEnumerable<EffectInstance> effects) => string.Join("|", effects.Select(e => Inv(
            $"{e.Id} {e.Type} {e.Enabled} {string.Join(",", e.Options.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => o.Key + "=" + o.Value))} {string.Join(",", e.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + p.Value.Value + "@" + string.Join(";", p.Value.Keyframes)))}")));
        if (double.IsNaN(delta))
        {
            Assert.Equal(Text(before), Text(after));
            return;
        }

        EffectInstance? old = before.Find(e => e.Type == BuiltInEffects.Audio);
        EffectInstance now = Assert.Single(after, e => e.Type == BuiltInEffects.Audio && (old is null || e.Id == old.Id));
        Assert.True(now.Enabled);
        Assert.Equal(Text(before.Where(e => e != old && e.Id != now.Id)), Text(after.Where(e => e != now)));
        Parameter? was = old?.Parameters.GetValueOrDefault(A.GainDb);
        Parameter gain = now.Parameters[A.GainDb];
        Assert.Equal(Math.Clamp((was?.Value ?? 0) + delta, Min, Max), gain.Value, 9);
        Assert.Equal(was?.Keyframes.Count ?? 0, gain.Keyframes.Count);
        for (int i = 0; i < gain.Keyframes.Count; i++)
        {
            Keyframe a = was!.Keyframes[i], b = gain.Keyframes[i];
            Assert.Equal((a.Time, a.Interpolation), (b.Time, b.Interpolation));
            Assert.Equal(Math.Clamp(a.Value + delta, Min, Max), b.Value, 9);
            if (i > 0 && a.Value + delta is > Min and < Max && was.Keyframes[i - 1].Value + delta is > Min and < Max)
            {
                // La forma: la diferencia con el keyframe anterior no cambia.
                Assert.Equal(a.Value - was.Keyframes[i - 1].Value, b.Value - gain.Keyframes[i - 1].Value, 9);
            }
        }

        foreach ((string name, Parameter p) in old?.Parameters ?? [])
        {
            if (name != A.GainDb)
            {
                Assert.Equal(p.Value, now.Parameters[name].Value);
            }
        }
    }
}
