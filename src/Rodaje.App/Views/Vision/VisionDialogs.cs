using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Services.Modules;
using Rodaje.App.Shell;
using Rodaje.App.Views.Analysis;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Views.Vision;

/// <summary>
/// «Quitar fondo» y «Reencuadre automático» (modulo-vision): eligen las opciones, corren el trabajo con avance y cancelar y,
/// al aplicar, dejan la máscara de imagen o los keyframes de la transformación en un solo paso del historial.
/// </summary>
internal static class VisionDialogs
{
    public const string RemoveBackgroundTitle = "Quitar fondo";
    public const string ReframeTitle = "Reencuadre automático";

    /// <summary>Formas del reencuadre: nombre, ancho y alto.</summary>
    public static IReadOnlyList<(string Label, int Width, int Height)> ReframeAspects { get; } =
        [("9:16 · vertical", 9, 16), ("1:1 · cuadrado", 1, 1), ("4:5 · retrato", 4, 5)];

    /// <summary>El clip de video elegido que se puede analizar, o el motivo para no hacerlo.</summary>
    public static (Clip? Clip, MediaRef? Media, string? Reason) Target(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        foreach (string id in session.SelectedClipIds)
        {
            if (session.Sequence.FindClip(id) is { } found && found.Clip.Generator is null
                && session.Project.FindMedia(found.Clip.MediaId) is { HasVideo: true, FrameRate: not null, Width: > 0, Height: > 0 } media)
            {
                return found.Clip.Speed is { Freeze: true }
                    ? (null, null, "Un fotograma congelado no se puede analizar: hazlo sobre el clip original.")
                    : QuickTools.Locked(session.Sequence, found.Clip.Id) is { } locked ? (null, null, locked) : (found.Clip, media, null);
            }
        }

        return (null, null, "Selecciona un clip de video.");
    }

    public static async Task RemoveBackgroundAsync(EditorServices services, LongActionScope scope)
    {
        EditorSession session = services.Session;
        (Clip? clip, MediaRef? media, string? reason) = Target(session);
        if (clip is null || media is null || services.Shell.XamlRoot is not { } root)
        {
            session.Notify(NotificationSeverity.Informational, RemoveBackgroundTitle, reason ?? "Selecciona un clip de video.");
            return;
        }

        if (services.Modules?.FindByFunction(VisionTools.RemoveBackgroundFunction) is not { } module)
        {
            session.Notify(NotificationSeverity.Informational, RemoveBackgroundTitle, "Hace falta el módulo «Visión con IA». Instálalo desde Complementos › Módulos de IA.");
            return;
        }

        (FfmpegExecutable? ffmpeg, string? why) = await Task.Run(() => (FfmpegExecutable.TryLocate(out string? w), w));
        if (ffmpeg is null)
        {
            session.Notify(NotificationSeverity.Error, RemoveBackgroundTitle, why ?? "No se encontró el ffmpeg.exe LGPL.");
            return;
        }

        session.Pause();
        (_, int frames) = VisionTools.FrameSpan(clip, media.FrameRate!.Value);
        var panel = new StackPanel { Spacing = 8, Width = AnalysisUi.PreviewWidth };
        panel.Children.Add(new TextBlock
        {
            Text = string.Create(SpanishNumbers.Format, $"«{media.Name}»: {frames} fotogramas. El sujeto queda y el fondo se vuelve transparente; debajo se ve la pista de abajo."),
            TextWrapping = TextWrapping.Wrap,
        });
        var quality = new ComboBox { Header = "Calidad", Width = AnalysisUi.PreviewWidth, SelectedIndex = 0 };
        quality.Items.Add("Normal · personas, rápido (MODNet)");
        quality.Items.Add("Alta · bordes finos y objetos, lento (BiRefNet)");
        panel.Children.Add(quality);
        var advice = new Shared.AiAdvicePanel();
        panel.Children.Add(advice.Panel);
        void Estimate() => advice.Show(VisionTools.EstimateRemoveBackground(services.Modules!, clip, media.FrameRate!.Value, quality.SelectedIndex == 1),
            () => quality.SelectedIndex = 0);
        Estimate();
        quality.SelectionChanged += (_, _) => Estimate();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Maximum = 1, Width = AnalysisUi.PreviewWidth, Visibility = Visibility.Collapsed };
        panel.Children.Add(progress);
        panel.Children.Add(status);
        ContentDialog dialog = AnalysisUi.Dialog(root, RemoveBackgroundTitle, "Aplicar", panel, AnalysisApplyPhase.NeedsAnalysis);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(scope.Token);
        ModuleJob? job = null;
        MatteVideo? matte = null;
        Task work = Task.CompletedTask;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        void ShowActivity()
        {
            ModuleActivityView view = ModuleActivity.Describe(job, cancel.IsCancellationRequested, "Recortando el sujeto");
            progress.IsIndeterminate = view.IsIndeterminate;
            progress.Value = view.Progress;
            status.Text = view.Text;
            scope.Report(view.IsIndeterminate ? null : view.Progress, view.Text);
        }

        timer.Tick += (_, _) => ShowActivity();
        dialog.PrimaryButtonClick += (_, args) => Rodaje.App.Services.Crashes.UiTask.Run(AnalyzeBeforeApply(args));
        async Task AnalyzeBeforeApply(ContentDialogButtonClickEventArgs args)
        {
            // Apagado mientras corre el módulo: un segundo clic no lanza otro Run. Si falló, vuelve para reintentar.
            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            try { if (matte is null) { work = Run(); await work; } args.Cancel = matte is null; }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        }
        ContentDialogResult? answer = await DialogGate.ShowAsync(dialog);
        timer.Stop();
        if (answer != ContentDialogResult.Primary)
        {
            await cancel.CancelAsync();
        }

        await (cancel.IsCancellationRequested ? services.LongActions.ShowCancellingAsync(RemoveBackgroundTitle, work, scope) : work);
        if (answer != ContentDialogResult.Primary || matte is null)
        {
            Delete(matte?.Path);
            return;
        }

        string reference = LutReference.ForFile(matte.Path);
        var proposal = new ModuleProposal(RemoveBackgroundTitle, session.SequenceId,
            [new PreviewMark(PreviewMarkKind.Range, clip.Start, clip.End, RemoveBackgroundTitle)],
            _ => [Edits.SetImageMask(session.SequenceId, clip.Id, reference, matte.Start)]);
        if (Apply(services, proposal))
        {
            (services.Content as EffectContent)?.ForgetMatte(matte.Path);
            session.Notify(NotificationSeverity.Success, RemoveBackgroundTitle, "Listo: la máscara está en la pestaña Máscaras (invertir, opacidad).");
        }
        else
        {
            Delete(matte.Path);
        }

        async Task Run()
        {
            quality.IsEnabled = false;
            progress.Visibility = Visibility.Visible;
            ShowActivity();
            timer.Start();
            try
            {
                matte = await VisionTools.RemoveBackgroundAsync(services.Modules, module, ffmpeg, media.Path, clip, media.FrameRate!.Value,
                    quality.SelectedIndex == 1, AnalysisFolder(services), $"Quitar fondo de «{media.Name}»", cancel.Token, j => job = j);
                status.Text = "Listo. Aplica para ver el resultado; un Ctrl+Z lo quita.";
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (OperationCanceledException)
            {
                status.Text = "Cancelado. El proyecto no cambió.";
            }
            catch (Exception ex) when (ex is ModuleException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                status.Text = ex is ModuleException error ? ModuleManager.Friendly(module, error) : "No se pudo quitar el fondo: " + ex.Message + " El proyecto no cambió.";
            }
            finally
            {
                timer.Stop();
                progress.Visibility = Visibility.Collapsed;
                quality.IsEnabled = matte is null;
            }
        }
    }

    public static async Task ReframeAsync(EditorServices services, LongActionScope scope)
    {
        EditorSession session = services.Session;
        (Clip? clip, MediaRef? media, string? reason) = Target(session);
        if (clip is null || media is null || services.Shell.XamlRoot is not { } root)
        {
            session.Notify(NotificationSeverity.Informational, ReframeTitle, reason ?? "Selecciona un clip de video.");
            return;
        }

        if (services.Modules?.FindByFunction(VisionTools.ReframeFunction) is not { } module)
        {
            session.Notify(NotificationSeverity.Informational, ReframeTitle, "Hace falta el módulo «Visión con IA». Instálalo desde Complementos › Módulos de IA.");
            return;
        }

        session.Pause();
        var panel = new StackPanel { Spacing = 8, Width = AnalysisUi.PreviewWidth };
        panel.Children.Add(new TextBlock
        {
            Text = "Sigue al sujeto con un recorte que se mueve suave: queda quieto mientras el sujeto cabe y se desliza cuando sale.",
            TextWrapping = TextWrapping.Wrap,
        });
        var aspect = new ComboBox { Header = "Formato", Width = AnalysisUi.PreviewWidth };
        foreach ((string label, _, _) in ReframeAspects)
        {
            aspect.Items.Add(label);
        }

        int current = ReframeAspects.ToList().FindIndex(a => a.Width * session.Sequence.Height == a.Height * session.Sequence.Width);
        aspect.SelectedIndex = Math.Max(0, current);
        panel.Children.Add(aspect);
        var subject = new ComboBox { Header = "Seguir", Width = AnalysisUi.PreviewWidth, SelectedIndex = 0 };
        subject.Items.Add("Rostros (YuNet)");
        subject.Items.Add("Personas de cuerpo entero (RT-DETRv2)");
        panel.Children.Add(subject);
        var advice = new Shared.AiAdvicePanel();
        advice.Show(VisionTools.EstimateReframe(services.Modules!, clip));
        panel.Children.Add(advice.Panel);
        double smoothing = 0.5;
        Slider? smooth = null;
        smooth = AnalysisUi.Slider(panel, "Suavidad", 0, 1, smoothing, 0.05, v => string.Create(SpanishNumbers.Format, $"{v * 100:0} %"),
            () => smoothing = smooth!.Value);
        var progress = new ProgressBar { Maximum = 1, Width = AnalysisUi.PreviewWidth, Visibility = Visibility.Collapsed };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(progress);
        panel.Children.Add(status);
        ContentDialog dialog = AnalysisUi.Dialog(root, ReframeTitle, "Aplicar", panel, AnalysisApplyPhase.NeedsAnalysis);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(scope.Token);
        ModuleJob? job = null;
        ReframePlan? plan = null;
        Task work = Task.CompletedTask;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        void ShowActivity()
        {
            ModuleActivityView view = ModuleActivity.Describe(job, cancel.IsCancellationRequested, "Buscando al sujeto");
            progress.IsIndeterminate = view.IsIndeterminate;
            progress.Value = view.Progress;
            status.Text = view.Text;
            scope.Report(view.IsIndeterminate ? null : view.Progress, view.Text);
        }

        timer.Tick += (_, _) => ShowActivity();
        dialog.PrimaryButtonClick += (_, args) => Rodaje.App.Services.Crashes.UiTask.Run(AnalyzeBeforeApply(args));
        async Task AnalyzeBeforeApply(ContentDialogButtonClickEventArgs args)
        {
            // Apagado mientras corre el módulo: un segundo clic no lanza otro Run. Si falló, vuelve para reintentar.
            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            try { if (plan is null) { work = Run(); await work; } args.Cancel = plan is null; }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        }
        ContentDialogResult? answer = await DialogGate.ShowAsync(dialog);
        timer.Stop();
        if (answer != ContentDialogResult.Primary)
        {
            await cancel.CancelAsync();
        }

        await (cancel.IsCancellationRequested ? services.LongActions.ShowCancellingAsync(ReframeTitle, work, scope) : work);
        if (answer != ContentDialogResult.Primary || plan is null)
        {
            return;
        }

        (_, int w, int h) = ReframeAspects[Math.Max(0, aspect.SelectedIndex)];
        Sequence sequence = session.Sequence;
        bool reshape = w * sequence.Height != h * sequence.Width;
        (int width, int height) = reshape ? ProjectFormats.Reshape(new AspectChoice("", "", w, h), sequence.Width, sequence.Height) : (sequence.Width, sequence.Height);
        TransformKey[] keys = [.. plan.Keys.Select(k => VisionTools.ToTransform(k, media.Width, media.Height, width, height))];
        string sequenceId = session.SequenceId;
        Rational rate = sequence.FrameRate;
        var proposal = new ModuleProposal(ReframeTitle, sequenceId,
            [.. keys.Select(k => new PreviewMark(PreviewMarkKind.Keyframe, clip.Start + (k.Time - clip.SourceIn), clip.Start + (k.Time - clip.SourceIn)))],
            _ => reshape
                ? [SequenceFormatEdit.Command(sequenceId, width, height, rate, ClipFit.Fit), Edits.SetTransformKeys(sequenceId, clip.Id, keys)]
                : [Edits.SetTransformKeys(sequenceId, clip.Id, keys)]);
        if (Apply(services, proposal))
        {
            session.Notify(NotificationSeverity.Success, ReframeTitle, string.Create(SpanishNumbers.Format,
                $"{keys.Length} keyframes de posición y escala{(reshape ? $"; el proyecto pasó a {ProjectFormats.AspectName(width, height)}" : "")}."));
        }

        async Task Run()
        {
            aspect.IsEnabled = false;
            subject.IsEnabled = false;
            progress.Visibility = Visibility.Visible;
            ShowActivity();
            timer.Start();
            (_, int aw, int ah) = ReframeAspects[Math.Max(0, aspect.SelectedIndex)];
            try
            {
                plan = await VisionTools.ReframeAsync(services.Modules, module, media.Path, clip, (double)aw / ah, subject.SelectedIndex == 0, smoothing,
                    $"Reencuadre de «{media.Name}»", cancel.Token, j => job = j);
                status.Text = double.IsNaN(plan.Inside)
                    ? string.Create(SpanishNumbers.Format, $"{plan.Keys.Count} keyframes. Aplica para verlo; un Ctrl+Z lo quita.")
                    : string.Create(SpanishNumbers.Format, $"{plan.Keys.Count} keyframes; el sujeto queda dentro del recorte en el {plan.Inside * 100:0} % de los fotogramas. Aplica para verlo; un Ctrl+Z lo quita.");
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (OperationCanceledException)
            {
                status.Text = "Cancelado. El proyecto no cambió.";
            }
            catch (Exception ex) when (ex is ModuleException or IOException or InvalidOperationException)
            {
                status.Text = ex is ModuleException error ? ModuleManager.Friendly(module, error) : "No se pudo analizar: " + ex.Message + " El proyecto no cambió.";
            }
            finally
            {
                timer.Stop();
                progress.Visibility = Visibility.Collapsed;
                aspect.IsEnabled = subject.IsEnabled = plan is null;
            }
        }
    }

    /// <summary>Carpeta de los medios que crea Rodaje (la elegida en «Carpeta de medios generados», D-034).</summary>
    private static string AnalysisFolder(EditorServices services) =>
        GeneratedMedia.Ensure(services.Session.FilePath, services.Settings.Current.GeneratedMediaFolder);

    private static bool Apply(EditorServices services, ModuleProposal proposal)
    {
        if (services.Proposals is { } proposals)
        {
            proposals.Show(proposal);
            return proposals.Apply();
        }

        return services.Session.SequenceId == proposal.SequenceId && services.Session.Execute(proposal.CreateCommand(services.Session.Project));
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
