using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Rodaje.App.Services;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Modules;
using Rodaje.App.Shell;
using Rodaje.App.Views.Shared;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Views.Analysis;

/// <summary>
/// «Reducir ruido»: limpia la voz del clip con modulo-audio-ia (DeepFilterNet3), deja escuchar A/B el original y el limpio
/// con la intensidad elegida y, al aplicar, pasa el clip al audio limpio en un paso del historial. Cancelar o cerrar deja el
/// proyecto como estaba.
/// </summary>
internal static class NoiseReductionDialog
{
    public static async Task ShowAsync(EditorServices services, LongActionScope? scope = null)
    {
        EditorSession session = services.Session;
        const string Title = "Reducir ruido";
        if (NoiseReduction.TargetClip(session.Sequence, session.SelectedClipIds, c => ClipAudioSource.HasAudio(session, c)) is not { } clip
            || services.Shell.XamlRoot is not { } root)
        {
            session.Notify(NotificationSeverity.Informational, Title, "Selecciona un clip con voz que esté en una pista de audio (o un video con su audio enlazado).");
            return;
        }

        if (services.Modules?.FindByFunction(NoiseReduction.Function) is not { } module)
        {
            session.Notify(NotificationSeverity.Informational, Title, "Hace falta el módulo «Audio con IA». Instálalo desde Complementos › Módulos de IA.");
            return;
        }

        if (clip.Speed is { Freeze: true } || session.Project.FindMedia(clip.MediaId) is not { } media)
        {
            session.Notify(NotificationSeverity.Informational, Title, "Un cuadro congelado no tiene sonido que limpiar.");
            return;
        }

        (Time Start, Time End) window = NoiseReduction.Window(clip, media.Duration);
        if (window.End - window.Start > NoiseReduction.MaxSpan)
        {
            session.Notify(NotificationSeverity.Informational, Title, "El clip dura más de una hora: córtalo y limpia cada parte.");
            return;
        }

        session.Pause();
        var panel = new StackPanel { Spacing = 4, Width = AnalysisUi.PreviewWidth };
        var status = new TextBlock { Text = "Limpiando la voz…", TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Maximum = 1, Width = AnalysisUi.PreviewWidth };
        panel.Children.Add(status);
        panel.Children.Add(progress);
        if (services.Modules is { } estimating)
        {
            var advice = new Shared.AiAdvicePanel();
            advice.Show(NoiseReduction.Estimate(estimating, window));
            panel.Children.Add(advice.Panel);
        }

        var listen = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
        var play = new Button { Content = "Escuchar" };
        var original = new ToggleButton { Content = "A: original" };
        var cleaned = new ToggleButton { Content = "B: limpio", IsChecked = true };
        listen.Children.Add(play);
        listen.Children.Add(original);
        listen.Children.Add(cleaned);
        panel.Children.Add(listen);

        NoisePreview? preview = null;
        Slider? intensity = null;
        double reduction = NoiseReduction.MaxReductionDb;
        intensity = AnalysisUi.Slider(panel, "Cuánto ruido se quita", 6, NoiseReduction.MaxReductionDb, reduction, 1,
            v => v >= NoiseReduction.MaxReductionDb ? "todo" : string.Create(SpanishNumbers.Format, $"hasta {v:0} dB"), () =>
            {
                reduction = intensity!.Value;
                preview?.ReductionDb = reduction;
            });
        intensity.IsEnabled = false;
        panel.Children.Add(new TextBlock
        {
            Text = "Con menos intensidad queda algo del ambiente y la voz suena más natural.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });
        var saveIn = new GeneratedFolderRow(services);
        panel.Children.Add(saveIn.Element);

        void Choose(bool listenOriginal)
        {
            original.IsChecked = listenOriginal;
            cleaned.IsChecked = !listenOriginal;
            preview?.ListenOriginal = listenOriginal;
        }

        original.Click += (_, _) => Choose(true);
        cleaned.Click += (_, _) => Choose(false);
        play.Click += (_, _) =>
        {
            if (preview is null)
            {
                return;
            }

            if (preview.IsPlaying)
            {
                preview.Stop();
                play.Content = "Escuchar";
            }
            else if (preview.Play(out string? problem))
            {
                play.Content = "Detener";
            }
            else
            {
                status.Text = "No hay salida de audio: " + problem;
            }
        };

        ContentDialog dialog = AnalysisUi.Dialog(root, Title, "Aplicar", panel);
        using var cancel = new CancellationTokenSource();
        ModuleJob? job = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (job is not null)
            {
                progress.Value = job.Progress;
                scope?.Report(job.Progress, "Limpiando la voz…");
                status.Text = (job.Stage ?? "Limpiando la voz") + "…";
            }
        };
        timer.Start();

        Task<CleanAudio> work = NoiseReduction.RunAsync(services.Modules, module, media.Path, window, saveIn.Folder,
            $"Reducir ruido de «{media.Name}»", cancel.Token, j => job = j);
        CleanAudio? clean = null;
        Task ready = Prepare();
        ContentDialogResult? answer = await DialogGate.ShowAsync(dialog);
        timer.Stop();
        preview?.Dispose();
        if (answer != ContentDialogResult.Primary)
        {
            await cancel.CancelAsync();
        }

        try
        {
            await ready;
        }
        catch (OperationCanceledException)
        {
        }

        if (answer != ContentDialogResult.Primary || clean is null)
        {
            Delete(clean?.Path);
            return;
        }

        string path;
        try
        {
            path = await Task.Run(() => NoiseReduction.Final(clean, reduction));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            session.Notify(NotificationSeverity.Error, Title, "No se pudo guardar el audio limpio: " + ex.Message);
            return;
        }

        if (!ReferenceEquals(path, clean.Path))
        {
            Delete(clean.Path);
        }

        try
        {
            path = MoveTo(path, saveIn.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            session.Notify(NotificationSeverity.Warning, Title, $"No se pudo mover el audio limpio a la carpeta elegida; queda en {Path.GetDirectoryName(path)}.");
        }

        MediaRef cleanMedia = NoiseReduction.Media(Guid.NewGuid().ToString("N"), clean with { Path = path }, media.Name + " (sin ruido)");
        if (session.Sequence.FindClip(clip.Id) is null || !session.Execute(NoiseReduction.Command(session.SequenceId, clip.Id, cleanMedia, clean.Offset)))
        {
            Delete(path);
        }

        async Task Prepare()
        {
            try
            {
                clean = await work;
                Time at = session.Sequence.FindClip(clip.Id) is { } found && session.Position >= found.Clip.Start && session.Position < found.Clip.End
                    ? found.Clip.ToSourceTime(session.Position)
                    : clip.SourceIn;
                preview = await Task.Run(() => NoisePreview.Load(clean, at));
                preview.ReductionDb = reduction;
                progress.Visibility = Visibility.Collapsed;
                status.Text = "Listo. Escucha A (original) y B (limpio) y elige cuánto ruido quitar.";
                listen.Visibility = Visibility.Visible;
                intensity.IsEnabled = true;
                dialog.IsPrimaryButtonEnabled = true;
                timer.Stop();
            }
            catch (Exception ex) when (ex is ModuleException or IOException or InvalidOperationException)
            {
                status.Text = ex is ModuleException error ? ModuleManager.Friendly(module, error) : "No se pudo limpiar: " + ex.Message;
                progress.Visibility = Visibility.Collapsed;
                timer.Stop();
            }
        }
    }

    /// <summary>Si la persona cambió de carpeta mientras se limpiaba, el WAV se pasa a la nueva.</summary>
    private static string MoveTo(string path, string folder)
    {
        if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        string target = Path.Combine(folder, Path.GetFileName(path));
        File.Move(path, target);
        return target;
    }

    private static void Delete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Queda en la carpeta de medios generados; no estorba.
        }
    }
}
