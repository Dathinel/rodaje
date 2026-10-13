using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Services.Translate;
using Rodaje.App.Shell;
using Rodaje.App.Views.Analysis;
using Rodaje.Core.Model;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Views.Translate;

/// <summary>
/// «Traducir pista»: elige la pista de subtítulos (la del clip seleccionado por defecto), el idioma de origen (o
/// detectarlo) y el de destino; traduce con <c>modulo-traducir</c> como un trabajo cancelable y crea una pista nueva
/// encima con los mismos tiempos y estilo, en un paso del historial. Si algo falla, el proyecto no cambia. Al cerrarlo con
/// la traducción en marcha, la barra de avisos dice «Cancelando…» hasta que el módulo lo confirma (<see cref="DialogWork"/>).
/// </summary>
internal static class TranslateDialog
{
    public const string ActionId = "tools.translateTrack";

    private static string _lastTarget = "en";

    public static async Task ShowAsync(EditorServices services, LongActionScope? scope = null)
    {
        EditorSession session = services.Session;
        if (services.Shell.XamlRoot is not { } root)
        {
            return;
        }

        if (services.Modules is not { } modules || !new ModuleSubtitleTranslator(modules).IsInstalled)
        {
            session.Notify(NotificationSeverity.Informational, "Traducir pista", ModuleSubtitleTranslator.NotInstalled);
            return;
        }

        IReadOnlyList<Track> tracks = SubtitleTrackTranslation.TranslatableTracks(session.Sequence);
        if (tracks.Count == 0)
        {
            session.Notify(NotificationSeverity.Informational, "Traducir pista", "No hay subtítulos ni textos que traducir: crea antes los subtítulos.");
            return;
        }

        var translator = new ModuleSubtitleTranslator(modules);
        var panel = new StackPanel { Spacing = 8, Width = AnalysisUi.PreviewWidth };
        // Los desplegables llevan textos y se elige por posición: mostrar una propiedad del elemento enlaza por reflexión, que con Native AOT
        // no encuentra la propiedad de un record de C# y deja los elementos en blanco.
        var track = new ComboBox { Header = "Pista", Width = AnalysisUi.PreviewWidth };
        TrackChoice[] trackChoices = [.. tracks.Select(t => new TrackChoice(t.Id, Fmt($"{(t.Name.Length > 0 ? t.Name : "Pista")} ({SubtitleTrackTranslation.Collect(t).Count} textos)")))];
        track.ItemsSource = trackChoices.Select(c => c.Label).ToArray();
        string? preferred = session.TranslatableTrack()?.Id;
        track.SelectedIndex = Math.Max(0, Array.FindIndex(trackChoices, c => c.TrackId == preferred));

        TranslationLanguage detect = new("auto", "Detectar");
        TranslationLanguage[] sources = [detect, .. SubtitleTrackTranslation.Languages];
        IReadOnlyList<TranslationLanguage> targets = SubtitleTrackTranslation.Languages;
        var source = new ComboBox { Header = "Idioma del original", Width = AnalysisUi.PreviewWidth };
        source.ItemsSource = sources.Select(l => l.Name).ToArray();
        source.SelectedIndex = 0;
        var target = new ComboBox { Header = "Traducir a", Width = AnalysisUi.PreviewWidth };
        target.ItemsSource = targets.Select(l => l.Name).ToArray();
        int last = targets.ToList().FindIndex(l => l.Code == _lastTarget);
        target.SelectedIndex = last >= 0 ? last : 1;

        var busy = new ProgressRing { IsActive = false, Width = 20, Height = 20 };
        var status = new TextBlock
        {
            Text = "Se crea una pista nueva encima con los mismos tiempos; la original no cambia.",
            TextWrapping = TextWrapping.Wrap,
            Width = AnalysisUi.PreviewWidth - 30,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(busy);
        row.Children.Add(status);
        panel.Children.Add(track);
        panel.Children.Add(source);
        panel.Children.Add(target);
        var advice = new Shared.AiAdvicePanel();
        panel.Children.Add(advice.Panel);
        panel.Children.Add(row);

        ContentDialog dialog = AnalysisUi.Dialog(root, "Traducir pista", "Traducir", panel);
        using var work = new DialogWork();
        bool running = false;

        void Refresh() => dialog.IsPrimaryButtonEnabled = !running && Chosen(track, trackChoices) is not null && Chosen(target, targets) is { } to
            && Chosen(source, sources)?.Code != to.Code;

        void Estimate() => advice.Show(Chosen(track, trackChoices) is { } c && session.Sequence.FindTrack(c.TrackId) is { } chosen
            ? translator.Estimate(SubtitleTrackTranslation.Collect(chosen))
            : null);
        Estimate();
        track.SelectionChanged += (_, _) =>
        {
            Refresh();
            Estimate();
        };
        source.SelectionChanged += (_, _) => Refresh();
        target.SelectionChanged += (_, _) => Refresh();
        Refresh();

        (string TrackId, TranslationLanguage Target, TrackTranslation Result)? done = null;
        // La espera (GetDeferral) se pide dentro del evento, antes del primer await.
        dialog.PrimaryButtonClick += (_, args) => UiTask.Run(work.Track(TranslateAsync(args)));
        async Task TranslateAsync(ContentDialogButtonClickEventArgs args)
        {
            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            running = true;
            busy.IsActive = true;
            Refresh();
            try
            {
                TrackChoice chosenTrack = Chosen(track, trackChoices)!;
                TranslationLanguage to = Chosen(target, targets)!;
                string? from = Chosen(source, sources) is { Code: not "auto" } s ? s.Code : null;
                IReadOnlyList<SubtitleText> texts = session.Sequence.FindTrack(chosenTrack.TrackId) is { } t ? SubtitleTrackTranslation.Collect(t) : [];
                status.Text = Fmt($"Traduciendo {texts.Count} textos al {to.Name.ToLower(CultureInfo.CurrentCulture)}… (sigue en Trabajos si cierras)");
                TrackTranslation result = await translator.TranslateAsync(texts, from, to.Code, "Traducir subtítulos", work.Token);
                done = (chosenTrack.TrackId, to, result);
            }
            catch (OperationCanceledException)
            {
                status.Text = "Cancelado.";
                args.Cancel = true;
            }
#pragma warning disable CA1031 // Un fallo del módulo se explica en el diálogo; el proyecto no cambia.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                status.Text = ex is TranslationException or ModuleException ? ex.Message : "No se pudo traducir. El proyecto no cambió.";
                if (ex is not (TranslationException or ModuleException))
                {
                    UiTask.Report(ex, notify: false);
                }

                args.Cancel = true;
            }
            finally
            {
                running = false;
                busy.IsActive = false;
                Refresh();
                deferral.Complete();
            }
        }

        ContentDialogResult? answer = await DialogGate.ShowAsync(dialog);
        await work.CloseAsync(services.LongActions, "Traducir pista", scope);
        if (answer != ContentDialogResult.Primary || done is not { } d)
        {
            return;
        }

        _lastTarget = d.Target.Code;
        if (session.AddTranslatedTrack(d.TrackId, d.Result.Texts, d.Target) && d.Result.Untranslated.Count > 0)
        {
            session.Notify(NotificationSeverity.Warning, "Traducir pista",
                Fmt($"{d.Result.Untranslated.Count} textos quedaron sin traducir y conservan el original; revísalos en la pista nueva."));
        }
    }

    private static string Fmt(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private sealed record TrackChoice(string TrackId, string Label);

    /// <summary>El elemento elegido en un desplegable de textos hecho con <paramref name="items"/>, o null si no hay.</summary>
    private static T? Chosen<T>(ComboBox box, IReadOnlyList<T> items)
        where T : class =>
        box.SelectedIndex >= 0 && box.SelectedIndex < items.Count ? items[box.SelectedIndex] : null;
}
