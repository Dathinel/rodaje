using Rodaje.App.Services;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Player;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Render;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;

namespace Rodaje.App.Shell;

internal static partial class SelfTestRun
{
    /// <summary>
    /// Máscaras en el primer clip: una estrella Bézier con borde y una zona desenfocada, la pestaña con sus vértices elegidos
    /// y la vista de la máscara en gris y superpuesta. Fuera de la estrella el cuadro tiene que oscurecerse; deshace al terminar.
    /// </summary>
    private static async Task MasksAsync(MainWindow window, PlayerEngine engine)
    {
        EditorSession session = window.Session;
        if (session.MainTrack is not { Clips.Count: > 0 } main || session.Project.FindMedia(main.Clips[0].MediaId) is not { HasVideo: true })
        {
            return;
        }

        string clipId = main.Clips[0].Id;
        int steps = session.History.Count;
        session.Select([clipId]);
        session.Seek(main.Clips[0].Start);
        FrameCapture before = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        SequenceEditCommand star = Edits.AddVertexMask(session.SequenceId, clipId, MaskShape.Bezier, 0.5, 0.5, 0.45, 0.8, MaskPath.Star(5), "Estrella (autoprueba)");
        if (!session.Execute(star)
            || !session.Execute(Edits.SetParameter(session.SequenceId, clipId, star.CreatedIds[0], M.Feather, 0.02, Core.Timing.Time.Zero))
            || !session.Execute(Edits.AddMaskedZone(session.SequenceId, clipId, ZoneEffect.Blur)))
        {
            throw new InvalidOperationException("La autoprueba no pudo añadir las máscaras.");
        }

        window.InspectorPane.Show(InspectorView.Tab.Masks);
        session.PickMask(star.CreatedIds[0]);
        session.SelectMaskVertices([0, 1]);
        await Task.Delay(400);
        FrameCapture masked = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await SaveFrameAsync(masked, "mascaras-visor");
        await CaptureAsync(window, "mascaras");
        session.SetMaskView(MaskViewMode.Matte);
        await Task.Delay(250);
        await SaveFrameAsync(await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5)), "mascaras-gris-visor");
        session.SetMaskView(MaskViewMode.Overlay);
        await Task.Delay(250);
        await SaveFrameAsync(await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5)), "mascaras-superpuesta-visor");
        session.SetMaskView(MaskViewMode.None);
        session.SetMaskTool(MaskTool.Pen);
        await Task.Delay(150);
        await CaptureAsync(window, "mascaras-pluma");
        session.SetMaskTool(MaskTool.Select);
        while (session.History.Count > steps && session.Undo())
        {
        }

        double corner = MeanLuma(before, 0, 0.08), cornerMasked = MeanLuma(masked, 0, 0.08);
        Program.Metrics.Note($"autoprueba: máscaras, esquina {corner:0.0} → {cornerMasked:0.0}");
        if (corner > 8 && cornerMasked > corner * 0.5)
        {
            throw new InvalidOperationException($"La estrella no recorta el clip: la esquina pasó de {corner:0.0} a {cornerMasked:0.0}.");
        }
    }
}
