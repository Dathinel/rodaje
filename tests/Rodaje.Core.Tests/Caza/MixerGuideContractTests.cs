using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;

namespace Rodaje.Core.Tests.Caza;

/// <summary>Las promesas de la sección «Mezclador» de <c>docs/guia/audio.md</c> que se comprueban en el núcleo.</summary>
public class MixerGuideContractTests
{
    private const string Seq = "seq-1";

    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static Project Fixture() => new()
    {
        Id = "p",
        Media = [new MediaRef { Id = "m", Path = "m.wav", Duration = S(60), HasAudio = true, SampleRate = 48000, AudioChannels = 2 }],
        Sequences =
        [
            new Sequence
            {
                Id = Seq,
                AudioTracks =
                [
                    new Track { Id = "voz", Kind = TrackKind.Audio, Name = "Voz", Clips = [new Clip { Id = "c1", MediaId = "m", SourceOut = S(10) }] },
                    new Track { Id = "musica", Kind = TrackKind.Audio, Name = "Música" },
                ],
            },
        ],
    };

    private static Parameter Fader(Track track, string parameter) =>
        track.Effects.Single(e => e.Type == BuiltInEffects.Audio).Parameters[parameter];

    // «volumen de −∞ a +12 dB»
    [Fact]
    public void Fader_volume_goes_from_silence_up_to_plus_12_dB()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        var ids = new SequentialIdGenerator();
        Track voice = project.Sequences[0].GetTrack("voz");

        stack.Execute(Edits.SetFader(Seq, "voz", A.GainDb, 12, Time.Zero, ids));
        Assert.Equal(12, Fader(voice, A.GainDb).Value);

        stack.Execute(Edits.SetFader(Seq, "voz", A.GainDb, -500, Time.Zero, ids));
        Assert.Equal(EffectRegistry.SilenceDb, Fader(voice, A.GainDb).Value);
    }

    // «doble clic vuelve a 0 dB» / «Doble clic: al centro»
    [Fact]
    public void Fader_pan_and_balance_rest_at_zero()
    {
        EffectDefinition audio = EffectRegistry.Find(BuiltInEffects.Audio)!;
        Assert.Equal(0, audio.FindParameter(A.GainDb)!.DefaultValue);
        Assert.Equal(0, audio.FindParameter(A.Pan)!.DefaultValue);
        Assert.Equal(0, audio.FindParameter(A.Balance)!.DefaultValue);

        Project project = Fixture();
        var stack = new UndoStack(project);
        var ids = new SequentialIdGenerator();
        stack.Execute(Edits.SetFader(Seq, "voz", A.Pan, -40, Time.Zero, ids));
        stack.Execute(Edits.SetFader(Seq, "voz", A.Pan, 0, Time.Zero, ids));
        Assert.Equal(0, Fader(project.Sequences[0].GetTrack("voz"), A.Pan).Value);
    }

    // «Es el mismo volumen que el de la cabecera de la pista.»
    [Fact]
    public void Fader_volume_is_the_tracks_rodaje_audio_gain()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        Track voice = project.Sequences[0].GetTrack("voz");
        voice.Effects.Add(new EffectInstance
        {
            Id = "vol",
            Type = BuiltInEffects.Audio,
            Parameters = { [A.GainDb] = new Parameter { Value = -6 } },
        });

        stack.Execute(Edits.SetFader(Seq, "voz", A.GainDb, -9, Time.Zero, new SequentialIdGenerator()));
        EffectInstance only = Assert.Single(voice.Effects);
        Assert.Equal("vol", only.Id);
        Assert.Equal(-9, only.Parameters[A.GainDb].Value);
    }

    // «M, S, R y T: silenciar, solo (Ctrl+clic: solo exclusivo), armar para grabar»
    [Fact]
    public void Solo_arm_and_exclusive_solo()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        Sequence sequence = project.Sequences[0];
        stack.Execute(Edits.SetTrackMix(Seq, "voz", solo: true));
        stack.Execute(Edits.SetTrackMix(Seq, "musica", solo: true, armed: true));
        Assert.Equal([true, true], sequence.AudioTracks.Select(t => t.Solo));
        Assert.True(sequence.GetTrack("musica").Armed);

        stack.Execute(Edits.SoloExclusive(Seq, "voz"));
        Assert.Equal([true, false], sequence.AudioTracks.Select(t => t.Solo));
    }

    // «arriba eliges adónde sale la pista (la mezcla o un bus)»
    [Fact]
    public void Output_goes_to_a_bus_or_back_to_the_mix()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        SequenceEditCommand add = Edits.AddBus(Seq, "Voces", ids: new SequentialIdGenerator());
        stack.Execute(add);
        Track voice = project.Sequences[0].GetTrack("voz");

        stack.Execute(Edits.SetTrackOutput(Seq, "voz", add.CreatedIds[0]));
        Assert.Equal(add.CreatedIds[0], voice.Output);
        stack.Execute(Edits.SetTrackOutput(Seq, "voz", null));
        Assert.Null(voice.Output);
    }

    // «El + de «Envíos» manda además una copia a un bus (por ejemplo una reverb), con su nivel y Pre»
    // «Clic derecho en un envío para quitarlo.»
    [Fact]
    public void A_send_is_an_extra_copy_with_its_level_and_pre_and_can_be_removed()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        var ids = new SequentialIdGenerator();
        SequenceEditCommand add = Edits.AddBuses(Seq, ["Voces", "Reverb"], ids);
        stack.Execute(add);
        string voices = add.CreatedIds[0], reverb = add.CreatedIds[1];
        Track voice = project.Sequences[0].GetTrack("voz");
        stack.Execute(Edits.SetTrackOutput(Seq, "voz", voices));

        stack.Execute(Edits.SetSend(Seq, "voz", reverb, ids: ids));
        AudioSend send = Assert.Single(voice.Sends);
        Assert.Equal((reverb, 0.0, false), (send.BusId, send.GainDb, send.PreFader));
        Assert.Equal(voices, voice.Output);

        stack.Execute(Edits.SetSend(Seq, "voz", reverb, gainDb: -18, preFader: true, ids: ids));
        Assert.Equal((-18.0, true), (send.GainDb, send.PreFader));

        stack.Execute(Edits.RemoveSend(Seq, "voz", reverb));
        Assert.Empty(voice.Sends);
        Assert.Equal(voices, voice.Output);
    }

    // «Añadir bus › Voces, Música y Efectos crea las tres submezclas de una vez.»
    [Fact]
    public void The_three_standard_buses_are_created_in_one_step()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        stack.Execute(Edits.AddBuses(Seq, ["Voces", "Música", "Efectos"], new SequentialIdGenerator()));
        Assert.Equal(["Voces", "Música", "Efectos"], project.Sequences[0].AudioBuses.Select(b => b.Name));

        stack.Undo();
        Assert.Empty(project.Sequences[0].AudioBuses);
    }

    // «Clic derecho en su nombre: renombrar o quitar (las pistas que salían a él vuelven a la mezcla).»
    [Fact]
    public void Renaming_and_removing_a_bus_sends_its_tracks_back_to_the_mix()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        SequenceEditCommand add = Edits.AddBus(Seq, "Voces", ids: new SequentialIdGenerator());
        stack.Execute(add);
        string bus = add.CreatedIds[0];
        stack.Execute(Edits.SetTrackOutput(Seq, "voz", bus));
        stack.Execute(Edits.SetTrackOutput(Seq, "musica", bus));

        stack.Execute(Edits.SetBusState(Seq, bus, name: "Diálogo"));
        Assert.Equal("Diálogo", project.Sequences[0].FindBus(bus)!.Name);

        stack.Execute(Edits.RemoveBus(Seq, bus));
        Assert.Empty(project.Sequences[0].AudioBuses);
        Assert.All(project.Sequences[0].AudioTracks, t => Assert.Null(t.Output));
    }

    // «El volumen y el paneo de una pista o un bus admiten keyframes (en el tiempo de la secuencia).»
    [Fact]
    public void Track_and_bus_volume_and_pan_take_keyframes_in_sequence_time()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        var ids = new SequentialIdGenerator();
        SequenceEditCommand add = Edits.AddBus(Seq, "Voces", ids: ids);
        stack.Execute(add);
        string bus = add.CreatedIds[0];

        foreach (string owner in new[] { "voz", bus })
        {
            foreach (string parameter in new[] { A.GainDb, A.Pan })
            {
                stack.Execute(Edits.WriteAutomation(Seq, owner, parameter, [new(S(3), -20)], S(2), S(4), ids));
                Sequence sequence = project.Sequences[0];
                List<EffectInstance> effects = sequence.FindBus(owner)?.Effects ?? sequence.GetTrack(owner).Effects;
                Parameter target = effects.Single(e => e.Type == BuiltInEffects.Audio).Parameters[parameter];
                Assert.Equal(-20, target.Evaluate(S(3)));
                Assert.Equal(0, target.Evaluate(S(5)));
            }
        }
    }

    // «lo que hagas queda en keyframes [...] y al soltar la curva vuelve a la de antes en 0,1 s. Todo el pase es un paso del historial.»
    [Fact]
    public void A_touch_pass_returns_to_the_previous_curve_and_undoes_in_one_step()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        var ids = new SequentialIdGenerator();
        stack.Execute(Edits.SetFader(Seq, "voz", A.GainDb, -3, Time.Zero, ids));
        Parameter gain = Fader(project.Sequences[0].GetTrack("voz"), A.GainDb);

        stack.Execute(Edits.WriteAutomation(Seq, "voz", A.GainDb, [new(S(2.5), -10), new(S(3), -14)], S(2), S(3.1), ids));
        Assert.Equal(-14, gain.Evaluate(S(3)));
        Assert.Equal(-3, gain.Evaluate(S(3.1)));
        Assert.Equal(-3, gain.Evaluate(S(8)));

        stack.Undo();
        Assert.Empty(Fader(project.Sequences[0].GetTrack("voz"), A.GainDb).Keyframes);
        Assert.Equal(-3, Fader(project.Sequences[0].GetTrack("voz"), A.GainDb).Value);
    }

    // «al soltar la curva vuelve a la de antes»: también si la curva de antes mantenía su valor (Mantener) hasta el keyframe siguiente.
    [Fact]
    public void A_touch_pass_keeps_a_held_curve_held_after_the_return()
    {
        Project project = Fixture();
        Track voice = project.Sequences[0].GetTrack("voz");
        voice.Effects.Add(new EffectInstance
        {
            Id = "vol",
            Type = BuiltInEffects.Audio,
            Parameters =
            {
                [A.GainDb] = new Parameter { Keyframes = [new(Time.Zero, -6, Interpolation.Hold), new(S(10), 0, Interpolation.Linear)] },
            },
        });
        var stack = new UndoStack(project);
        Parameter gain = Fader(voice, A.GainDb);
        Assert.Equal(-6, gain.Evaluate(S(6)));

        stack.Execute(Edits.WriteAutomation(Seq, "voz", A.GainDb, [new(S(2.5), -12)], S(2), S(3), new SequentialIdGenerator()));
        Assert.Equal(-12, gain.Evaluate(S(2.5)));
        Assert.Equal(-6, gain.Evaluate(S(3)));
        Assert.Equal(-6, gain.Evaluate(S(6)));
        Assert.Equal(-6, gain.Evaluate(S(9.9)));
        Assert.Equal(0, gain.Evaluate(S(10)));
    }
}
