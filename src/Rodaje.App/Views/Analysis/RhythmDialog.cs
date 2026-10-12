using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.Analysis;
using Rodaje.Analysis.Rhythm;
using Rodaje.App.Services;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Services.Format;
using Rodaje.App.Shell;
using Rodaje.App.Theme;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Analysis;

/// <summary>
/// «Detectar ritmo»: tempo y pulsos del clip de música. Pone marcadores en los pulsos y, si se pide, corta el clip
/// elegido cada N pulsos («cortar al ritmo»); todo en un paso del historial.
/// </summary>
internal static class RhythmDialog
{
    public static async Task ShowAsync(EditorServices services, LongActionScope? scope = null)
    {
        EditorSession session = services.Session;
        if (ClipAudioSource.SelectedAudioClip(session) is not { } clip || services.Shell.XamlRoot is not { } root)
        {
            session.Notify(NotificationSeverity.Informational, "Detectar ritmo", "Selecciona el clip de música.");
            return;
        }

        var panel = new StackPanel { Spacing = 8, Width = AnalysisUi.PreviewWidth };
        var status = new TextBlock { Text = "Analizando el audio…", TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Maximum = 1, Width = AnalysisUi.PreviewWidth };
        Canvas preview = AnalysisUi.Preview();
        var markers = new CheckBox { Content = "Poner un marcador en cada pulso", IsChecked = true };
        var cut = new CheckBox { Content = "Cortar el clip al ritmo" };
        var every = new ComboBox { ItemsSource = new[] { "cada pulso", "cada 2 pulsos", "cada 4 pulsos (compás)", "cada 8 pulsos" }, SelectedIndex = 2 };
        panel.Children.Add(status);
        panel.Children.Add(progress);
        panel.Children.Add(preview);
        panel.Children.Add(markers);
        panel.Children.Add(cut);
        panel.Children.Add(every);

        ContentDialog dialog = AnalysisUi.Dialog(root, "Detectar ritmo", "Aplicar", panel);
        var envelope = new OnsetEnvelope(ClipAudioSource.SampleRate, ClipAudioSource.Channels);
        BeatAnalysis? result = null;
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

        if (answer != ContentDialogResult.Primary || result is not { Beats.Count: > 0 } || session.Sequence.FindClip(clip.Id) is null)
        {
            return;
        }

        List<Time> beats = [.. result.Beats.Select(b => ClipAudioMap.ToTimeline(clip, b))];
        int step = 1 << Math.Max(0, every.SelectedIndex);
        session.BeginGesture("Ritmo");
        try
        {
            if (markers.IsChecked == true)
            {
                session.Execute(Edits.AddBeatMarkers(session.SequenceId, beats));
            }

            if (cut.IsChecked == true)
            {
                session.Execute(Edits.CutToBeats(session.SequenceId, clip.Id, beats.Where((_, i) => i % step == 0)));
            }
        }
        finally
        {
            session.EndGesture();
        }

        async Task Analyze()
        {
            try
            {
                await ClipAudioSource.AnalyzeAsync(session, clip, envelope, new Progress<double>(p => scope?.Report(progress.Value = p, "Analizando el audio…")), cancel.Token);
                result = await Task.Run(() => BeatTracker.Track(envelope), cancel.Token);
                progress.Visibility = Visibility.Collapsed;
                double width = Math.Max(0.02, result.Duration / AnalysisUi.PreviewWidth);
                AnalysisUi.DrawRanges(preview, result.Duration, result.Beats.Select(b => new TimeRange(b, b + width)), ThemeResources.Color("BeatMarkColor"));
                status.Text = result.Beats.Count == 0
                    ? "No se encontró un pulso regular en este clip."
                    : string.Create(SpanishNumbers.Format, $"Tempo: {result.Bpm:0.0} BPM · {result.Beats.Count} pulsos.");
                dialog.IsPrimaryButtonEnabled = result.Beats.Count > 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status.Text = $"No se pudo leer el audio: {ex.Message}";
                progress.Visibility = Visibility.Collapsed;
            }
        }
    }
}
