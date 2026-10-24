using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.Analysis.Video;
using Rodaje.App.Services;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Services.Format;
using Rodaje.App.Shell;
using Rodaje.Core.Model;

namespace Rodaje.App.Views.Analysis;

/// <summary>
/// «Estabilizar»: analiza el movimiento del clip (o lo toma de la caché), muestra cuánto temblor queda y cuánto se
/// acerca la imagen con cada ajuste y, al aceptar, pone el efecto en un paso del historial.
/// </summary>
internal static class StabilizeDialog
{
    public static async Task ShowAsync(EditorServices services, LongActionScope? scope = null)
    {
        EditorSession session = services.Session;
        if (ClipVideoSource.SelectedVideoClip(session) is not { } clip || services.Shell.XamlRoot is not { } root)
        {
            session.Notify(NotificationSeverity.Informational, "Estabilizar", "Selecciona un clip de video.");
            return;
        }

        EffectInstance? existing = clip.Effects.Find(e => e.Type == BuiltInEffects.Stabilize);
        double intensityValue = existing?.Parameters.GetValueOrDefault(BuiltInEffects.StabilizeParameters.Intensity)?.Value ?? 0.5;
        bool cropValue = existing?.Options.GetValueOrDefault(BuiltInEffects.StabilizeOptions.Crop) != "off";

        var panel = new StackPanel { Spacing = 2, Width = AnalysisUi.PreviewWidth };
        var status = new TextBlock { Text = "Analizando el movimiento…", TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Maximum = 1, Width = AnalysisUi.PreviewWidth };
        panel.Children.Add(status);
        panel.Children.Add(progress);

        StabilizationStore store = StabilizationStore.Default;
        (string Key, MotionTrack Track)? analysis = null;
        Slider? intensity = null;
        CheckBox? crop = null;
        void Refresh()
        {
            if (analysis is not { } a || intensity is null || crop is null)
            {
                return;
            }

            StabilizationResult result = a.Track.Stabilize(new StabilizeOptions(intensity.Value, crop.IsChecked == true));
            double pixels = a.Track.Height;
            double reliable = a.Track.Motions.Count(m => m.Inliers > 0) / (double)Math.Max(1, a.Track.Motions.Count - 1);
            status.Text = string.Create(SpanishNumbers.Format,
                $"Temblor: {result.ShakeBefore * pixels:0.0} → {result.ShakeAfter * pixels:0.00} px por fotograma² " +
                $"({(result.ShakeBefore > 0 ? 1 - (result.ShakeAfter / result.ShakeBefore) : 0):P0} menos). " +
                $"Acercamiento: {result.Zoom - 1:P0}. Fotogramas con movimiento medido: {Math.Min(1, reliable):P0}.");
        }

        intensity = AnalysisUi.Slider(panel, "Intensidad", 0, 1, intensityValue, 0.05,
            v => string.Create(SpanishNumbers.Format, $"{v * 100:0} %"), Refresh);
        crop = new CheckBox { Content = "Recortar el borde automáticamente", IsChecked = cropValue };
        crop.Checked += (_, _) => Refresh();
        crop.Unchecked += (_, _) => Refresh();
        panel.Children.Add(crop);

        ContentDialog dialog = AnalysisUi.Dialog(root, "Estabilizar", "Aplicar", panel, AnalysisApplyPhase.NeedsAnalysis);
        using var cancel = new CancellationTokenSource();
        Task work = Task.CompletedTask;
        dialog.PrimaryButtonClick += (_, args) => Rodaje.App.Services.Crashes.UiTask.Run(AnalyzeBeforeApply(args));
        async Task AnalyzeBeforeApply(ContentDialogButtonClickEventArgs args)
        {
            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            try { if (analysis is null) { work = Analyze(); await work; } args.Cancel = analysis is null; }
            finally { deferral.Complete(); }
        }
        ContentDialogResult? answer = await DialogGate.ShowAsync(dialog);
        await cancel.CancelAsync();
        try
        {
            await work;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (answer != ContentDialogResult.Primary || analysis is not { } done || session.Sequence.FindClip(clip.Id) is null)
        {
            return;
        }

        session.Execute(VideoAnalysisEdits.Stabilize(session.SequenceId, clip.Id, done.Key, intensity.Value, crop.IsChecked == true));

        async Task Analyze()
        {
            try
            {
                analysis = await ClipVideoSource.AnalyzeMotionAsync(session, clip, store, new Progress<double>(p => scope?.Report(progress.Value = p, "Analizando el movimiento…")), cancel.Token);
                progress.Visibility = Visibility.Collapsed;
                Refresh();
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status.Text = $"No se pudo analizar el video: {ex.Message}";
                progress.Visibility = Visibility.Collapsed;
            }
        }
    }
}
