using Rodaje.App.Services.Audio;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;

namespace Rodaje.App.Tests.Audio;

/// <summary>La lógica del mezclador sin interfaz: tiras, curva del fader, reordenar inserciones y la escritura del modo «tocar».</summary>
public sealed class MixerModelTests
{
    private static Project Fixture() => new()
    {
        Id = "p",
        Sequences =
        [
            new Sequence
            {
                Id = "s",
                AudioTracks =
                [
                    new Track
                    {
                        Id = "voz",
                        Kind = TrackKind.Audio,
                        Name = "Voz",
                        Solo = true,
                        Output = "b",
                        Effects =
                        [
                            new EffectInstance { Id = "eq", Type = BuiltInEffects.Equalizer },
                            new EffectInstance { Id = "vol", Type = BuiltInEffects.Audio, Parameters = { [A.GainDb] = new Parameter { Keyframes = [new(Time.Zero, -10), new(Time.FromSeconds(2), 0)] } } },
                            new EffectInstance { Id = "comp", Type = BuiltInEffects.Compressor },
                            new EffectInstance { Id = "rev", Type = BuiltInEffects.Reverb },
                        ],
                    },
                ],
                AudioBuses = [new AudioBus { Id = "b", Name = "Voces" }],
                MasterGainDb = -2,
            },
        ],
    };

    [Fact]
    public void Strips_list_tracks_buses_and_the_mix_with_the_fader_at_the_cursor()
    {
        IReadOnlyList<MixerStrip> strips = MixerModel.Strips(Fixture().Sequences[0], Time.FromSeconds(1));
        Assert.Equal([StripKind.Track, StripKind.Bus, StripKind.Master], strips.Select(s => s.Kind));
        MixerStrip voice = strips[0];
        Assert.Equal(-5, voice.GainDb, 6);
        Assert.True(voice.GainAutomated);
        Assert.True(voice.Solo);
        Assert.Equal("b", voice.Output);
        Assert.Equal(["eq", "comp", "rev"], voice.Inserts.Select(e => e.Id));
        Assert.Equal(-2, strips[2].GainDb);
    }

    [Fact]
    public void Fader_taper_is_monotonic_and_round_trips()
    {
        double previous = -1;
        for (double db = -60; db <= 12; db += 0.5)
        {
            double position = MixerModel.FaderPosition(db);
            Assert.True(position > previous);
            Assert.Equal(db, MixerModel.FaderDb(position), 9);
            previous = position;
        }

        Assert.Equal(0.78, MixerModel.FaderPosition(0), 9);
        Assert.Equal("−∞", MixerModel.FormatDb(-60));
        Assert.Equal("−3,5", MixerModel.FormatDb(-3.5));
        Assert.Equal("I 30", MixerModel.FormatPan(-30));
        Assert.Equal("C", MixerModel.FormatPan(0.2));
    }

    [Fact]
    public void Moving_an_insert_lands_between_its_new_neighbours_in_the_full_stack()
    {
        List<EffectInstance> effects = Fixture().Sequences[0].AudioTracks[0].Effects;

        // Llevar la reverb al principio: va delante del ecualizador.
        Assert.Equal(0, MixerModel.MoveIndex(effects, "rev", ["rev", "eq", "comp"]));

        // Llevar el ecualizador al final: detrás de la reverb (el volumen queda donde estaba).
        int index = MixerModel.MoveIndex(effects, "eq", ["comp", "rev", "eq"]);
        var moved = effects.Where(e => e.Id != "eq").ToList();
        moved.Insert(index, effects[0]);
        Assert.Equal(["vol", "comp", "rev", "eq"], moved.Select(e => e.Id));
    }

    [Fact]
    public void Touch_writing_keeps_only_the_points_that_matter_and_returns_to_the_old_curve()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        var recorder = new TouchRecorder("voz", A.GainDb, Time.FromSeconds(0.5));
        for (int i = 1; i <= 30; i++)
        {
            // Una rampa recta de 0,5 s a 1 s y luego quieto: bastan los extremos.
            double t = 0.5 + (i * 0.025);
            recorder.Add(Time.FromSeconds(t), t <= 1 ? -10 + ((t - 0.5) * 20) : 0);
        }

        IReadOnlyList<Rodaje.Core.Editing.MixerEdits.AutomationPoint> thinned = recorder.Thinned();
        Assert.True(thinned.Count <= 4, $"{thinned.Count} puntos");
        stack.Execute(recorder.Command("s")!);
        Parameter gain = project.Sequences[0].AudioTracks[0].Effects[1].Parameters[A.GainDb];
        Assert.Equal(0, gain.Evaluate(Time.FromSeconds(1.2)), 6);
        Assert.Equal(-7.5, gain.Evaluate(Time.FromSeconds(0.5)), 6);
        Assert.Equal(-2.5, gain.Evaluate(Time.FromSeconds(1.5)), 6);
        Assert.Equal(0, gain.Evaluate(Time.FromSeconds(3)));
    }
}
