using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Formato del proyecto desde el reproductor (como el «Proporción» de CapCut): el botón dice la forma y la resolución,
/// el menú cambia la proporción en un clic con «Ajustar clips» recordado, y «Más opciones…» abre el diálogo con la
/// resolución, los cuadros por segundo y las medidas a mano. Cada cambio es un paso del historial.
/// </summary>
internal sealed partial class PlayerView
{
    private static IReadOnlyList<(ClipFit Fit, string Label, string Help)> FitChoices => ProjectFormats.FitChoices;

    private void AttachFormat(EditorServices services)
    {
        services.ShortcutRegistry.Register(new ShortcutAction(PlayerActionIds.ProjectFormat, "Formato del proyecto…", ShortcutGroups.View, []));
        _bindings.Add(services.Shortcuts.Bind(PlayerActionIds.ProjectFormat, () => UiTask.Run(ShowFormatDialogAsync())));
    }

    private void UpdateFormatButton()
    {
        if (Session is null)
        {
            return;
        }

        Sequence sequence = Session.Sequence;
        int shortSide = Math.Min(sequence.Width, sequence.Height);
        string resolution = ProjectFormats.Resolutions.FirstOrDefault(r => r.ShortSide == shortSide).Label ?? $"{sequence.Width}×{sequence.Height}";
        FormatButton.Content = $"{ProjectFormats.AspectName(sequence.Width, sequence.Height)} · {resolution}";
        ToolTipService.SetToolTip(FormatButton, $"Formato del proyecto: {ProjectFormats.Describe(sequence)}. Clic para cambiarlo.");
    }

    /// <summary>
    /// El contorno del lienzo donde el render pone la imagen (así se ve la forma real: 9:16 vertical, 1:1…) y el aviso del
    /// visor, centrado dentro de la imagen y con salto de línea a su ancho.
    /// </summary>
    private void UpdateCanvasOutline()
    {
        ViewerPlacement? placement = Placement();
        if (placement is { } p)
        {
            CanvasOutline.Visibility = Visibility.Visible;
            CanvasOutline.StrokeThickness = p.Hairline;
            PlaceRect(CanvasOutline, p.Outline);
            PlaceRect(StatusHost, p.Image);
        }
        else
        {
            CanvasOutline.Visibility = Visibility.Collapsed;
            PlaceRect(StatusHost, new PanelRect(0, 0, Math.Max(0, VideoPanel.ActualWidth), Math.Max(0, VideoPanel.ActualHeight)));
        }
    }

    /// <summary>Para la autoprueba: muestra el aviso del visor vacío aunque haya clips, para medir dónde queda.</summary>
    internal void ShowHintForSelfTest(bool show)
    {
        _hintForSelfTest = show;
        UpdateTransport();
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusHost.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnFormatMenuOpening(object sender, object e)
    {
        if (Session is null || _services is null)
        {
            return;
        }

        Sequence sequence = Session.Sequence;
        AspectChoice current = ProjectFormats.Match(sequence.Width, sequence.Height);
        FormatMenu.Items.Clear();
        foreach (AspectChoice aspect in ProjectFormats.Aspects)
        {
            if (aspect.Custom)
            {
                continue;
            }

            (int w, int h) = ProjectFormats.Reshape(aspect, sequence.Width, sequence.Height);
            var item = new RadioMenuFlyoutItem
            {
                Text = $"{aspect.Label}   {aspect.Use}", GroupName = "ProjectAspect", IsChecked = ReferenceEquals(aspect, current),
            };
            ToolTipService.SetToolTip(item, $"{w}×{h}");
            item.Click += (_, _) => ApplyFormat(w, h, Session.Sequence.FrameRate);
            FormatMenu.Items.Add(item);
        }

        var custom = new RadioMenuFlyoutItem { Text = "Personalizado…", GroupName = "ProjectAspect", IsChecked = current.Custom };
        custom.Click += (_, _) => UiTask.Run(ShowFormatDialogAsync());
        FormatMenu.Items.Add(custom);
        FormatMenu.Items.Add(new MenuFlyoutSeparator());

        var fit = new MenuFlyoutSubItem { Text = "Ajustar clips" };
        foreach ((ClipFit value, string label, string help) in FitChoices)
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "ProjectFit", IsChecked = ProjectFormats.Normalize(_services.Settings.Current.FormatClipFit) == value };
            ToolTipService.SetToolTip(item, help);
            item.Click += (_, _) => _services.Settings.Current.FormatClipFit = value;
            fit.Items.Add(item);
        }

        FormatMenu.Items.Add(fit);
        var rates = new MenuFlyoutSubItem { Text = $"Cuadros por segundo ({ProjectFormats.Fps(sequence.FrameRate)})" };
        foreach ((Rational rate, string label) in ProjectFormats.FrameRates)
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "ProjectRate", IsChecked = rate == sequence.FrameRate };
            item.Click += (_, _) => ApplyFormat(Session.Sequence.Width, Session.Sequence.Height, rate);
            rates.Items.Add(item);
        }

        FormatMenu.Items.Add(rates);
        var more = new MenuFlyoutItem { Text = "Resolución y más opciones…", Icon = new FontIcon { Glyph = "" } };
        more.Click += (_, _) => UiTask.Run(ShowFormatDialogAsync());
        FormatMenu.Items.Add(more);
    }

    private void ApplyFormat(int width, int height, Rational frameRate) =>
        ApplyFormat(width, height, frameRate, ProjectFormats.Normalize(_services?.Settings.Current.FormatClipFit ?? ClipFit.Fit));

    private void ApplyFormat(int width, int height, Rational frameRate, ClipFit fit, SequenceColorSpace? colorSpace = null)
    {
        // Un ajuste fuera de la lista (ajustes guardados a mano) ya no lanza aquí: antes «First» cerraba la app desde el clic.
        fit = ProjectFormats.Normalize(fit);
        if (Session is not { } session || !ProjectFormats.IsValidSize(width, height) || !session.ChangeFormat(width, height, frameRate, fit, colorSpace))
        {
            return;
        }

        string how = ProjectFormats.FitLabel(fit).ToLowerInvariant();
        session.Notify(NotificationSeverity.Success, "Formato del proyecto", $"Ahora es {ProjectFormats.Describe(session.Sequence)} (clips: {how}).",
            new NotificationAction("Deshacer", () => session.Undo()));
    }

    /// <summary>El diálogo completo: proporción, resolución o medidas a mano, cuadros por segundo y cómo quedan los clips.</summary>
    internal async Task ShowFormatDialogAsync()
    {
        if (Session is not { } session || _services is not { } services || XamlRoot is null)
        {
            return;
        }

        Sequence sequence = session.Sequence;
        var panel = new StackPanel { Spacing = 8, Width = 420 };
        var aspectBox = new ComboBox { Header = "Proporción", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (AspectChoice aspect in ProjectFormats.Aspects)
        {
            aspectBox.Items.Add(aspect.Custom ? aspect.Label : $"{aspect.Label} · {aspect.Use}");
        }

        var resolutionBox = new ComboBox { Header = "Resolución", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach ((int _, string label) in ProjectFormats.Resolutions)
        {
            resolutionBox.Items.Add(label);
        }

        var widthBox = new NumberBox { Header = "Ancho", Minimum = ProjectFormats.MinSide, Maximum = ProjectFormats.MaxSide, SmallChange = 2, Value = sequence.Width, Width = 120 };
        var heightBox = new NumberBox { Header = "Alto", Minimum = ProjectFormats.MinSide, Maximum = ProjectFormats.MaxSide, SmallChange = 2, Value = sequence.Height, Width = 120 };
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { widthBox, heightBox } };
        var rateBox = new ComboBox { Header = "Cuadros por segundo", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach ((Rational _, string label) in ProjectFormats.FrameRates)
        {
            rateBox.Items.Add(label);
        }

        rateBox.SelectedIndex = Math.Max(0, ProjectFormats.FrameRates.ToList().FindIndex(r => r.Rate == sequence.FrameRate));
        var colorBox = new ComboBox { Header = "Color", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach ((SequenceColorSpace _, string label, string help) in ProjectFormats.ColorSpaces)
        {
            var item = new ComboBoxItem { Content = label };
            ToolTipService.SetToolTip(item, help);
            colorBox.Items.Add(item);
        }

        colorBox.SelectedIndex = Math.Max(0, ProjectFormats.ColorSpaces.ToList().FindIndex(c => c.Space == sequence.ColorSpace));
        var fitPanel = new StackPanel { Spacing = 2 };
        fitPanel.Children.Add(new TextBlock { Text = "Ajustar clips", Style = (Style)Application.Current.Resources["LabelTextStyle"] });
        var fitButtons = new List<RadioButton>();
        foreach ((ClipFit value, string label, string help) in FitChoices)
        {
            var radio = new RadioButton { Content = $"{label}: {help}", GroupName = "DialogFit", Tag = value, IsChecked = ProjectFormats.Normalize(services.Settings.Current.FormatClipFit) == value };
            fitButtons.Add(radio);
            fitPanel.Children.Add(radio);
        }

        var result = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextStyle"], TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(aspectBox);
        panel.Children.Add(resolutionBox);
        panel.Children.Add(sizeRow);
        panel.Children.Add(rateBox);
        panel.Children.Add(colorBox);
        panel.Children.Add(fitPanel);
        panel.Children.Add(result);

        bool updating = false;
        (int Width, int Height) Size()
        {
            AspectChoice aspect = ProjectFormats.Aspects[Math.Max(0, aspectBox.SelectedIndex)];
            if (aspect.Custom)
            {
                int w = double.IsNaN(widthBox.Value) ? 0 : (int)Math.Round(widthBox.Value);
                int h = double.IsNaN(heightBox.Value) ? 0 : (int)Math.Round(heightBox.Value);
                return (w, h);
            }

            return aspect.Size(ProjectFormats.Resolutions[Math.Max(0, resolutionBox.SelectedIndex)].ShortSide);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Formato del proyecto", Content = panel, PrimaryButtonText = "Aplicar", CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        void Refresh()
        {
            if (updating)
            {
                return;
            }

            updating = true;
            bool custom = ProjectFormats.Aspects[Math.Max(0, aspectBox.SelectedIndex)].Custom;
            resolutionBox.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
            sizeRow.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            (int w, int h) = Size();
            bool valid = ProjectFormats.IsValidSize(w, h);
            dialog.IsPrimaryButtonEnabled = valid;
            SequenceColorSpace space = ProjectFormats.ColorSpaces[Math.Max(0, colorBox.SelectedIndex)].Space;
            string color = space == SequenceColorSpace.Sdr709 ? "" : $" · {ProjectFormats.ColorSpaceName(space)} (se ve en HDR solo con pantalla HDR; exporta en HEVC, AV1 o ProRes de 10 bits)";
            result.Text = valid
                ? $"Quedará {ProjectFormats.AspectName(w, h)} · {w}×{h} · {rateBox.SelectedItem} fps{color}. Un solo Ctrl+Z lo deshace."
                : $"Ancho y alto pares, de {ProjectFormats.MinSide} a {ProjectFormats.MaxSide} píxeles.";
            updating = false;
        }

        AspectChoice currentAspect = ProjectFormats.Match(sequence.Width, sequence.Height);
        aspectBox.SelectedIndex = ProjectFormats.Aspects.ToList().IndexOf(currentAspect);
        int shortSide = Math.Min(sequence.Width, sequence.Height);
        int resolution = ProjectFormats.Resolutions.ToList().FindIndex(r => r.ShortSide == shortSide);
        resolutionBox.SelectedIndex = resolution >= 0 ? resolution : 1;
        if (resolution < 0 && !currentAspect.Custom)
        {
            aspectBox.SelectedIndex = ProjectFormats.Aspects.Count - 1;
        }

        aspectBox.SelectionChanged += (_, _) => Refresh();
        resolutionBox.SelectionChanged += (_, _) => Refresh();
        rateBox.SelectionChanged += (_, _) => Refresh();
        colorBox.SelectionChanged += (_, _) => Refresh();
        widthBox.ValueChanged += (_, _) => Refresh();
        heightBox.ValueChanged += (_, _) => Refresh();
        Refresh();

        if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        (int width, int height) = Size();
        ClipFit fit = fitButtons.FirstOrDefault(b => b.IsChecked == true)?.Tag is ClipFit chosen ? chosen : ClipFit.Fit;
        services.Settings.Current.FormatClipFit = fit;
        if (ProjectFormats.IsValidSize(width, height))
        {
            ApplyFormat(width, height, ProjectFormats.FrameRates[Math.Max(0, rateBox.SelectedIndex)].Rate, fit,
                ProjectFormats.ColorSpaces[Math.Max(0, colorBox.SelectedIndex)].Space);
        }
    }
}
