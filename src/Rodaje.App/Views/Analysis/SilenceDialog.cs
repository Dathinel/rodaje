using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.Analysis;
using Rodaje.Analysis.Ducking;
using Rodaje.Analysis.Silence;
using Rodaje.App.Services;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Services.Modules;
using Rodaje.App.Services.Format;
using Rodaje.App.Shell;
using Rodaje.App.Theme;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Analysis;

/// <summary>
/// «Quitar silencios»: analiza el audio del clip, muestra en rojo lo que se corta y, al aceptar, lo quita en un paso del
/// historial con el ripple de la sesión. Mover los ajustes vuelve a detectar al instante (la envolvente queda en memoria).
/// </summary>
internal static class SilenceDialog
{
    internal static IVoiceActivityDetector Detector { get; set; } = new EnergyVoiceDetector();

    public static async Task ShowAsync(EditorServices services, LongActionScope? scope = null)
    {
        EditorSession session = services.Session;
        if (ClipAudioSource.SelectedAudioClip(session) is not { } clip || services.Shell.XamlRoot is not { } root)
        {
            session.Notify(NotificationSeverity.Informational, "Quitar silencios", "Selecciona un clip con sonido.");
            return;
        }

        var panel = new StackPanel { Spacing = 2, Width = AnalysisUi.PreviewWidth };
        var status = new TextBlock { Text = "Analizando el audio…", TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Maximum = 1, Width = AnalysisUi.PreviewWidth };
        Canvas preview = AnalysisUi.Preview();
        panel.Children.Add(status);
        panel.Children.Add(progress);
        panel.Children.Add(preview);

        bool useVad = Detector is ModuleVoiceDetector moduleDetector && moduleDetector.UsesModuleAt(ClipAudioSource.SampleRate);
        string method = useVad ? ((ModuleVoiceDetector)Detector).MethodName(ClipAudioSource.SampleRate) : "Energía del nivel de audio";
        (Time sourceIn, Time sourceOut) = ClipAudioMap.SourceWindow(clip);
        double duration = (sourceOut - sourceIn).ToSeconds();
        var envelope = new LevelEnvelope(ClipAudioSource.SampleRate, ClipAudioSource.Channels);
        IReadOnlyList<TimeRange>? voice = null;
        SilenceAnalysis? result = null;
        bool ready = false;
        var options = new SilenceOptions();
        Slider? threshold = null, minimum = null, margin = null;
        void Refresh()
        {
            if (!ready || (!useVad && threshold is null) || minimum is null || margin is null)
            {
                return;
            }

            options = options with
            {
                ThresholdDb = threshold?.Value ?? options.ThresholdDb,
                MinimumSilence = minimum.Value,
                PaddingBefore = margin.Value,
                PaddingAfter = margin.Value,
            };
            result = voice is null
                ? SilenceDetector.Detect(envelope, options)
                : SilenceDetector.DetectFromVoice(voice, duration, options);
            AnalysisUi.DrawRanges(preview, result.Duration, result.Cuts, ThemeResources.Color("CutMarkColor"));
            status.Text = string.Create(SpanishNumbers.Format,
                $"{result.Cuts.Count} tramos; el clip pasa de {AnalysisUi.Sec(result.Duration)} a {AnalysisUi.Sec(result.Duration - result.CutSeconds)}. Detección: {method}.");
        }

        if (!useVad)
        {
            threshold = AnalysisUi.Slider(panel, "Umbral bajo el nivel de la voz", -60, -10, options.ThresholdDb, 1, AnalysisUi.Db, Refresh);
        }
        minimum = AnalysisUi.Slider(panel, "Silencio más corto que se quita", 0.1, 3, options.MinimumSilence, 0.05, AnalysisUi.Sec, Refresh);
        margin = AnalysisUi.Slider(panel, "Margen que se deja a cada lado", 0, 0.5, options.PaddingBefore, 0.01, AnalysisUi.Ms, Refresh);
        var ripple = new CheckBox { Content = "Cerrar los huecos (ripple)", IsChecked = true };
        panel.Children.Add(ripple);

        ContentDialog dialog = AnalysisUi.Dialog(root, "Quitar silencios", "Quitar", panel);
        using var cancel = new CancellationTokenSource();
        Task analysis = Analyze();
        ContentDialogResult? answer = await DialogGate.ShowAsync(dialog);
        await cancel.CancelAsync();
        try
        {
            await analysis;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (answer != ContentDialogResult.Primary || result is null || result.Cuts.Count == 0 || session.Sequence.FindClip(clip.Id) is null)
        {
            return;
        }

        List<(Time Start, Time End)> spans = ClipAudioMap.ToTimeline(clip, result.Cuts);
        session.Execute(Edits.RemoveSilences(session.SequenceId, clip.Id, spans, ripple.IsChecked == true, session.Timeline.RippleScope));

        async Task Analyze()
        {
            try
            {
                if (useVad)
                {
                    IVoiceActivitySession detection = Detector.Begin(ClipAudioSource.SampleRate, ClipAudioSource.Channels);
                    await ClipAudioSource.AnalyzeAsync(session, clip, detection, new Progress<double>(p => scope?.Report(progress.Value = p, "Analizando el audio…")), cancel.Token);
                    voice = await detection.FinishAsync(cancel.Token);
                    method = detection.MethodName;
                }
                else
                {
                    await ClipAudioSource.AnalyzeAsync(session, clip, envelope, new Progress<double>(p => scope?.Report(progress.Value = p, "Analizando el audio…")), cancel.Token);
                }
                progress.Visibility = Visibility.Collapsed;
                ready = true;
                Refresh();
                dialog.IsPrimaryButtonEnabled = result is { Cuts.Count: > 0 };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status.Text = $"No se pudo leer el audio: {ex.Message}";
                progress.Visibility = Visibility.Collapsed;
            }
        }
    }
}
