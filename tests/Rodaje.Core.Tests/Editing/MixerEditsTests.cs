using System.Text.Json.Nodes;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;

namespace Rodaje.Core.Tests.Editing;

/// <summary>Buses, envíos, salida, solo, fader y escritura de automatización: deshacer, validación y formato 7.</summary>
public class MixerEditsTests
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

    [Fact]
    public void Buses_sends_and_outputs_undo_in_one_step_and_survive_a_round_trip()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        var ids = new SequentialIdGenerator();
        SequenceEditCommand add = Edits.AddBuses(Seq, ["Voces", "Música", "Efectos"], ids);
        stack.Execute(add);
        string voices = add.CreatedIds[0], effects = add.CreatedIds[2];
        stack.Execute(Edits.SetTrackOutput(Seq, "voz", voices));
        stack.Execute(Edits.SetSend(Seq, "voz", effects, gainDb: -12, ids: ids));
        stack.Execute(Edits.SetSend(Seq, "voz", effects, preFader: true, ids: ids));
        stack.Execute(Edits.SetTrackMix(Seq, "musica", solo: true));
        stack.Execute(Edits.SetFader(Seq, voices, A.Pan, -30, Time.Zero, ids));
        Sequence sequence = project.Sequences[0];
        Assert.Equal(3, sequence.AudioBuses.Count);
        Assert.Equal(voices, sequence.GetTrack("voz").Output);
        AudioSend send = Assert.Single(sequence.GetTrack("voz").Sends);
        Assert.Equal((-12.0, true), (send.GainDb, send.PreFader));
        Assert.Empty(ModelValidator.Validate(project));

        Project reopened = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        Assert.Equal(ProjectSerializer.Serialize(project), ProjectSerializer.Serialize(reopened));
        Assert.Equal(-30, reopened.Sequences[0].GetTrack("voz").Output is { } output
            ? reopened.Sequences[0].FindBus(output)!.Effects.Single().Parameters[A.Pan].Value
            : double.NaN);

        // Quitar el bus devuelve la pista a la mezcla en el mismo paso; deshacer lo deja como estaba.
        stack.Execute(Edits.RemoveBus(Seq, voices));
        Assert.Null(sequence.GetTrack("voz").Output);
        stack.Undo();
        Assert.Equal(voices, sequence.GetTrack("voz").Output);
        for (int i = 0; i < 5; i++)
        {
            stack.Undo();
        }

        Assert.Equal(3, sequence.AudioBuses.Count);
        Assert.Null(sequence.GetTrack("voz").Output);
        stack.Undo();
        Assert.Empty(sequence.AudioBuses);
    }

    [Fact]
    public void Validator_rejects_dangling_outputs_duplicate_sends_and_mixer_fields_on_video()
    {
        Project project = Fixture();
        Sequence sequence = project.Sequences[0];
        sequence.AudioBuses.Add(new AudioBus { Id = "bus", Name = "Voces" });
        sequence.GetTrack("voz").Output = "nada";
        sequence.GetTrack("voz").Sends = [new AudioSend { BusId = "bus" }, new AudioSend { BusId = "bus" }];
        sequence.GetTrack("musica").Sends = [new AudioSend { BusId = "bus", GainDb = 40 }];
        sequence.VideoTracks.Add(new Track { Id = "v", Kind = TrackKind.Video, Name = "V1", Solo = true });
        IReadOnlyList<string> errors = ModelValidator.Validate(project);
        Assert.Contains(errors, e => e.Contains("sale a un bus que no existe", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("dos envíos", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("fuera de rango", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("pista de video", StringComparison.Ordinal));
    }

    [Fact]
    public void Exclusive_solo_leaves_only_one_and_a_second_click_clears_them()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        stack.Execute(Edits.SetTrackMix(Seq, "voz", solo: true));
        stack.Execute(Edits.SoloExclusive(Seq, "musica"));
        Sequence sequence = project.Sequences[0];
        Assert.Equal([false, true], sequence.AudioTracks.Select(t => t.Solo));
        stack.Execute(Edits.SoloExclusive(Seq, "musica"));
        Assert.All(sequence.AudioTracks, t => Assert.False(t.Solo));

        // El solo y el armado valen en una pista bloqueada (como el silencio); el fader no.
        sequence.GetTrack("voz").Locked = true;
        stack.Execute(Edits.SetTrackMix(Seq, "voz", solo: true, armed: true));
        Assert.True(sequence.GetTrack("voz").Armed);
        Assert.Throws<EditException>(() => stack.Execute(Edits.SetFader(Seq, "voz", A.GainDb, -6, Time.Zero)));
    }

    [Fact]
    public void Touch_automation_replaces_the_touched_span_and_keeps_the_curve_outside()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        var ids = new SequentialIdGenerator();
        stack.Execute(Edits.SetFader(Seq, "voz", A.GainDb, -3, Time.Zero, ids));
        stack.Execute(Edits.WriteAutomation(Seq, "voz", A.GainDb,
            [new(S(2.5), -10), new(S(3), -12), new(S(3.5), -11)], S(2), S(4), ids));
        Parameter gain = project.Sequences[0].GetTrack("voz").Effects.Single().Parameters[A.GainDb];
        Assert.Equal([S(2), S(2.5), S(3), S(3.5), S(4)], gain.Keyframes.Select(k => k.Time));
        Assert.Equal(-3, gain.Evaluate(S(1)));
        Assert.Equal(-3, gain.Evaluate(S(2)));
        Assert.Equal(-12, gain.Evaluate(S(3)));
        Assert.Equal(-3, gain.Evaluate(S(4)));
        Assert.Equal(-3, gain.Evaluate(S(9)));

        // Una segunda pasada sobre parte del tramo cambia solo esa parte.
        stack.Execute(Edits.WriteAutomation(Seq, "voz", A.GainDb, [new(S(3.2), -20)], S(3.1), S(3.3), ids));
        Assert.Equal(-12, gain.Evaluate(S(3)), 6);
        Assert.Equal(-20, gain.Evaluate(S(3.2)));
        Assert.Equal(-11, gain.Evaluate(S(3.5)));
        stack.Undo();
        stack.Undo();
        Assert.Empty(project.Sequences[0].GetTrack("voz").Effects.Single().Parameters[A.GainDb].Keyframes);
    }

    [Fact]
    public void Migration_6_to_7_keeps_the_old_sound_order()
    {
        const string v6 = """
            {"schemaVersion": 6, "id": "p", "media": [], "sequences": [{"id": "s", "audioTracks": [{"id": "t", "kind": "audio", "name": "A",
              "clips": [], "transitions": [], "effects": [
                {"id": "c1", "type": "rodaje.compressor", "enabled": true, "parameters": {}, "options": {}},
                {"id": "e1", "type": "rodaje.equalizer", "enabled": true, "parameters": {}, "options": {}},
                {"id": "e0", "type": "rodaje.equalizer", "enabled": false, "parameters": {}, "options": {}},
                {"id": "c2", "type": "rodaje.compressor", "enabled": true, "parameters": {}, "options": {}},
                {"id": "e2", "type": "rodaje.equalizer", "parameters": {}, "options": {}}]}],
              "masterEffects": [{"id": "m1", "type": "rodaje.equalizer", "enabled": true, "parameters": {}, "options": {}}]}]}
            """;
        JsonObject root = JsonNode.Parse(v6)!.AsObject();
        Assert.Equal(6, ProjectMigrator.Default.Migrate(root));
        Project project = ProjectSerializer.Deserialize(v6);
        List<EffectInstance> effects = project.Sequences[0].AudioTracks[0].Effects;
        Assert.Equal(["e1", "c1", "e0", "c2", "e2"], effects.Select(e => e.Id));
        Assert.Equal([true, true, false, false, false], effects.Select(e => e.Enabled));
        Assert.Single(project.Sequences[0].MasterEffects, e => e.Enabled);
    }
}
