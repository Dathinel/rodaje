using Rodaje.App.Services;
using Rodaje.App.Services.Audio;
using Rodaje.App.Tests.Support;
using Rodaje.Audio.Processing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit;
using K = Rodaje.Core.Model.BuiltInEffects.CompressorParameters;

namespace Rodaje.App.Tests.Audio;

/// <summary>La pestaña «Dinámica» sin interfaz: el compresor de un clip, de una pista o de la mezcla, con keyframes y deshacer.</summary>
public sealed class DynamicsEditorTests
{
    [Fact]
    public void A_track_compressor_is_created_on_first_change_in_one_step_and_animates_in_sequence_time() => UiThread.Run(async () =>
    {
        EditorSession session = await Open();
        string trackId = session.Sequence.AudioTracks[0].Id;
        var editor = new DynamicsEditor(session) { Owner = new DynamicsOwner(DynamicsOwnerKind.Track, trackId) };
        int steps = session.History.Count;

        Assert.True(editor.SetValue(K.Threshold, -30, "Cambiar umbral"));
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal(-30, editor.Read(K.Threshold).Value);

        At(session, 2);
        Assert.True(editor.ToggleKeyframe(K.Threshold));
        At(session, 6);
        Assert.True(editor.SetValue(K.Threshold, -10, "Cambiar umbral"));
        Assert.Equal(2, editor.Read(K.Threshold).KeyframeCount);
        List<Keyframe> keys = editor.Compressor()!.Parameters[K.Threshold].Keyframes;
        Assert.Equal([Time.FromSeconds(2), Time.FromSeconds(6)], keys.Select(k => k.Time));
        At(session, 4);
        Assert.Equal(-20, editor.Read(K.Threshold).Value, 3);

        Assert.True(editor.ClearKeyframes(K.Threshold, "Quitar keyframes de umbral"));
        Assert.Equal(-20, editor.Read(K.Threshold).Value, 3);
        Assert.Equal(0, editor.Read(K.Threshold).KeyframeCount);
        Assert.False(editor.SetSidechain(trackId), "Una pista no se escucha a sí misma.");
        Assert.DoesNotContain(editor.SidechainCandidates(), t => t.Id == trackId);
    });

    [Fact]
    public void The_mix_takes_presets_and_values_and_undo_restores_it() => UiThread.Run(async () =>
    {
        EditorSession session = await Open();
        var editor = new DynamicsEditor(session) { Owner = DynamicsOwner.Mix(session.Sequence) };
        Assert.Null(editor.Compressor());

        Assert.True(editor.ApplyPreset(AudioPresets.MixLimiter));
        Assert.Same(AudioPresets.MixLimiter, editor.MatchingPreset(out bool has));
        Assert.True(has);
        Assert.Equal(1, editor.Read(K.Limiter).Value);
        Assert.False(editor.SetSidechain(session.Sequence.AudioTracks[0].Id), "La mezcla no admite sidechain externo.");

        Assert.True(editor.SetValue(K.Mix, 60, "Cambiar mezcla en paralelo"));
        Assert.Null(editor.MatchingPreset(out _));
        Assert.True(session.Undo());
        Assert.True(session.Undo());
        Assert.Empty(session.Sequence.MasterEffects);
        Assert.Equal("mezcla:", editor.Owner!.MonitorKey);
    });

    [Fact]
    public void The_drawn_curve_is_the_static_curve_with_automatic_makeup() => UiThread.Run(async () =>
    {
        EditorSession session = await Open();
        var editor = new DynamicsEditor(session) { Owner = DynamicsOwner.Mix(session.Sequence) };
        editor.SetValue(K.Threshold, -20, "Cambiar umbral");
        editor.SetValue(K.Ratio, 4, "Cambiar relación");
        editor.SetValue(K.AutoMakeup, 1, "Cambiar compensación automática");
        (double threshold, double ratio, double knee, double makeup) = editor.Curve();
        Assert.Equal(7.5, makeup, 6);
        foreach ((double input, double output) in TransferCurve.Points(threshold, ratio, knee, makeup))
        {
            Assert.Equal(CompressorSettings.Curve(input, -20, 4, knee) + 7.5, output, 9);
        }

        Assert.Equal(121, TransferCurve.Points(threshold, ratio, knee, makeup).Count());
    });

    [Fact]
    public void The_history_names_the_compressor_not_a_filter() => UiThread.Run(async () =>
    {
        EditorSession session = await Open();
        var editor = new DynamicsEditor(session) { Owner = new DynamicsOwner(DynamicsOwnerKind.Track, session.Sequence.AudioTracks[0].Id) };

        Assert.True(editor.Add());
        Assert.Equal("Añadir compresor", session.History.UndoDescription);
        Assert.True(editor.SetEnabled(false));
        Assert.Equal("Desactivar compresor", session.History.UndoDescription);
        Assert.True(editor.Remove());
        Assert.Equal("Quitar compresor", session.History.UndoDescription);
    });

    private static void At(EditorSession session, double seconds) =>
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(seconds) });

    private static async Task<EditorSession> Open()
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        return session;
    }
}
