using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.App.Theme;
using Rodaje.App.Views.Shared;
using Rodaje.Export;
using Rodaje.Media.Video;

namespace Rodaje.App.Views.Export;

/// <summary>
/// Trabajos → Exportaciones: una tarjeta por exportación con miniatura, avance, fps, tiempo restante y cancelar; al
/// terminar, abrir la carpeta o reproducir; si falla, el error en español con su acción. Avisa también en la barra de
/// la ventana al terminar, porque el panel puede no estar a la vista.
/// </summary>
internal sealed partial class ExportJobsView : UserControl, IEditorView
{
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly TextBlock _empty = new()
    {
        Text = "No hay exportaciones. Usa EXPORTAR (Ctrl+E) para crear el video terminado; puedes seguir editando mientras tanto.",
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly Button _clear = new() { Content = "Limpiar terminadas", IsEnabled = false };
    private readonly ToggleButton _pause = new()
    {
        Content = "Pausar la cola",
        BorderThickness = new Thickness(1),
        BorderBrush = Resource<Brush>("ControlBorderBrush"),
        Padding = new Thickness(12, 0, 12, 0),
        MinHeight = 26,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Dictionary<ExportEntry, Card> _cards = [];
    private EditorServices? _services;
    private ExportJobList? _jobs;

    public ExportJobsView()
    {
        _empty.Style = Resource<Style>("CaptionTextStyle");
        var title = new TextBlock { Text = "Exportaciones", Style = Resource<Style>("PanelTitleTextStyle"), VerticalAlignment = VerticalAlignment.Center };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(_pause, 1);
        header.Children.Add(_pause);
        ToolTipService.SetToolTip(_pause, "En pausa, la exportación en marcha termina pero no empieza ninguna otra");
        _pause.Click += (_, _) =>
        {
            if (_jobs is not null)
            {
                _jobs.IsPaused = _pause.IsChecked == true;
            }
        };
        Grid.SetColumn(_clear, 2);
        header.Children.Add(_clear);
        _clear.Click += (_, _) => _jobs?.ClearFinished();
        var root = new StackPanel { Spacing = 8, Padding = new Thickness(12, 10, 12, 12) };
        root.Children.Add(header);
        root.Children.Add(_empty);
        root.Children.Add(_list);
        Content = root;
    }

    /// <summary>Cambió cuántas exportaciones hay en curso.</summary>
    public event EventHandler<int>? ActiveCountChanged;

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _jobs = services.Exports;
        if (_jobs is null)
        {
            return;
        }

        _jobs.Changed += (_, _) => Show();
        _jobs.Finished += (_, entry) => Announce(entry);
        Show();
    }

    private void Show()
    {
        if (_jobs is not { } jobs)
        {
            return;
        }

        foreach (ExportEntry gone in _cards.Keys.Except(jobs.Entries).ToArray())
        {
            _list.Children.Remove(_cards[gone].Root);
            _cards.Remove(gone);
        }

        for (int i = 0; i < jobs.Entries.Count; i++)
        {
            ExportEntry entry = jobs.Entries[i];
            if (!_cards.TryGetValue(entry, out Card? card))
            {
                card = new Card(this, entry);
                _cards.Add(entry, card);
                _list.Children.Insert(Math.Min(i, _list.Children.Count), card.Root);
            }

            card.Update();
        }

        UIElement[] order = [.. jobs.Entries.Select(e => (UIElement)_cards[e].Root)];
        if (!order.SequenceEqual(_list.Children))
        {
            _list.Children.Clear();
            foreach (UIElement root in order)
            {
                _list.Children.Add(root);
            }
        }

        _pause.IsChecked = jobs.IsPaused;
        _pause.Content = jobs.IsPaused ? "Reanudar la cola" : "Pausar la cola";

        _empty.Visibility = jobs.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _clear.IsEnabled = jobs.Entries.Any(e => e.IsFinished);
        ActiveCountChanged?.Invoke(this, jobs.ActiveCount);
    }

    private void Announce(ExportEntry entry)
    {
        if (_services is not { } services)
        {
            return;
        }

        EditorSession session = services.Session;
        switch (entry.Progress.State)
        {
            case ExportJobState.Completed:
                session.Notify(NotificationSeverity.Success, "Exportación terminada",
                    $"«{entry.Title}» está listo ({ExportText.Duration(entry.Progress.Elapsed)})." + (entry.Job?.MeasuredNote is { } note ? $" {note}." : ""),
                    new NotificationAction("Abrir carpeta", () => OpenFolder(entry)));
                break;
            case ExportJobState.Failed when entry.Failure is { } failure:
                ExportErrorView error = ExportErrorText.For(failure);
                session.Notify(NotificationSeverity.Error, error.Title, error.Message,
                    error.ActionText is { } text ? new NotificationAction(text, () => RunAction(entry, error.Action)) : null);
                break;
        }
    }

    private void RunAction(ExportEntry entry, ExportErrorAction action)
    {
        if (_services is not { } services || _jobs is not { } jobs)
        {
            return;
        }

        switch (action)
        {
            case ExportErrorAction.Retry:
                jobs.Retry(entry, current: services.Session.Project);
                break;
            case ExportErrorAction.ContinueWithout:
                jobs.Retry(entry, continueWithoutMissing: true, current: services.Session.Project);
                break;
            case ExportErrorAction.ChooseOutput or ExportErrorAction.ChangeSettings:
                services.Shortcuts.Invoke(ActionIds.Export);
                break;
            case ExportErrorAction.OpenPlugins:
                services.Shell.ShowPage(ShellPage.Plugins);
                break;
        }
    }

    private void OpenFolder(ExportEntry entry)
    {
        if (!ExportEntry.OutputExists(entry.OutputPath))
        {
            NotWritten(entry);
            return;
        }

        using Process? _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Path.TrimEndingDirectorySeparator(entry.OutputPath)}\"") { UseShellExecute = false });
    }

    // Una secuencia de imágenes es una carpeta: el shell la abre en el Explorador.
    private void Play(ExportEntry entry)
    {
        if (!ExportEntry.OutputExists(entry.OutputPath))
        {
            NotWritten(entry);
            return;
        }

        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo(entry.OutputPath) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _services?.Session.Notify(NotificationSeverity.Warning, "No se pudo abrir", ex.Message);
        }
    }

    private void NotWritten(ExportEntry entry) => _services?.Session.Notify(NotificationSeverity.Warning, "No está el archivo",
        _jobs?.Runner.IsSimulation == true
            ? "Esta exportación fue una simulación: no se escribió ningún archivo."
            : $"«{entry.Title}» ya no está en su carpeta; quizá se movió o se borró.");

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];

    /// <summary>La tarjeta de una exportación; se actualiza en sitio para no rearmar la lista diez veces por segundo.</summary>
    private sealed class Card
    {
        private readonly ExportJobsView _owner;
        private readonly ExportEntry _entry;
        private readonly Image _thumbnail = new() { Width = 96, Height = 54, Stretch = Stretch.Uniform };
        private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        private readonly ProgressBar _bar = new() { Maximum = 1, Margin = new Thickness(0, 4, 0, 0) };
        private readonly Button _cancel = new() { Content = "", FontSize = ThemeResources.Size("CaptionFontSize") };
        private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        private BgraImage? _shownThumbnail;
        private ExportJobState? _shownState;
        private int _shownPosition = -1;

        public Card(ExportJobsView owner, ExportEntry entry)
        {
            _owner = owner;
            _entry = entry;
            _title.Style = Resource<Style>("BodyTextStyle");
            _title.Text = entry.Title;
            ToolTipService.SetToolTip(_title, entry.OutputPath);
            _status.Style = Resource<Style>("CaptionTextStyle");
            _error.Style = Resource<Style>("CaptionTextStyle");
            _error.Foreground = Resource<Brush>("MeterHighBrush");
            _cancel.Style = Resource<Style>("ToolButtonStyle");
            ToolTipService.SetToolTip(_cancel, "Cancelar la exportación");
            _cancel.Click += (_, _) => owner._jobs?.Cancel(entry);

            var thumbFrame = new Border
            {
                Width = 96, Height = 54, Background = Resource<Brush>("BackgroundBrush"), CornerRadius = ThemeResources.Corner("ControlCornerRadius"),
                Child = _thumbnail, VerticalAlignment = VerticalAlignment.Top,
            };
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(_title);
            text.Children.Add(_status);
            text.Children.Add(_bar);
            text.Children.Add(_error);
            text.Children.Add(_actions);

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(thumbFrame);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Grid.SetColumn(_cancel, 2);
            _cancel.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(_cancel);
            Root = new Border
            {
                Child = grid, Padding = new Thickness(10, 8, 6, 8), CornerRadius = ThemeResources.Corner("ControlCornerRadius"), BorderThickness = new Thickness(1),
                Background = Resource<Brush>("ControlBrush"), BorderBrush = Resource<Brush>("StrokeBrush"),
            };
        }

        public Border Root { get; }

        public void Update()
        {
            ExportProgress progress = _entry.Progress;
            int position = _entry.Job is null ? _owner._jobs?.QueuePosition(_entry) ?? 0 : 0;
            _status.Text = position > 0
                ? $"En cola · {position}.º" + (_owner._jobs?.IsPaused == true ? " · la cola está en pausa" : "")
                : ExportText.Status(progress) + (progress.State == ExportJobState.Completed && _entry.Job?.MeasuredNote is { } note ? $" · {note}" : "");
            _bar.Value = progress.Fraction;
            _bar.IsIndeterminate = progress.State is ExportJobState.Preparing or ExportJobState.MeasuringLoudness or ExportJobState.MixingAudio
                or ExportJobState.Finalizing;
            _bar.Visibility = progress.State.IsFinished() ? Visibility.Collapsed : Visibility.Visible;
            _cancel.Visibility = progress.State.IsFinished() ? Visibility.Collapsed : Visibility.Visible;
            if (_entry.Thumbnail is { } thumbnail && !ReferenceEquals(thumbnail, _shownThumbnail))
            {
                _shownThumbnail = thumbnail;
                _thumbnail.Source = BgraBitmaps.From(thumbnail);
            }

            if (_shownState != progress.State || _shownPosition != position)
            {
                _shownState = progress.State;
                _shownPosition = position;
                ShowActions(progress.State);
            }
        }

        private void ShowActions(ExportJobState state)
        {
            _actions.Children.Clear();
            _error.Visibility = Visibility.Collapsed;
            switch (state)
            {
                case ExportJobState.Completed:
                    AddAction("Abrir carpeta", () => _owner.OpenFolder(_entry));
                    AddAction("Reproducir", () => _owner.Play(_entry));
                    break;
                case ExportJobState.Failed:
                    if (_entry.Failure is { } failure)
                    {
                        ExportErrorView error = ExportErrorText.For(failure);
                        _error.Text = $"{error.Title}. {error.Message}";
                        _error.Visibility = Visibility.Visible;
                        if (error.ActionText is { } text)
                        {
                            AddAction(text, () => _owner.RunAction(_entry, error.Action));
                        }
                    }

                    break;
                case ExportJobState.Queued when _shownPosition > 0 && _owner._jobs is { } jobs:
                    int waiting = jobs.Waiting.Count;
                    if (_shownPosition > 1)
                    {
                        AddAction("Empezar antes", () => jobs.Move(_entry, earlier: true));
                    }

                    if (_shownPosition < waiting)
                    {
                        AddAction("Empezar después", () => jobs.Move(_entry, earlier: false));
                    }

                    return;
                case ExportJobState.Canceled:
                    AddAction("Volver a exportar", () => _owner._jobs?.Retry(_entry, current: _owner._services?.Session.Project));
                    break;
                default:
                    return;
            }

            AddAction("Quitar", () => _owner._jobs?.Remove(_entry));
        }

        private void AddAction(string text, Action run)
        {
            var button = new Button { Content = text, Padding = new Thickness(8, 3, 8, 3) };
            button.Click += (_, _) => run();
            _actions.Children.Add(button);
        }
    }
}
