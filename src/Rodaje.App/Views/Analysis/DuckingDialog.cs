using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.Analysis;
using Rodaje.Analysis.Ducking;
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
/// «Ducking automático»: el clip seleccionado es la música; se busca la voz en otra pista (sus clips que se cruzan con la
/// música) y la música baja con keyframes de volumen antes de cada frase. Un paso del historial.
/// </summary>
internal static class DuckingDialog
{
    /// <summary>El detector; cuando exista el módulo VAD se elige aquí sin tocar el resto.</summary>
    internal static IVoiceActivityDetector Detector { get; set; } = new EnergyVoiceDetector();

    public static async Task ShowAsync(EditorServices services, LongActionScope? scope = null)
    {
        EditorSession session = services.Session;
        if (ClipAudioSource.SelectedAudioClip(session) is not { } music || services.Shell.XamlRoot is not { } root)
        {
            session.Notify(NotificationSeverity.Informational, "Ducking automático", "Selecciona el clip de música.");
            return;
        }

        Track musicTrack = session.Sequence.FindClip(music.Id)!.Value.Track;
        List<Track> voiceTracks = [.. session.Sequence.AllTracks.Where(t => t != musicTrack && VoiceClips(session, t, music).Count > 0)];
        if (voiceTracks.Count == 0)
        {
            session.Notify(NotificationSeverity.Informational, "Ducking automático", "No hay otra pista con sonido debajo de la música.");
            return;
        }

        var panel = new StackPanel { Spacing = 2, Width = AnalysisUi.PreviewWidth };
        var trackBox = new ComboBox { ItemsSource = voiceTracks.Select(t => $"Voz en «{t.Name}»").ToList(), SelectedIndex = 0, Width = AnalysisUi.PreviewWidth };
        var status = new TextBlock { Text = "Analizando la voz…", TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Maximum = 1, Width = AnalysisUi.PreviewWidth };
        Canvas preview = AnalysisUi.Preview();
        panel.Children.Add(trackBox);
        panel.Children.Add(status);
        panel.Children.Add(progress);
        panel.Children.Add(preview);

        var options = new DuckingOptions();
        List<TimeRange> voice = [];
        List<GainPoint> plan = [];
        bool ready = false;
        Slider? reduction = null, anticipation = null, release = null;
        double origin = music.Start.ToSeconds();
        double length = music.Duration.ToSeconds();
        void Refresh()
        {
            if (!ready || reduction is null || anticipation is null || release is null)
            {
                return;
            }

            options = options with { ReductionDb = reduction.Value, Anticipation = anticipation.Value, Release = release.Value };
            plan = DuckingPlanner.Plan(voice, options);
            IEnumerable<TimeRange> ducked = plan.Chunk(4).Where(c => c.Length == 4).Select(c => new TimeRange(c[0].Time - origin, c[3].Time - origin));
            AnalysisUi.DrawRanges(preview, length, ducked, ThemeResources.Color("VoiceMarkColor"));
            status.Text = string.Create(SpanishNumbers.Format, $"{voice.Count} tramos de voz; la música baja {-options.ReductionDb:0} dB en cada uno.");
        }

        reduction = AnalysisUi.Slider(panel, "Cuánto baja la música", -30, -3, options.ReductionDb, 1, AnalysisUi.Db, Refresh);
        anticipation = AnalysisUi.Slider(panel, "Baja antes de la voz", DuckingOptions.MinimumAnticipation, 0.5, options.Anticipation, 0.01, AnalysisUi.Ms, Refresh);
        release = AnalysisUi.Slider(panel, "Vuelve a subir en", 0.1, 2, options.Release, 0.05, AnalysisUi.Sec, Refresh);

        ContentDialog dialog = AnalysisUi.Dialog(root, "Ducking automático", "Aplicar", panel);
        CancellationTokenSource cancel = new();
        Task analysis = Analyze(voiceTracks[0], cancel.Token);
        trackBox.SelectionChanged += (_, _) =>
        {
            cancel.Cancel();
            cancel.Dispose();
            cancel = new CancellationTokenSource();
            ready = false;
            dialog.IsPrimaryButtonEnabled = false;
            analysis = Analyze(voiceTracks[Math.Max(0, trackBox.SelectedIndex)], cancel.Token);
        };

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
        finally
        {
            cancel.Dispose();
        }

        if (answer != ContentDialogResult.Primary || plan.Count == 0 || session.Sequence.FindClip(music.Id) is null)
        {
            return;
        }

        (Time, double)[] envelope = [.. plan.Select(p => (Time.FromSeconds(Math.Max(0, p.Time)), p.OffsetDb))];
        session.Execute(Edits.Duck(session.SequenceId, [music.Id], envelope));

        async Task Analyze(Track track, CancellationToken token)
        {
            try
            {
                progress.Visibility = Visibility.Visible;
                status.Text = "Analizando la voz…";
                List<Clip> clips = VoiceClips(session, track, music);
                var found = new List<TimeRange>();
                for (int i = 0; i < clips.Count; i++)
                {
                    Clip clip = clips[i];
                    IVoiceActivitySession detection = Detector.Begin(ClipAudioSource.SampleRate, ClipAudioSource.Channels);
                    int index = i;
                    await ClipAudioSource.AnalyzeAsync(session, clip, detection, new Progress<double>(p => scope?.Report(progress.Value = (index + p) / clips.Count, "Buscando la voz…")), token);
                    foreach (TimeRange r in await detection.FinishAsync(token))
                    {
                        (Time start, Time end) = ClipAudioMap.ToTimeline(clip, r);
                        found.Add(new(start.ToSeconds(), end.ToSeconds()));
                    }
                }

                token.ThrowIfCancellationRequested();
                voice = TimeRange.Merge(found);
                progress.Visibility = Visibility.Collapsed;
                ready = true;
                Refresh();
                dialog.IsPrimaryButtonEnabled = plan.Count > 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status.Text = $"No se pudo leer el audio: {ex.Message}";
                progress.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Clips con sonido de la pista que se cruzan con la música.</summary>
    private static List<Clip> VoiceClips(EditorSession session, Track track, Clip music) =>
        [.. track.Clips.Where(c => c.Id != music.Id && c.Start < music.End && c.End > music.Start && ClipAudioSource.HasAudio(session, c))];
}
