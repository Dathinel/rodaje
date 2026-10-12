using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Links;

/// <summary>«Separar audio»: el clip de video recibe su clip de audio enlazado en una pista de audio libre.</summary>
public sealed class DetachAudioTests
{
    [Fact]
    public void Detaching_creates_the_linked_audio_clip_on_the_first_free_audio_track()
    {
        var f = new TrimFixture().Add("v1", "v", "m1", 10, 60, 5);

        string id = TimelineEdits.DetachAudio(f.Ctx, "v");

        f.AssertValid();
        Clip audio = f.Clip(id);
        Assert.Same(f.Track("a1"), f.Sequence.GetClip(id).Track);
        f.AssertClip(id, 10, 60, 5);
        Assert.Equal("m1", audio.MediaId);
        Assert.NotNull(audio.LinkId);
        Assert.Equal(audio.LinkId, f.Clip("v").LinkId);
    }

    [Fact]
    public void Detaching_skips_busy_and_locked_tracks_and_adds_one_when_none_is_free()
    {
        var f = new TrimFixture().Add("v1", "v", "m1", 0, 50, 10).Add("a1", "x", "m3", 0, 20, 30).Lock("a2");

        string id = TimelineEdits.DetachAudio(f.Ctx, "v");

        f.AssertValid();
        Track added = f.Sequence.GetClip(id).Track;
        Assert.Equal(3, f.Sequence.AudioTracks.Count);
        Assert.Same(f.Sequence.AudioTracks[^1], added);
        Assert.Equal("Audio 3", added.Name);
    }

    [Fact]
    public void Detaching_on_a_chosen_track_needs_room_there()
    {
        var f = new TrimFixture().Add("v1", "v", "m1", 0, 50, 0).Add("a2", "x", "m3", 0, 20, 30);

        AssertFails(() => TimelineEdits.DetachAudio(f.Ctx, "v", "a2"), "x");
        AssertFails(() => TimelineEdits.DetachAudio(f.Ctx, "v", "v2"), "de video");
        string id = TimelineEdits.DetachAudio(f.Ctx, "v", "a1");
        Assert.Same(f.Track("a1"), f.Sequence.GetClip(id).Track);
    }

    [Fact]
    public void Audio_effects_move_to_the_new_clip_and_the_speed_is_shared()
    {
        var f = new TrimFixture().Add("v1", "v", "m2", 0, 100, 0);
        Clip video = f.Clip("v");
        video.Effects.Add(new EffectInstance { Id = "fx-t", Type = BuiltInEffects.Transform });
        video.Effects.Add(new EffectInstance { Id = "fx-a", Type = BuiltInEffects.Audio });
        TimelineEdits.SetSpeed(f.Ctx, "v", new Rational(2, 1));

        string id = TimelineEdits.DetachAudio(f.Ctx, "v");

        f.AssertValid();
        Clip audio = f.Clip(id);
        Assert.Equal(["fx-t"], video.Effects.Select(e => e.Id));
        Assert.Equal(["fx-a"], audio.Effects.Select(e => e.Id));
        Assert.Equal(video.Duration, audio.Duration);
        Assert.True(video.Speed!.SameTiming(audio.Speed!));
    }

    [Fact]
    public void A_clip_already_linked_to_other_video_keeps_its_link()
    {
        var f = new TrimFixture().Add("v1", "v", "m1", 0, 50, 0, link: "L").Add("v2", "w", "m2", 0, 50, 0, link: "L");

        string id = TimelineEdits.DetachAudio(f.Ctx, "v");

        f.AssertValid();
        Assert.Equal("L", f.Clip(id).LinkId);
        Assert.Equal(3, f.Sequence.GetLinkGroup("v").Count);
    }

    [Fact]
    public void Detaching_is_rejected_when_it_makes_no_sense()
    {
        var f = new TrimFixture().Add("v1", "v", "m1", 0, 50, 0, link: "L").Add("a1", "a", "m1", 0, 50, 0, link: "L")
            .Add("a2", "s", "m3", 0, 20, 60).AddTitle("v2", "t", 0, 25, 0);
        f.Project.Media.Add(new MediaRef { Id = "mudo", Name = "mudo", Duration = F(100), HasVideo = true });
        f.Track("v1").Clips.Add(new Clip { Id = "m", MediaId = "mudo", SourceOut = F(10), Start = F(80) });

        AssertFails(() => TimelineEdits.DetachAudio(f.Ctx, "v"), "ya está separado");
        AssertFails(() => TimelineEdits.DetachAudio(f.Ctx, "s"), "pista de audio");
        AssertFails(() => TimelineEdits.DetachAudio(f.Ctx, "t"), "título");
        AssertFails(() => TimelineEdits.DetachAudio(f.Ctx, "m"), "no tiene sonido");
        AssertFails(() => TimelineEdits.DetachAudio(f.Ctx, "falta"), "No existe");
    }

    [Fact]
    public void A_frozen_frame_has_no_sound_to_detach()
    {
        var f = new TrimFixture().Add("v1", "v", "m1", 0, 50, 0);
        string frozen = TimelineEdits.FreezeFrame(f.Ctx, "v", F(10));

        AssertFails(() => TimelineEdits.DetachAudio(f.Ctx, frozen), "congelado");
    }

    [Fact]
    public void A_locked_video_track_is_not_touched()
    {
        var f = new TrimFixture().Add("v1", "v", "m1", 0, 50, 0).Lock("v1");

        AssertFails(() => TimelineEdits.DetachAudio(f.Ctx, "v"), "bloqueada");
    }

    [Fact]
    public void The_command_undoes_and_redoes_in_one_step_with_the_same_ids()
    {
        var f = new TrimFixture().Add("v1", "v", "m1", 0, 50, 0);
        string original = ProjectSerializer.Serialize(f.Project);
        var stack = new UndoStack(f.Project);
        SequenceEditCommand command = Edits.DetachAudio("seq", "v", ids: new SequentialIdGenerator());

        stack.Execute(command);
        string detached = ProjectSerializer.Serialize(f.Project);
        string id = Assert.Single(command.CreatedIds);
        Assert.NotNull(f.Sequence.FindClip(id));
        stack.Undo();
        Assert.Equal(original, ProjectSerializer.Serialize(f.Project));
        stack.Redo();
        Assert.Equal(detached, ProjectSerializer.Serialize(f.Project));
    }
}
