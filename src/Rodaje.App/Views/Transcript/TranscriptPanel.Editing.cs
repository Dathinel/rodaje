using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Settings;
using Rodaje.App.Shell;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Windows.Foundation;
using Windows.System;

namespace Rodaje.App.Views.Transcript;

internal sealed partial class TranscriptPanel
{
    private static SolidColorBrush SpeakerBrush(string? color) => new(Personalization.TryParseColor(color, out uint argb)
        ? Theme.ThemeResources.ToColor(argb) : Theme.ThemeResources.SolidColor("AccentBrush"));

    private void OnShowRemovedToggled(object sender, RoutedEventArgs args)
    {
        Model.ShowRemoved = ((ToggleSwitch)sender).IsOn;
        foreach ((TextBlock text, DocParagraph paragraph) in _realized) RenderParagraph(text, paragraph);
    }

    private void ShowWordEditor()
    {
        if (Model.SelectionCount != 1 || _services is null) return;
        int index = Model.SelectionFirst;
        RevealWord(index);
        TextBlock? text = _realized.Keys.FirstOrDefault(t => t.Inlines.OfType<Run>().Any(r => _wordIndices.GetValueOrDefault(r, -1) == index));
        if (text is null) return;
        Run run = text.Inlines.OfType<Run>().First(r => _wordIndices.GetValueOrDefault(r, -1) == index);
        Rect rect = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        TranscriptDocument document = Model.Document;
        var box = new TextBox { Text = document.Words[index].Text, Width = 220, PlaceholderText = "Corregir palabra · Intro para guardar" };
        AutomationProperties.SetName(box, "Corregir palabra");
        var flyout = new Flyout { Content = box };
        box.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                if (ReferenceEquals(document, Model.Document)) _services.Session.ExecuteTranscript(Model.CorrectWord(box.Text));
                flyout.Hide();
                Focus(FocusState.Programmatic);
                args.Handled = true;
            }
            else if (args.Key == VirtualKey.Escape)
            {
                flyout.Hide();
                Focus(FocusState.Programmatic);
                args.Handled = true;
            }
        };
        flyout.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        flyout.ShowAt(text, new FlyoutShowOptions { Position = new Point(rect.X, rect.Bottom) });
    }

    private void OnWordRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        var text = (TextBlock)sender;
        Point point = args.GetPosition(text);
        if (RunAt(text, point) is not { } run) return;
        var menu = new MenuFlyout();
        if (_removedRuns.TryGetValue(run, out RemovedTranscriptSpan span))
        {
            var recover = new MenuFlyoutItem { Text = "Recuperar" };
            recover.Click += (_, _) => _services?.Session.ExecuteTranscript(Model.Recover(span));
            menu.Items.Add(recover);
        }
        else if (_wordIndices.TryGetValue(run, out int word))
        {
            if (!Model.IsSelected(word)) Model.SelectWord(word);
            SelectionChanged();
            var correct = new MenuFlyoutItem { Text = "Corregir palabra · F2", IsEnabled = Model.SelectionCount == 1 };
            correct.Click += (_, _) => ShowWordEditor();
            var paragraph = new MenuFlyoutItem { Text = "Empezar párrafo aquí" };
            paragraph.Click += (_, _) => _services?.Session.ExecuteTranscript(Model.SplitParagraph());
            menu.Items.Add(correct);
            menu.Items.Add(paragraph);
        }
        else return;
        menu.ShowAt(text, point);
        args.Handled = true;
    }

    private void OnSpeakersClick(object sender, RoutedEventArgs args)
    {
        if (Model.CurrentTranscript is null) return;
        var menu = new MenuFlyout();
        for (int i = 0; i < Model.Speakers.Count; i++)
        {
            TranscriptSpeaker speaker = Model.Speakers[i];
            int number = i + 1;
            var submenu = new MenuFlyoutSubItem { Text = speaker.Name, Foreground = SpeakerBrush(speaker.Color) };
            var assign = new MenuFlyoutItem { Text = number <= 9 ? $"Asignar · Alt+{number}" : "Asignar", IsEnabled = Model.SelectionCount > 0 };
            assign.Click += (_, _) => _services?.Session.ExecuteTranscript(Model.AssignSpeaker(number));
            submenu.Items.Add(assign);
            var edit = new MenuFlyoutItem { Text = "Renombrar y dar color…" };
            edit.Click += (_, _) => UiTask.Run(EditSpeakerAsync(speaker));
            submenu.Items.Add(edit);
            menu.Items.Add(submenu);
        }
        var remove = new MenuFlyoutItem { Text = "Quitar hablante · Alt+0", IsEnabled = Model.SelectionCount > 0 };
        remove.Click += (_, _) => _services?.Session.ExecuteTranscript(Model.AssignSpeaker(0));
        var add = new MenuFlyoutItem { Text = "Añadir hablante…" };
        add.Click += (_, _) => UiTask.Run(EditSpeakerAsync(null));
        menu.Items.Add(remove);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(add);
        menu.ShowAt((FrameworkElement)sender);
    }

    private async Task EditSpeakerAsync(TranscriptSpeaker? speaker)
    {
        if (_services is null || XamlRoot is null || DialogGate.IsOpen) return;
        EditorSession session = _services.Session;
        TranscriptDocument document = Model.Document;
        var name = new TextBox { Header = "Nombre", Text = speaker?.Name ?? $"Hablante {Model.Speakers.Count + 1}" };
        var defaultColor = new CheckBox { Content = "Color por omisión", IsChecked = speaker?.Color is null };
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorPreviewVisible = false,
            Color = SpeakerBrush(speaker?.Color).Color,
        };
        picker.ColorChanged += (_, _) => defaultColor.IsChecked = false;
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(name);
        content.Children.Add(defaultColor);
        content.Children.Add(picker);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = speaker is null ? "Añadir hablante" : "Editar hablante",
            Content = content, PrimaryButtonText = "Guardar", CloseButtonText = "Cancelar",
        };
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = string.IsNullOrWhiteSpace(name.Text);
        if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary || !ReferenceEquals(document, Model.Document)) return;
        Windows.UI.Color selected = picker.Color;
        string? color = defaultColor.IsChecked == true ? null : $"#{selected.R:X2}{selected.G:X2}{selected.B:X2}";
        session.ExecuteTranscript(speaker is null ? Model.AddSpeaker(name.Text.Trim(), color)
            : Model.UpdateSpeaker(speaker.Id, name.Text.Trim(), color));
    }
}
