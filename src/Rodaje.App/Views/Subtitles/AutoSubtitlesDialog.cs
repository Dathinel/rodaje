using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.Analysis;
using Rodaje.App.Services;
using Rodaje.App.Services.Modules;
using Rodaje.App.Services.Titles;
using Rodaje.App.Shell;
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
    public const string Label = "Subtítulos automáticos...";
    public const string Glyph = "";
    private const string Title = "Subtítulos automáticos";

    private static string _language = "auto";

    /// <summary>Lo que llama el menú: un fallo se avisa, no cierra la app.</summary>
    public static async void Run(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        try
        {
            await ShowAsync(services);
        }
#pragma warning disable CA1031 // Un fallo del diálogo no debe cerrar la app: se avisa.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            services.Session.Notify(NotificationSeverity.Error, Title, ex.Message);
        }
    }

    public static async Task ShowAsync(EditorServices services)
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
            session.Notify(NotificationSeverity.Informational, Title, "Hace falta el módulo «Subtítulos automáticos». Instálalo desde Complementos.");
            return;
        }

        if (clip.Speed is { Freeze: true } || session.Project.FindMedia(clip.MediaId) is not { } media)
        {
            session.Notify(NotificationSeverity.Informational, Title, "Un cuadro congelado no tiene voz que transcribir.");
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

        ContentDialog dialog = AnalysisUi.Dialog(root, Title, "Aplicar", panel);
        using var cancel = new CancellationTokenSource();
        ModuleJob? job = null;
        IReadOnlyList<SpeechSegment>? segments = null;
        IReadOnlyList<SubtitleCue> cues = [];
        string transcribed = "";
        bool running = false;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (job is not null)
            {
                progress.Value = job.Progress;
                status.Text = (job.Stage ?? "Transcribiendo") + "…";
            }
        };

        string Selected() => language.SelectedIndex >= 0 ? AutoSubtitles.Languages[language.SelectedIndex].Id : "auto";
        List<string> labels = [];

        void Show(IReadOnlyList<SubtitleCue> shown)
        {
            labels = [.. shown.Select(c => $"{AutoSubtitles.Range(c.Start, c.End)}   {c.Text.Replace('\n', ' ')}")];
            list.ItemsSource = labels;
            AnalysisUi.DrawRanges(bar, clipSeconds,
                shown.Select(c => new TimeRange((c.Start - clip.Start).ToSeconds(), (c.End - clip.Start).ToSeconds())),
                AnalysisUi.Color(0xF2, 0xC1, 0x4E));
        }

        async Task Transcribe()
        {
            running = true;
            again.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false;
            progress.Visibility = Visibility.Visible;
            progress.Value = 0;
            status.Text = "Transcribiendo…";
            string wanted = Selected();
            job = null;
            timer.Start();
            try
            {
                Transcript transcript = await AutoSubtitles.RunAsync(modules, module, media.Path, window, wanted,
                    $"Subtítulos de «{media.Name}»", cancel.Token, j => job = j);
                segments = AutoSubtitles.ToSequence(clip, transcript);
                cues = AutoSubtitles.Cues(segments, frameRate);
                transcribed = wanted;
                _language = wanted;
                Show(cues);
                status.Text = cues.Count == 0
                    ? "No se oyó voz en el clip."
                    : string.Create(AutoSubtitles.Numbers, $"{cues.Count} subtítulos{(transcript.Language is { Length: > 0 } found && wanted == "auto" ? $" (idioma: {found})" : "")}. Revísalos y aplica.");
            }
            catch (OperationCanceledException)
            {
                status.Text = "Cancelado. El proyecto no cambió.";
            }
            // «Transcribir de nuevo» la llama desde un manejador async void: ningún fallo puede escapar y cerrar la app.
#pragma warning disable CA1031 // Se muestra en el diálogo.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                status.Text = ex is ModuleException error ? ModuleManager.Friendly(module, error) : "No se pudo transcribir: " + ex.Message + " El proyecto no cambió.";
            }
            finally
            {
                timer.Stop();
                progress.Visibility = Visibility.Collapsed;
                running = false;
                again.IsEnabled = Selected() != transcribed;
                dialog.IsPrimaryButtonEnabled = cues.Count > 0;
            }
        }

        language.SelectionChanged += (_, _) => again.IsEnabled = !running && Selected() != transcribed;
        Task current = Task.CompletedTask;
        again.Click += async (_, _) => await (current = Transcribe());
        list.ItemClick += (_, e) =>
        {
            int index = e.ClickedItem is string label ? labels.IndexOf(label) : -1;
            if (index >= 0 && index < cues.Count)
            {
                session.Seek(cues[index].Start);
            }
        };

        current = Transcribe();
        ContentDialogResult? answer = await DialogGate.ShowAsync(dialog);
        timer.Stop();
        if (answer != ContentDialogResult.Primary || running)
        {
            await cancel.CancelAsync();
        }

        await current;

        if (answer != ContentDialogResult.Primary || segments is not { Count: > 0 } chosen || cues.Count == 0)
        {
            return;
        }

        var proposal = new ModuleProposal(AutoSubtitles.Description, sequenceId,
            [.. cues.Select(c => new PreviewMark(PreviewMarkKind.Clip, c.Start, c.End, c.Text))],
            _ => [AutoSubtitles.Command(sequenceId, chosen, SubtitleStyles.NewSubtitle())]);
        if (services.Proposals is { } proposals)
        {
            proposals.Show(proposal);
            if (proposals.Apply())
            {
                session.Notify(NotificationSeverity.Success, Title, string.Create(AutoSubtitles.Numbers, $"Se pusieron {cues.Count} subtítulos."));
            }
        }
        else if (session.SequenceId == sequenceId)
        {
            session.Execute(proposal.CreateCommand(session.Project));
        }
    }
}
