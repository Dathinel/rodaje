using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Views.Subtitles;
using Rodaje.Core.Model;
using Windows.System;

namespace Rodaje.App.Views.Inspector.Text;

/// <summary>
/// «Palabras» de un subtítulo automático: cada palabra con su hora, para corregirla, partir el subtítulo ahí o ir al
/// cursor; unir con el siguiente y la palabra resaltada (karaoke).
/// </summary>
internal sealed partial class TextPanel
{
    private string? _wordsShown;

    private void BuildWordRows()
    {
        HighlightRows.Children.Add(Toggle("Resaltar la palabra que suena", "highlight-on", s => s.Highlight is not null,
            (s, on) => s.Highlight = on ? new StyleHighlight() : null));
        HighlightRows.Children.Add(ColorRow("Resaltado", "highlight-color", s => s.Highlight?.Color, (s, c) => (s.Highlight ??= new StyleHighlight()).Color = c));
    }

    private void ShowWords(Clip clip, ClipGenerator generator)
    {
        WordsSection.Visibility = generator.Words is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (generator.Words is not { Count: > 0 } words || _editor is not { } editor)
        {
            _wordsShown = null;
            return;
        }

        WordsSection.Summary = words.Count == 1 ? "1 palabra" : $"{words.Count} palabras";
        JoinButton.IsEnabled = editor.HasNextTitle();

        // Se rehace la lista solo si cambió lo que muestra, y nunca con una palabra a medio corregir.
        string shown = clip.Id + "|" + clip.Start.Ticks + "|" + clip.SourceIn.Ticks + "|" + string.Join('\u001f', words.Select(w => $"{w.Start.Ticks}:{w.Text}"));
        if (shown == _wordsShown
            || (_wordsShown is not null && WordList.Children.OfType<Grid>().Any(g => g.Children.OfType<TextBox>().Any(b => b.FocusState != FocusState.Unfocused))))
        {
            return;
        }

        _wordsShown = shown;
        WordList.Children.Clear();
        for (int i = 0; i < words.Count; i++)
        {
            WordList.Children.Add(WordRow(clip, words[i], i));
        }
    }

    private Grid WordRow(Clip clip, GeneratorWord word, int index)
    {
        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var time = new HyperlinkButton
        {
            Content = AutoSubtitles.Clock(TitleEditor.WordTime(clip, word)),
            Padding = new Thickness(0),
            FontSize = Resource<double>("BodyFontSize"),
        };
        ToolTipService.SetToolTip(time, "Llevar el cursor a esta palabra");
        time.Click += (_, _) => _services?.Session.Seek(TitleEditor.WordTime(clip, word));

        var box = new TextBox { Text = word.Text, Height = 28, MinHeight = 28, MinWidth = 0, FontSize = Resource<double>("BodyFontSize") };
        AutomationProperties.SetName(box, $"Palabra {index + 1}");
        void Commit()
        {
            if (box.Text != word.Text)
            {
                _wordsShown = null;
                _editor?.CorrectWord(index, box.Text);
            }
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                Commit();
            }
            else if (e.Key == VirtualKey.Escape)
            {
                box.Text = word.Text;
            }
        };

        var split = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            Width = 28,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            IsEnabled = index > 0,
        };
        AutomationProperties.SetName(split, $"Dividir el subtítulo en «{word.Text}»");
        ToolTipService.SetToolTip(split, "Dividir aquí: esta palabra empieza un subtítulo nuevo");
        split.Click += (_, _) =>
        {
            _wordsShown = null;
            _editor?.SplitAtWord(index);
        };

        Grid.SetColumn(box, 1);
        Grid.SetColumn(split, 2);
        grid.Children.Add(time);
        grid.Children.Add(box);
        grid.Children.Add(split);
        return grid;
    }

    private void OnJoinClick(object sender, RoutedEventArgs e)
    {
        _wordsShown = null;
        _editor?.JoinWithNext();
    }
}
