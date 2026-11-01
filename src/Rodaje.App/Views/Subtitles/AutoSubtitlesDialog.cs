using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.Analysis;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Modules;
using Rodaje.App.Services.Titles;
using Rodaje.App.Shell;
using Rodaje.App.Theme;
using Rodaje.App.Views.Analysis;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Views.Subtitles;

/// <summary>
/// «Subtítulos automáticos» (menú del clip): transcribe la voz del clip con modulo-subtitulos, muestra los subtítulos como
/// quedarán (lista con tiempos y barra del clip; un clic lleva el cursor ahí) y, al aplicar, los pone en una pista de
/// subtítulos en un paso del historial, con sus palabras guardadas para editar por palabra. Cancelar o cerrar deja el
/// proyecto como estaba.
/// </summary>
internal static class AutoSubtitlesDialog
{
    public const string Label = "Subtítulos automáticos…";
    public const string Glyph = "";
    private const string Title = "Subtítulos automáticos";

    private static string _language = "auto";

    /// <summary>Lo que llama el menú, como acción larga: un fallo se avisa, no cierra la app.</summary>
    public static void Run(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        UiTask.Run(services.LongActions.RunAsync(SubtitleCommands.AutoSubtitles, Title, scope => ShowAsync(services, scope)));
    }

    public static async Task ShowAsync(EditorServices services, LongActionScope? scope = null)
    {
        EditorSession session = services.Session;
        if (AutoSubtitles.TargetClip(session.Sequence, session.SelectedClipIds, c => ClipAudioSource.HasAudio(session, c)) is not { } clip
            || services.Shell.XamlRoot is not { } root)
        {
            session.Notify(NotificationSeverity.Informational, Title, "Selecciona un clip con voz (un video con sonido o un clip de audio).");
            return;
        }

        if (services.Modules is not { } modules || modules.FindByFunction(AutoSubtitles.Function) is not { } module)
        {
            session.Notify(NotificationSeverity.Informational, Title, "Hace falta el módulo «Subtítulos automáticos». Instálalo desde Complementos › Módulos de IA.");
            return;
        }

        if (clip.Speed is { Freeze: true } || session.Project.FindMedia(clip.MediaId) is not { } media)
        {
            session.Notify(NotificationSeverity.Informational, Title, "Un fotograma congelado no tiene voz que transcribir.");
            return;
        }

        (Time Start, Time End) window = AutoSubtitles.Window(clip);
        if (window.End <= window.Start || window.End - window.Start > ModuleMedia.MaxAudioSpan)
        {
            session.Notify(NotificationSeverity.Informational, Title, "El clip dura más de tres horas: córtalo y transcribe cada parte.");
            return;
        }

        session.Pause();
        string sequenceId = session.SequenceId;
        Rational frameRate = session.Sequence.FrameRate;
        double clipSeconds = Math.Max(1e-6, clip.Duration.ToSeconds());

        var panel = new StackPanel { Spacing = 4, Width = AnalysisUi.PreviewWidth };
        // Textos sueltos en las listas (como en los demás diálogos de análisis): sin enlaces a propiedades que AOT recorte.
        var language = new ComboBox
        {
            Header = "Idioma de la voz",
            Width = AnalysisUi.PreviewWidth,
            ItemsSource = AutoSubtitles.Languages.Select(l => l.Name).ToList(),
            SelectedIndex = Math.Max(0, AutoSubtitles.Languages.ToList().FindIndex(l => l.Id == _language)),
        };
        panel.Children.Add(language);
        var advice = new Views.Shared.AiAdvicePanel();
        advice.Show(AutoSubtitles.Estimate(modules, module, window));
        panel.Children.Add(advice.Panel);

        var again = new Button { Content = "Transcribir de nuevo", IsEnabled = false, Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(again);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Maximum = 1, Width = AnalysisUi.PreviewWidth };
        panel.Children.Add(status);
        panel.Children.Add(progress);

        Canvas bar = AnalysisUi.Preview();
        panel.Children.Add(bar);
        var list = new ListView { Height = 240, Width = AnalysisUi.PreviewWidth, SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        panel.Children.Add(list);
        panel.Children.Add(new TextBlock
        {
            Text = "Un clic en un subtítulo lleva el cursor ahí. Al aplicar van a una pista de subtítulos; un Ctrl+Z los quita todos.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });

        ContentDialog dialog = AnalysisUi.Dialog(root, Title, "Aplicar", panel, AnalysisApplyPhase.NeedsAnalysis);
        using var cancel = new CancellationTokenSource();
        ModuleJob? job = null;
        // La propuesta se descarta al empezar cada transcripción: si la nueva falla o se cancela, no queda la anterior.
        var draft = new TranscriptionDraft();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        void ShowActivity()
        {
            ModuleActivityView view = ModuleActivity.Describe(job, cancel.IsCancellationRequested, "Transcribiendo");
            progress.IsIndeterminate = view.IsIndeterminate;
            progress.Value = view.Progress;
            scope?.Report(view.IsIndeterminate ? null : view.Progress, view.IsIndeterminate ? view.Text : "Transcribiendo la voz…");
            status.Text = view.Text;
        }

        timer.Tick += (_, _) => ShowActivity();

        string Selected() => language.SelectedIndex >= 0 ? AutoSubtitles.Languages[language.SelectedIndex].Id : "auto";
        List<string> labels = [];

        void Show(IReadOnlyList<SubtitleCue> shown)
        {
            labels = [.. shown.Select(c => $"{AutoSubtitles.Range(c.Start, c.End)}   {c.Text.Replace('\n', ' ')}")];
            list.ItemsSource = labels;
            AnalysisUi.DrawRanges(bar, clipSeconds,
                shown.Select(c => new TimeRange((c.Start - clip.Start).ToSeconds(), (c.End - clip.Start).ToSeconds())),
                ThemeResources.Color("BeatMarkColor"));
        }

        async Task Transcribe()
        {
            draft.Begin();
            Show(draft.Cues);
            again.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = true;
            progress.Visibility = Visibility.Visible;
            string wanted = Selected();
            job = null;
            ShowActivity();
            timer.Start();
            try
            {
                Transcript transcript = await AutoSubtitles.RunAsync(modules, module, media.Path, window, wanted,
                    $"Subtítulos de «{media.Name}»", cancel.Token, j => job = j);
                IReadOnlyList<SpeechSegment> segments = AutoSubtitles.ToSequence(clip, transcript);
                IReadOnlyList<SubtitleCue> cues = AutoSubtitles.Cues(segments, frameRate);
                draft.Complete(wanted, segments, cues);
                _language = wanted;
                Show(cues);
                status.Text = cues.Count == 0
                    ? "No se oyó voz en el clip."
                    : string.Create(SpanishNumbers.Format, $"{cues.Count} subtítulos{(transcript.Language is { Length: > 0 } found && wanted == "auto" ? $" (idioma: {found})" : "")}. Revísalos y aplica.");
            }
            catch (OperationCanceledException)
            {
                draft.Fail();
                status.Text = "Cancelado. El proyecto no cambió.";
            }
            // «Transcribir de nuevo» la llama desde un manejador async void: ningún fallo puede escapar y cerrar la app.
#pragma warning disable CA1031 // Se muestra en el diálogo.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                draft.Fail();
                status.Text = ex is ModuleException error ? ModuleManager.Friendly(module, error) : "No se pudo transcribir: " + ex.Message + " El proyecto no cambió.";
                if (ex is not ModuleException)
                {
                    UiTask.Report(ex, notify: false);
                }

            }
            finally
            {
                timer.Stop();
                progress.Visibility = Visibility.Collapsed;
                if (draft.Running)
                {
                    draft.Fail();
                }

                again.IsEnabled = draft.CanTranscribe(Selected());
                // Activo mientras transcribe (aplica al terminar); después, solo si hay algo que aplicar.
                dialog.IsPrimaryButtonEnabled = draft.CanApply;
            }
        }

        language.SelectionChanged += (_, _) => again.IsEnabled = draft.CanTranscribe(Selected());
        Task current = Task.CompletedTask;
        again.Click += (_, _) => UiTask.Run(current = Transcribe());
        list.ItemClick += (_, e) =>
        {
            int index = e.ClickedItem is string label ? labels.IndexOf(label) : -1;
            if (index >= 0 && index < draft.Cues.Count)
            {
                session.Seek(draft.Cues[index].Start);
            }
        };

        current = Transcribe();
        dialog.PrimaryButtonClick += (_, args) => Rodaje.App.Services.Crashes.UiTask.Run(WaitForTranscription(args));
        async Task WaitForTranscription(ContentDialogButtonClickEventArgs args)
        {
            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            try { await current; args.Cancel = !draft.CanApply; }
            finally { deferral.Complete(); }
        }
        ContentDialogResult? answer = await DialogGate.ShowAsync(dialog);
        timer.Stop();
        if (answer != ContentDialogResult.Primary || draft.Running)
        {
            await cancel.CancelAsync();
            await services.LongActions.ShowCancellingAsync(Title, current, scope);
        }

        await current;

        if (answer != ContentDialogResult.Primary || !draft.CanApply || draft.Segments is not { } chosen)
        {
            return;
        }

        IReadOnlyList<SubtitleCue> applied = draft.Cues;
        var proposal = new ModuleProposal(AutoSubtitles.Description, sequenceId,
            [.. applied.Select(c => new PreviewMark(PreviewMarkKind.Clip, c.Start, c.End, c.Text))],
            _ => [AutoSubtitles.Command(sequenceId, chosen, SubtitleStyles.NewSubtitle())]);
        if (services.Proposals is { } proposals)
        {
            proposals.Show(proposal);
            if (proposals.Apply())
            {
                session.Notify(NotificationSeverity.Success, Title, string.Create(SpanishNumbers.Format, $"Se pusieron {applied.Count} subtítulos."));
            }
        }
        else if (session.SequenceId == sequenceId)
        {
            session.Execute(proposal.CreateCommand(session.Project));
        }
    }
}
