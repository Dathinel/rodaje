using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Theme;
using Rodaje.Core.Timing;
using Rodaje.Engine.Presentation;

namespace Rodaje.App.Shell;

/// <summary>«Ir a tiempo» (Ctrl+J): un cuadro para escribir un tiempo o un salto y llevar ahí el cursor.</summary>
internal sealed partial class MainWindow
{
    private async Task GoToTimeAsync()
    {
        if (DialogGate.IsOpen || Root.XamlRoot is null)
        {
            return;
        }

        EditorSession session = Session;
        var box = new TextBox
        {
            Text = Timecode.Format(session.ProgramPosition.SnapToFrame(session.Sequence.FrameRate)),
            PlaceholderText = "1:23, 1:23.5, 83, 120f, +10, -2s",
            MinWidth = 260,
        };
        box.SelectAll();
        var hint = new TextBlock
        {
            Text = "Minutos:segundos (con decimales si quieres), segundos sueltos o fotogramas con «f». Con + o - delante, salta desde el cursor.",
            TextWrapping = TextWrapping.Wrap,
            Style = ThemeResources.Style("CaptionTextStyle"),
        };
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Text = "No se entiende ese tiempo." };
        var content = new StackPanel { Spacing = 8, MaxWidth = 360 };
        content.Children.Add(box);
        content.Children.Add(hint);
        content.Children.Add(problem);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Ir a tiempo",
            Content = content,
            PrimaryButtonText = "Ir",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        // Si lo escrito no es un tiempo, el cuadro sigue abierto con el aviso; la vista previa del cursor se queda donde estaba.
        dialog.PrimaryButtonClick += (_, args) =>
        {
            bool moved = session.GoToTime(box.Text);
            problem.Visibility = moved ? Visibility.Collapsed : Visibility.Visible;
            args.Cancel = !moved;
        };
        box.TextChanged += (_, _) =>
        {
            Rational rate = session.Sequence.FrameRate;
            dialog.IsPrimaryButtonEnabled = TimeInput.TryParse(box.Text, rate, session.ProgramPosition, out _);
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        await DialogGate.ShowAsync(dialog);
    }
}
