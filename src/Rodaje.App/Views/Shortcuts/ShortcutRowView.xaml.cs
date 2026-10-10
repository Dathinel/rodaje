using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Views.Shortcuts;

/// <summary>Lo que una fila le pide a la página.</summary>
internal interface IShortcutRowHost
{
    /// <param name="replacing">La tecla que se cambia, o <c>null</c> para añadir una más.</param>
    void BeginCapture(ShortcutRowView view, KeyChord? replacing);

    void CancelCapture();

    void RemoveKey(ShortcutRow row, KeyChord chord);

    void Reset(ShortcutRow row);
}

/// <summary>Una fila de la página de atajos; el foco vuelve a «Cambiar» al terminar una captura.</summary>
internal sealed partial class ShortcutRowView : UserControl
{
    private readonly IShortcutRowHost _host;
    private bool _focusLeft;

    public ShortcutRowView(ShortcutRow row, IShortcutRowHost host)
    {
        Row = row;
        _host = host;
        InitializeComponent();
        Row.PropertyChanged += OnRowChanged;
    }

    public ShortcutRow Row { get; }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShortcutRow.IsCapturing))
        {
            if (Row.IsCapturing)
            {
                DispatcherQueue.TryEnqueue(() => CaptureBox.Focus(FocusState.Keyboard));
            }
            else if (!_focusLeft)
            {
                ChangeButton.Focus(FocusState.Keyboard);
            }

            _focusLeft = false;
        }
        else if (e.PropertyName == nameof(ShortcutRow.CaptureText) && Row.IsCapturing && Row.CaptureText.Length > 0)
        {
            FrameworkElementAutomationPeer.FromElement(CaptureBox)?.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, Row.CaptureText, "atajos.captura");
        }
    }

    private void OnChangeClick(object sender, RoutedEventArgs e) =>
        _host.BeginCapture(this, Row.Keys.Count > 0 ? Row.Keys[0] : null);

    private void OnAddClick(object sender, RoutedEventArgs e) => _host.BeginCapture(this, null);

    private void OnResetClick(object sender, RoutedEventArgs e) => _host.Reset(Row);

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is KeyChip chip)
        {
            _host.BeginCapture(this, chip.Chord);
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is KeyChip chip)
        {
            _host.RemoveKey(Row, chip.Chord);
            ChangeButton.Focus(FocusState.Keyboard);
        }
    }

    private void OnCaptureBoxClick(object sender, RoutedEventArgs e) => _host.CancelCapture();

    private void OnCaptureBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (Row.IsCapturing)
        {
            // La persona se fue a otro control: se cancela sin quitarle el foco.
            _focusLeft = true;
            _host.CancelCapture();
        }
    }
}
