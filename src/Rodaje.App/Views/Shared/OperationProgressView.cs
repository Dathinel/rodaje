using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services.Progress;

namespace Rodaje.App.Views.Shared;

/// <summary>
/// La vista común del avance de una operación larga (barra de estado, diálogos y panel Trabajos): barra con porcentaje (o en
/// movimiento si no se sabe), la línea de fase y tiempo restante, el aviso «Sigue trabajando; último avance hace N s» solo
/// cuando pasan unos segundos sin avance, y «Cancelar» si la operación lo admite. Consulta el <see cref="OperationProgress"/>
/// cuatro veces por segundo mientras está a la vista, así que quien informa puede hacerlo cada cuadro sin costar nada, y
/// solo toca los controles cuyo contenido cambió (no parpadea). Los colores salen del tema (claro, oscuro y alto contraste).
/// </summary>
internal sealed partial class OperationProgressView : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly ProgressBar _bar = new() { Maximum = 1, MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _percent = new() { MinWidth = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cancel = new() { Content = "Cancelar", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _stalled = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };
    private OperationProgress? _operation;

    public OperationProgressView()
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_bar);
        Grid.SetColumn(_percent, 1);
        row.Children.Add(_percent);
        Grid.SetColumn(_cancel, 2);
        row.Children.Add(_cancel);

        _text.Style = Resource<Style>("CaptionTextStyle");
        _text.Foreground = Resource<Brush>("TextMutedBrush");
        _stalled.Style = Resource<Style>("CaptionTextStyle");
        _percent.Style = Resource<Style>("CaptionTextStyle");
        _cancel.Visibility = Visibility.Collapsed;
        _cancel.Click += (_, _) =>
        {
            _cancel.IsEnabled = false;
            OnCancel?.Invoke();
        };
        AutomationProperties.SetName(_bar, "Progreso de la operación");
        AutomationProperties.SetName(_cancel, "Cancelar la operación");

        Content = new StackPanel { Spacing = 4, Children = { row, _text, _stalled } };
        Visibility = Visibility.Collapsed;
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>El avance que se dibuja; nulo oculta la vista.</summary>
    public OperationProgress? Operation
    {
        get => _operation;
        set
        {
            _operation = value;
            _cancel.IsEnabled = true;
            Refresh();
        }
    }

    /// <summary>Lo que hace «Cancelar» (normalmente, pedir la cancelación y <see cref="OperationProgress.MarkCancelling"/>).</summary>
    public Action? OnCancel { get; set; }

    /// <summary>Falso donde el mensaje ya se muestra aparte (por ejemplo, el título de la barra de avisos).</summary>
    public bool ShowMessage { get; set; } = true;

    /// <summary>Falso donde otro control ya ofrece «Cancelar» (por ejemplo, el botón de la barra de avisos).</summary>
    public bool ShowCancel { get; set; } = true;

    /// <summary>Vuelve a leer el avance y toca solo lo que cambió.</summary>
    public void Refresh()
    {
        if (_operation is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        OperationSnapshot s = _operation.Snapshot();
        Visibility = Visibility.Visible;
        bool indeterminate = s.Fraction is null && !s.IsFinished;
        if (_bar.IsIndeterminate != indeterminate)
        {
            _bar.IsIndeterminate = indeterminate;
        }

        double value = s.Fraction ?? 0;
        if (Math.Abs(_bar.Value - value) >= 0.002 || (value >= 1) != (_bar.Value >= 1))
        {
            _bar.Value = value;
        }

        Set(_percent, s.PercentText);
        Set(_text, s.TextFor(ShowMessage));
        Set(_stalled, s.StalledText);
        _stalled.Visibility = s.StalledText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Visibility cancel = s.CanCancel && ShowCancel ? Visibility.Visible : Visibility.Collapsed;
        if (_cancel.Visibility != cancel)
        {
            _cancel.Visibility = cancel;
        }
    }

    private static void Set(TextBlock block, string text)
    {
        if (block.Text != text)
        {
            block.Text = text;
        }

        Visibility v = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (block.Visibility != v)
        {
            block.Visibility = v;
        }
    }

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];
}
