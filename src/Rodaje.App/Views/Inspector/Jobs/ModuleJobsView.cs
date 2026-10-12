using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services.Modules;
using Rodaje.App.Shell;
using Rodaje.App.Theme;

namespace Rodaje.App.Views.Inspector.Jobs;

/// <summary>
/// Trabajos → IA: una fila por trabajo de módulo con etapa, avance, tiempo restante y cancelar; si falló, el mensaje
/// sin trazas. Se oculta mientras no hubo ninguno.
/// </summary>
internal sealed partial class ModuleJobsView : UserControl, IEditorView
{
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly Button _clear = new() { Content = "Limpiar terminados", IsEnabled = false };
    private readonly Dictionary<ModuleJob, Row> _rows = [];
    private ModuleJobList? _jobs;

    public ModuleJobsView()
    {
        var title = new TextBlock { Text = "IA", Style = Resource<Style>("PanelTitleTextStyle"), VerticalAlignment = VerticalAlignment.Center };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(_clear, 1);
        header.Children.Add(_clear);
        _clear.Click += (_, _) => _jobs?.ClearFinished();
        var root = new StackPanel { Spacing = 8, Padding = new Thickness(12, 10, 12, 4) };
        root.Children.Add(header);
        root.Children.Add(_list);
        Content = root;
        Visibility = Visibility.Collapsed;
    }

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _jobs = services.Modules?.Jobs;
        if (_jobs is not null)
        {
            _jobs.Changed += (_, _) => Show();
        }
    }

    private void Show()
    {
        if (_jobs is not { } jobs)
        {
            return;
        }

        IReadOnlyList<ModuleJob> current = jobs.Jobs;
        foreach (ModuleJob gone in _rows.Keys.Except(current).ToArray())
        {
            _list.Children.Remove(_rows[gone].Root);
            _rows.Remove(gone);
        }

        for (int i = 0; i < current.Count; i++)
        {
            if (!_rows.TryGetValue(current[i], out Row? row))
            {
                row = new Row(current[i]);
                _rows.Add(current[i], row);
                _list.Children.Insert(Math.Min(i, _list.Children.Count), row.Root);
            }

            row.Update();
        }

        Visibility = current.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _clear.IsEnabled = current.Any(j => j.IsFinished);
    }

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];

    private sealed class Row
    {
        private readonly ModuleJob _job;
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly ProgressBar _bar = new() { Maximum = 1 };
        private readonly Button _cancel = new() { Content = "", FontSize = ThemeResources.Size("CaptionFontSize"), VerticalAlignment = VerticalAlignment.Top };

        public Row(ModuleJob job)
        {
            _job = job;
            var title = new TextBlock { Text = job.Title, Style = Resource<Style>("BodyTextStyle"), TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(title, job.ModuleName);
            _status.Style = Resource<Style>("CaptionTextStyle");
            _cancel.Style = Resource<Style>("ToolButtonStyle");
            ToolTipService.SetToolTip(_cancel, "Cancelar");
            _cancel.Click += (_, _) => job.Cancel();
            var text = new StackPanel { Spacing = 4 };
            text.Children.Add(title);
            text.Children.Add(_status);
            text.Children.Add(_bar);
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(text);
            Grid.SetColumn(_cancel, 1);
            grid.Children.Add(_cancel);
            Root = grid;
        }

        public Grid Root { get; }

        public void Update()
        {
            _status.Text = _job.StatusText;
            _status.Foreground = _job.State == ModuleJobState.Failed ? Resource<Brush>("MeterHighBrush") : Resource<Brush>("TextMutedBrush");
            _bar.Value = _job.Progress;
            _bar.IsIndeterminate = _job.State == ModuleJobState.Waiting;
            _bar.Visibility = _cancel.Visibility = _job.IsFinished ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
