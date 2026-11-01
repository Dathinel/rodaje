using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing;

/// <summary>«Sincronizar por el sonido»: el inicio nuevo sale del desfase entre medios y el clip se mueve con sus enlazados.</summary>
public sealed class AudioAlignmentTests
{
    /// <summary>Cámara (video + audio enlazado, desde el cuadro 10 del medio) en el cuadro 50; grabadora externa suelta en el 0.</summary>
    private static TrimFixture Shoot() => new TrimFixture()
        .Add("v1", "cam-v", "m1", 10, 60, 50, "L")
        .Add("a1", "cam-a", "m1", 10, 60, 50, "L")
        .Add("a2", "rec", "m3", 0, 50, 0);

    [Fact]
    public void The_new_start_puts_the_same_sound_at_the_same_sequence_instant()
    {
        var reference = new Clip { Id = "r", Start = F(50), SourceIn = F(10), SourceOut = F(60) };
        var other = new Clip { Id = "o", Start = F(0), SourceIn = F(0), SourceOut = F(50) };

        // El 0 de la grabadora cae en el cuadro 4 de la cámara: su cuadro 0 suena cuando la cámara va por el 4, o sea en 50 + (4 − 10) = 44.
        Assert.Equal(F(44), AnalysisEdits.AlignedStart(reference, other, F(4)));
        // Si la grabadora arrancó 6 cuadros antes que la cámara (offset −6), su cuadro 0 suena en 50 + (−6 − 10) = 34.
        Assert.Equal(F(34), AnalysisEdits.AlignedStart(reference, other, F(-6)));
        // Un clip que ya está recortado: su SourceIn también cuenta.
        var trimmed = new Clip { Id = "t", Start = F(0), SourceIn = F(20), SourceOut = F(50) };
        Assert.Equal(F(64), AnalysisEdits.AlignedStart(reference, trimmed, F(4)));
    }

    [Fact]
    public void Aligning_moves_the_recorder_in_one_undo_step_and_keeps_the_reference_still()
    {
        TrimFixture f = Shoot();
        var undo = new UndoStack(f.Project);
        SequenceSnapshot before = SequenceSnapshot.Capture(f.Sequence);

        undo.Execute(Edits.AlignByAudio("seq", "cam-v", [("rec", F(4))]));

        f.AssertClip("rec", 0, 50, 44);
        f.AssertClip("cam-v", 10, 60, 50);
        f.AssertClip("cam-a", 10, 60, 50);
        f.AssertValid();
        Assert.True(undo.Undo());
        Assert.True(before.Matches(f.Sequence));
    }

    [Fact]
    public void Aligning_the_camera_to_the_recorder_moves_video_and_linked_audio_together()
    {
        TrimFixture f = Shoot();
        // Referencia: la grabadora. El 0 de la cámara cae en el cuadro −4 de la grabadora: el cuadro 10 de la cámara suena en 0 + (10 − 4 − 0) = 6.
        IReadOnlyList<(string, Time)> shifts = AnalysisEdits.AlignByAudio(f.Ctx, "rec", [("cam-v", F(-4))]);

        Assert.Equal([("cam-v", F(-44))], shifts);
        f.AssertClip("cam-v", 10, 60, 6);
        f.AssertClip("cam-a", 10, 60, 6);
        f.AssertValid();
    }

    [Fact]
    public void An_offset_off_the_frame_grid_snaps_to_the_frame_and_an_aligned_clip_reports_no_shift()
    {
        TrimFixture f = Shoot();
        Time almostFour = F(4) + Time.FromSeconds(0.01);

        IReadOnlyList<(string, Time)> shifts = AnalysisEdits.AlignByAudio(f.Ctx, "cam-v", [("rec", almostFour)]);
        Assert.Equal(F(44), f.Clip("rec").Start);

        shifts = AnalysisEdits.AlignByAudio(f.Ctx, "cam-v", [("rec", F(4))]);
        Assert.Equal([("rec", Time.Zero)], shifts);
    }

    [Fact]
    public void It_fails_when_the_place_is_taken_before_the_start_or_with_a_retimed_reference()
    {
        TrimFixture busy = Shoot().Add("a2", "wall", "m3", 0, 50, 60);
        AssertFails(() => AnalysisEdits.AlignByAudio(busy.Ctx, "cam-v", [("rec", F(4))]), "ocupado");

        TrimFixture early = Shoot();
        AssertFails(() => AnalysisEdits.AlignByAudio(early.Ctx, "cam-v", [("rec", F(-60))]), "antes del inicio");

        TrimFixture fast = Shoot();
        fast.Clip("cam-v").Speed = new ClipSpeed { Rate = new Rational(2, 1) };
        AssertFails(() => AnalysisEdits.AlignByAudio(fast.Ctx, "cam-v", [("rec", F(4))]), "1×");
    }
}
