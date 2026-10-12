using Rodaje.App.Services;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Player;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Render.Scopes;
using Q = Rodaje.Core.Model.BuiltInEffects.HslQualifierParameters;

namespace Rodaje.App.Shell;

internal static partial class SelfTestRun
{
    /// <summary>
    /// Color y efectos: una pila con calificador, curvas de tono, resplandor y grano en el primer clip; los monitores abiertos
    /// (cada uno tiene que llegar de la GPU), la vista de la máscara del calificador y el A/B. Deshace al terminar.
    /// </summary>
    private static async Task ColorAsync(MainWindow window, PlayerEngine engine)
    {
        EditorSession session = window.Session;
        if (session.MainTrack is not { Clips.Count: > 0 } main)
        {
            return;
        }

        string clipId = main.Clips[0].Id;
        int steps = session.History.Count;
        string? glowId = null;
        session.Select([clipId]);
        if (!session.Execute(new SequenceEditCommand("Color y efectos (autoprueba)", session.SequenceId, ctx =>
        {
            string color = EffectEdits.AddEffect(ctx, clipId, BuiltInEffects.Color);
            EffectEdits.SetParameter(ctx, clipId, color, BuiltInEffects.ColorParameters.Contrast, 0.15, Core.Timing.Time.Zero);
            string qualifier = EffectEdits.AddEffect(ctx, clipId, BuiltInEffects.HslQualifier);
            EffectEdits.SetParameter(ctx, clipId, qualifier, Q.HueCenter, 30, Core.Timing.Time.Zero);
            EffectEdits.SetParameter(ctx, clipId, qualifier, Q.Saturation, 0.4, Core.Timing.Time.Zero);
            string curves = EffectEdits.AddEffect(ctx, clipId, BuiltInEffects.Curves);
            EffectEdits.SetOption(ctx, clipId, curves, BuiltInEffects.CurvesOptions.HueSaturation, "0,0.5;0.6,0.25");
            glowId = EffectEdits.AddEffect(ctx, clipId, BuiltInEffects.Glow);
            string grain = EffectEdits.AddEffect(ctx, clipId, BuiltInEffects.Grain);
            EffectEdits.SetParameter(ctx, clipId, grain, BuiltInEffects.GrainParameters.Amount, 0.2, Core.Timing.Time.Zero);
        })))
        {
            throw new InvalidOperationException("La autoprueba no pudo añadir los efectos de color.");
        }

        window.InspectorPane.Show(InspectorView.Tab.Filters);
        PlayerView player = window.PlayerPane;
        player.ShowScopes(true, save: false);
        foreach ((ScopeKind kind, string name) in new[]
        {
            (ScopeKind.Waveform, "monitores-onda"), (ScopeKind.Parade, "monitores-desfile"),
            (ScopeKind.Vectorscope, "monitores-vectores"), (ScopeKind.Histogram, "monitores-histograma"),
        })
        {
            player.Scopes.Kind = kind;
            for (int i = 0; i < 100 && player.Scopes.ShownKind != kind; i++)
            {
                await Task.Delay(30);
            }

            if (player.Scopes.ShownKind != kind)
            {
                throw new InvalidOperationException($"El monitor {kind} no llegó de la GPU al panel.");
            }

            await Task.Delay(120);
            await CaptureAsync(window, name);
        }

        Program.Metrics.Note($"autoprueba: monitores, {engine.ScopeSubmitMilliseconds:0.00} ms de CPU al encolar");
        player.ShowScopes(false, save: false);

        await Task.Delay(800);
        FrameCapture graded = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await CaptureAsync(window, "filtros-pila");
        engine.SetQualifierMaskView(true);
        await Task.Delay(250);
        FrameCapture mask = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        engine.SetQualifierMaskView(false);
        await SaveFrameAsync(mask, "calificador-mascara");
        if (glowId is { } glow)
        {
            session.SetPreviewOff(glow, true);
            await Task.Delay(300);
            FrameCapture withoutGlow = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            session.SetPreviewOff(glow, false);
            double difference = MeanLuma(graded, 0, 1) - MeanLuma(withoutGlow, 0, 1);
            Program.Metrics.Note($"autoprueba: A/B del resplandor, luma media {difference:+0.0;-0.0}");
            if (session.History.Count != steps + 1)
            {
                throw new InvalidOperationException("El A/B tocó el historial.");
            }
        }

        session.Undo();
        if (session.History.Count != steps)
        {
            throw new InvalidOperationException("Deshacer no quitó los efectos de color de la autoprueba.");
        }
    }
}
