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
/// elegido cada N pulsos («cortar al ritmo») o ajusta los demás clips seleccionados para que cada uno termine en el pulso
/// que toca («montaje al ritmo»); todo en un paso del historial.
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
        IReadOnlyList<string> others = OtherSelectedClips(session, clip);
        var fit = new CheckBox
        {
            Content = others.Count == 1 ? "Ajustar al ritmo el otro clip seleccionado" : $"Ajustar al ritmo los otros {others.Count} clips seleccionados",
            Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        ToolTipService.SetToolTip(fit, "Cada clip termina en el pulso que toca y lo que sigue se corre, así los cortes caen en la música. Un clip no crece más allá de su material.");
        var every = new ComboBox { ItemsSource = new[] { "cada pulso", "cada 2 pulsos", "cada 4 pulsos (compás)", "cada 8 pulsos" }, SelectedIndex = 2 };
        panel.Children.Add(status);
        panel.Children.Add(progress);
        panel.Children.Add(preview);
        panel.Children.Add(markers);
        panel.Children.Add(cut);
        panel.Children.Add(fit);
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

            if (fit.IsChecked == true && others.Count > 0)
            {
                session.Execute(Edits.FitClipsToBeats(session.SequenceId, others, beats, step));
            }
        }
        finally
        {
            session.EndGesture();
        }

        if (fit.IsChecked == true && others.Count > 0)
        {
            session.Notify(NotificationSeverity.Success, "Montaje al ritmo",
                others.Count == 1 ? "El clip termina en el pulso que le toca." : $"{others.Count} clips terminan en el pulso que les toca; Ctrl+Z deshace todo.");
        }

        /// <summary>Los clips seleccionados que no son la música ni su grupo enlazado: los que se ajustan al ritmo.</summary>
        static IReadOnlyList<string> OtherSelectedClips(EditorSession session, Clip music) =>
            [.. session.SelectedClipIds.Where(id => id != music.Id && session.Sequence.FindClip(id) is { } found
                && (music.LinkId is null || found.Clip.LinkId != music.LinkId))];

        async Task Analyze()
        {
            try
            {
                await ClipAudioSource.AnalyzeAsync(session, clip, envelope, new Progress<double>(p => scope?.Report(progress.Value = p, "Analizando el audio…")), cancel.Token);
                result = await Task.Run(() => BeatTracker.Track(envelope), cancel.Token);
                progress.Visibility = Visibility.Collapsed;
                scope?.Report(1, "Análisis listo");
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
