using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Translate;
using Rodaje.App.Shell;
using Rodaje.App.Views.Analysis;
using Rodaje.Core.Model;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Views.Translate;

/// <summary>
/// «Traducir pista»: elige la pista de subtítulos (la del clip seleccionado por defecto), el idioma de origen (o
/// detectarlo) y el de destino; traduce con <c>modulo-traducir</c> como un trabajo cancelable y crea una pista nueva
/// encima con los mismos tiempos y estilo, en un paso del historial. Si algo falla, el proyecto no cambia.
/// </summary>
internal static class TranslateDialog
{
    public const string ActionId = "tools.translateTrack";

    private static string _lastTarget = "en";

    public static async Task ShowAsync(EditorServices services)
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
        var track = new ComboBox { Header = "Pista", Width = AnalysisUi.PreviewWidth, DisplayMemberPath = nameof(TrackChoice.Label) };
        TrackChoice[] trackChoices = [.. tracks.Select(t => new TrackChoice(t.Id, Fmt($"{(t.Name.Length > 0 ? t.Name : "Pista")} ({SubtitleTrackTranslation.Collect(t).Count} textos)")))];
        track.ItemsSource = trackChoices;
        string? preferred = session.TranslatableTrack()?.Id;
        track.SelectedItem = trackChoices.FirstOrDefault(c => c.TrackId == preferred) ?? trackChoices[0];

        TranslationLanguage detect = new("auto", "Detectar");
        var source = new ComboBox { Header = "Idioma del original", Width = AnalysisUi.PreviewWidth, DisplayMemberPath = nameof(TranslationLanguage.Name) };
        source.ItemsSource = new[] { detect }.Concat(SubtitleTrackTranslation.Languages).ToArray();
        source.SelectedIndex = 0;
        var target = new ComboBox { Header = "Traducir a", Width = AnalysisUi.PreviewWidth, DisplayMemberPath = nameof(TranslationLanguage.Name) };
        target.ItemsSource = SubtitleTrackTranslation.Languages;
        target.SelectedItem = SubtitleTrackTranslation.Languages.FirstOrDefault(l => l.Code == _lastTarget) ?? SubtitleTrackTranslation.Languages[1];

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
        panel.Children.Add(row);

        ContentDialog dialog = AnalysisUi.Dialog(root, "Traducir pista", "Traducir", panel);
        using var cancel = new CancellationTokenSource();
        bool running = false;

        void Refresh() => dialog.IsPrimaryButtonEnabled = !running && track.SelectedItem is TrackChoice && target.SelectedItem is TranslationLanguage to
            && (source.SelectedItem as TranslationLanguage)?.Code != to.Code;

        track.SelectionChanged += (_, _) => Refresh();
        source.SelectionChanged += (_, _) => Refresh();
        target.SelectionChanged += (_, _) => Refresh();
        Refresh();

        (string TrackId, TranslationLanguage Target, TrackTranslation Result)? done = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            running = true;
            busy.IsActive = true;
            Refresh();
            try
            {
                var chosenTrack = (TrackChoice)track.SelectedItem;
                var to = (TranslationLanguage)target.SelectedItem;
                string? from = source.SelectedItem is TranslationLanguage { Code: not "auto" } s ? s.Code : null;
                IReadOnlyList<SubtitleText> texts = session.Sequence.FindTrack(chosenTrack.TrackId) is { } t ? SubtitleTrackTranslation.Collect(t) : [];
                status.Text = Fmt($"Traduciendo {texts.Count} textos al {to.Name.ToLower(CultureInfo.CurrentCulture)}… (sigue en Trabajos si cierras)");
                TrackTranslation result = await translator.TranslateAsync(texts, from, to.Code, "Traducir subtítulos", cancel.Token);
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
                args.Cancel = true;
            }
            finally
            {
                running = false;
                busy.IsActive = false;
                Refresh();
                deferral.Complete();
            }
        };

        ContentDialogResult? answer = await DialogGate.ShowAsync(dialog);
        await cancel.CancelAsync();
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

    private static string Fmt(FormattableString s) => s.ToString(SpanishNumbers.Format);

    private sealed record TrackChoice(string TrackId, string Label);
}
