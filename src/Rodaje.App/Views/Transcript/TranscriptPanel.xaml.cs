using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Windows.Foundation;
using Windows.System;

namespace Rodaje.App.Views.Transcript;

internal sealed partial class TranscriptPanel : UserControl, IFocusedShortcutTarget
{
    private readonly Dictionary<TextBlock, DocParagraph> _realized = [];
    private EditorServices? _services;
    private string? _guideTrackId;
    private bool _settingTracks;

    public TranscriptPanel()
    {
        InitializeComponent();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Visible) Refresh();
        });
    }

    public TranscriptPanelModel Model { get; } = new();
    internal bool HasVisibleWords => _realized.Keys.Any(t => t.Inlines.Count > 0 && t.ActualHeight > 0);
    internal int RealizedWordCount => _realized.Values.Sum(p => p.Count);

    public void ShowGuideTrack(string? trackId)
    {
        _guideTrackId = trackId;
        Refresh();
    }

    public void Attach(EditorServices services)
    {
        _services = services;
        services.ShortcutRegistry.RegisterRange(TranscriptShortcuts.Catalog);
        services.Session.ProjectEdited += (_, _) => Refresh();
        services.Session.ProjectOpened += (_, _) =>
        {
            _guideTrackId = null;
            Refresh();
        };
        services.Session.TransportChanged += (_, _) => FollowCursor();
        Theme.ThemeResources.ThemeChanged += (_, _) =>
        {
            foreach ((TextBlock text, DocParagraph paragraph) in _realized) Highlight(text, paragraph);
        };
    }

    public void Refresh()
    {
        if (_services is null || Visibility != Visibility.Visible || _services.Session.IsInGesture) return;
        EditorSession session = _services.Session;
        if (!session.RebuildTranscript(Model, _guideTrackId)) return;

        _settingTracks = true;
        GuideTrack.Items.Clear();
        foreach (Track track in session.Sequence.AudioTracks.Concat(session.Sequence.VideoTracks)
                     .Where(t => t.Clips.Any(c => session.Project.FindTranscript(c.MediaId) is not null)))
        {
            GuideTrack.Items.Add(new ComboBoxItem { Content = track.Name, Tag = track.Id });
        }

        if (_guideTrackId is not null && !GuideTrack.Items.Cast<ComboBoxItem>().Any(t => (string)t.Tag == _guideTrackId))
        {
            _guideTrackId = null;
            session.RebuildTranscript(Model);
        }

        GuideTrack.SelectedItem = GuideTrack.Items.Cast<ComboBoxItem>().FirstOrDefault(t => (string)t.Tag == Model.Document.TrackId);
        GuideTrack.Visibility = GuideTrack.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _settingTracks = false;
        _realized.Clear();
        // Índices en un vector WinRT: DocParagraph es un struct del núcleo, no un tipo proyectado.
        ParagraphList.ItemsSource = new ObservableCollection<object>(Enumerable.Range(0, Model.Document.Paragraphs.Count).Select(i => (object)i));
        EmptyText.Visibility = Model.Document.Words.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = Model.Document.Words.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateSummary();
        FollowCursor();
    }

    private void OnParagraphPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (TextOf(args.Element) is not { } text) return;
        DocParagraph paragraph = Model.Document.Paragraphs[args.Index];
        _realized[text] = paragraph;
        text.Inlines.Clear();
        for (int i = paragraph.FirstWord; i < paragraph.FirstWord + paragraph.Count; i++)
        {
            text.Inlines.Add(new Run { Text = Model.Document.Words[i].Text + " " });
        }

        Highlight(text, paragraph);
    }

    private void OnParagraphClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (TextOf(args.Element) is { } text) _realized.Remove(text);
    }

    private static TextBlock? TextOf(UIElement element) => element is Grid { Children.Count: > 1 } grid ? grid.Children[1] as TextBlock : null;

    private void OnParagraphSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var text = (TextBlock)sender;
        if (_realized.TryGetValue(text, out DocParagraph paragraph)) Highlight(text, paragraph);
    }

    private void Highlight(TextBlock text, DocParagraph paragraph)
    {
        if (VisualTreeHelper.GetParent(text) is not Grid grid || grid.Children[0] is not Canvas canvas) return;
        canvas.Children.Clear();
        var selectedBackground = new SolidColorBrush(Theme.ThemeResources.SolidColor("AccentBrush"));
        var selectedForeground = new SolidColorBrush(Theme.ThemeResources.SolidColor("AccentForegroundBrush"));
        var activeBackground = new SolidColorBrush(Colors.Goldenrod);
        var activeForeground = new SolidColorBrush(Colors.Black);
        Rect? pending = null;
        bool pendingActive = false;

        void Flush()
        {
            if (pending is not { } rect) return;
            var background = new Rectangle { Width = rect.Width, Height = rect.Height, Fill = pendingActive ? activeBackground : selectedBackground };
            Canvas.SetLeft(background, rect.X);
            Canvas.SetTop(background, rect.Y);
            canvas.Children.Add(background);
            pending = null;
        }

        for (int i = paragraph.FirstWord; i < paragraph.FirstWord + paragraph.Count; i++)
        {
            var run = (Run)text.Inlines[i - paragraph.FirstWord];
            bool active = Model.ActiveWord == i;
            bool selected = Model.IsSelected(i);
            if (active || selected) run.Foreground = active ? activeForeground : selectedForeground;
            else run.ClearValue(TextElement.ForegroundProperty);
            if ((!active && !selected) || text.ActualHeight <= 0) continue;
            for (int offset = 0; offset < run.Text.Length; offset++)
            {
                if (run.ContentStart.GetPositionAtOffset(offset, LogicalDirection.Forward) is not { } pointer) continue;
                Rect rect = pointer.GetCharacterRect(LogicalDirection.Forward);
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                if (pending is { } prior && pendingActive == active && Math.Abs(prior.Y - rect.Y) < 0.5
                    && Math.Abs(prior.Height - rect.Height) < 0.5 && Math.Abs(prior.Right - rect.X) < 1)
                {
                    pending = new Rect(prior.X, prior.Y, rect.Right - prior.X, Math.Max(prior.Height, rect.Height));
                }
                else
                {
                    Flush();
                    pending = rect;
                    pendingActive = active;
                }
            }
        }

        Flush();
    }

    private int WordAt(TextBlock text, Point point)
    {
        if (!_realized.TryGetValue(text, out DocParagraph paragraph)) return -1;
        int i = paragraph.FirstWord;
        foreach (Inline inline in text.Inlines)
        {
            if (inline is Run run)
            {
                for (int offset = 0; offset < run.Text.Length; offset++)
                {
                    TextPointer? pointer = run.ContentStart.GetPositionAtOffset(offset, LogicalDirection.Forward);
                    if (pointer is not null && pointer.GetCharacterRect(LogicalDirection.Forward).Contains(point)) return i;
                }
            }
            i++;
        }

        return -1;
    }

    private void OnWordTapped(object sender, TappedRoutedEventArgs args)
    {
        var text = (TextBlock)sender;
        int word = WordAt(text, args.GetPosition(text));
        if (word < 0) return;
        Focus(FocusState.Programmatic);
        Model.SelectWord(word, KeyboardShortcutHost.CurrentModifiers().HasFlag(KeyModifiers.Shift));
        SelectionChanged();
        args.Handled = true;
    }

    private void OnWordDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        var text = (TextBlock)sender;
        int word = WordAt(text, args.GetPosition(text));
        if (word < 0) return;
        Model.SelectWord(word);
        SelectionChanged();
        _services?.Session.Seek(Model.Document.Words[word].SeqStart);
        args.Handled = true;
    }

    private void SelectionChanged()
    {
        _services?.Session.SelectTranscriptWords(Model);
        foreach ((TextBlock text, DocParagraph paragraph) in _realized) Highlight(text, paragraph);
        UpdateSummary();
    }

    private void UpdateSummary() => SummaryText.Text = $"{Model.Document.Words.Count} palabras · {Model.SelectionCount} seleccionadas · {Model.Matches.Count} resultados";

    private void FollowCursor()
    {
        if (_services is null || Visibility != Visibility.Visible || !Model.UpdateActiveWord(_services.Session.Position)) return;
        foreach ((TextBlock text, DocParagraph paragraph) in _realized) Highlight(text, paragraph);
        RevealWord(Model.ActiveWord);
    }

    private void RevealWord(int word)
    {
        if (word < 0 || Scroller.ViewportHeight <= 0) return;
        IReadOnlyList<DocParagraph> paragraphs = Model.Document.Paragraphs;
        int index = 0;
        while (index < paragraphs.Count && word >= paragraphs[index].FirstWord + paragraphs[index].Count) index++;
        if (index == paragraphs.Count) return;
        UIElement? element = ParagraphList.TryGetElement(index);
        if (element is null)
        {
            element = ParagraphList.GetOrCreateElement(index);
            element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            ParagraphList.UpdateLayout();
        }
        if (element is not FrameworkElement row) return;
        Point at = row.TransformToVisual(Scroller).TransformPoint(new Point());
        if (at.Y < 0 || at.Y + row.ActualHeight > Scroller.ViewportHeight)
        {
            Rect target = new(0, 0, row.ActualWidth, Math.Min(row.ActualHeight, Scroller.ViewportHeight));
            if (TextOf(row) is { } text && word - paragraphs[index].FirstWord < text.Inlines.Count)
            {
                target = text.Inlines[word - paragraphs[index].FirstWord].ContentStart.GetCharacterRect(LogicalDirection.Forward);
                Point wordAt = row.TransformToVisual(Scroller).TransformPoint(new Point(target.X, target.Y));
                if (wordAt.Y >= 0 && wordAt.Y + target.Height <= Scroller.ViewportHeight) return;
            }

            row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, TargetRect = target });
        }
    }

    public bool TryHandleShortcut(KeyChord chord)
    {
        if (_services is null || Visibility != Visibility.Visible) return false;
        bool textInput = XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox;
        foreach (ShortcutAction action in TranscriptShortcuts.Catalog)
        {
            if (!_services.ShortcutRegistry.GetKeys(action.Id).Contains(chord)) continue;
            if (textInput && action.Id != TranscriptShortcuts.Find) return false;
            switch (action.Id)
            {
                case "transcript.removeFillers": UiTask.Run(ShowFillersAsync()); break;
                case TranscriptShortcuts.Delete: _services.Session.ExecuteTranscript(Model.DeleteSelection(true)); break;
                case TranscriptShortcuts.Lift: _services.Session.ExecuteTranscript(Model.DeleteSelection(false)); break;
                case TranscriptShortcuts.Cut: Model.Cut(); break;
                case TranscriptShortcuts.Paste: _services.Session.ExecuteTranscript(Model.Paste(Model.SelectionFirst < 0 ? Model.Document.Words.Count : Model.SelectionFirst)); break;
                case TranscriptShortcuts.Find: SearchBox.Focus(FocusState.Programmatic); SearchBox.SelectAll(); break;
                case TranscriptShortcuts.SelectAll: Model.SelectAll(); SelectionChanged(); break;
                case TranscriptShortcuts.ExtendLeft: Model.ExtendSelection(-1); SelectionChanged(); RevealWord(Model.SelectionFirst); break;
                case TranscriptShortcuts.ExtendRight: Model.ExtendSelection(1); SelectionChanged(); RevealWord(Model.SelectionFirst + Model.SelectionCount - 1); break;
            }

            return true;
        }

        return false;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs args)
    {
        Model.Find(SearchBox.Text);
        UpdateSummary();
    }

    private void FindNext(bool previous)
    {
        if (!Model.FindNext(previous)) return;
        SelectionChanged();
        RevealWord(Model.SelectionFirst);
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter) return;
        FindNext(KeyboardShortcutHost.CurrentModifiers().HasFlag(KeyModifiers.Shift));
        args.Handled = true;
    }

    private void OnPreviousClick(object sender, RoutedEventArgs args) => FindNext(true);
    private void OnNextClick(object sender, RoutedEventArgs args) => FindNext(false);
    private void OnGuideChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_settingTracks) return;
        _guideTrackId = (GuideTrack.SelectedItem as ComboBoxItem)?.Tag as string;
        Refresh();
    }

    public Task ShowFillersAsync() => ShowChoicesAsync(true);
    private void OnFillersClick(object sender, RoutedEventArgs args) => UiTask.Run(ShowChoicesAsync(true));
    private void OnPausesClick(object sender, RoutedEventArgs args) => UiTask.Run(ShowChoicesAsync(false));

    private async Task ShowChoicesAsync(bool fillers)
    {
        if (_services is null || XamlRoot is null || DialogGate.IsOpen) return;
        Refresh();
        TranscriptDocument document = Model.Document;
        var choices = new StackPanel { Spacing = 4 };
        if (fillers)
        {
            foreach (FillerChoice choice in Model.Fillers)
            {
                var box = new CheckBox { Content = $"{document.Words[choice.Word].Text} · {document.Words[choice.Word].SeqStart.ToSeconds():0.0} s{(choice.Safe ? "" : " · revisar")}", IsChecked = choice.IsChecked };
                box.Checked += (_, _) => choice.IsChecked = true;
                box.Unchecked += (_, _) => choice.IsChecked = false;
                choices.Children.Add(box);
            }
        }
        else
        {
            foreach (PauseChoice choice in Model.Pauses)
            {
                var box = new CheckBox { Content = $"{choice.Start.ToSeconds():0.0} s · pausa de {(choice.End - choice.Start).ToSeconds():0.0} s", IsChecked = choice.IsChecked };
                box.Checked += (_, _) => choice.IsChecked = true;
                box.Unchecked += (_, _) => choice.IsChecked = false;
                choices.Children.Add(box);
            }
        }

        if (choices.Children.Count == 0) choices.Children.Add(new TextBlock { Text = "No hay candidatos en esta pista." });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = fillers ? "Quitar muletillas" : "Quitar pausas",
            Content = new ScrollViewer { Content = choices, MaxHeight = 400 },
            PrimaryButtonText = "Quitar lo marcado",
            CloseButtonText = "Cancelar",
            IsPrimaryButtonEnabled = choices.Children.OfType<CheckBox>().Any(),
        };
        if (await DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary && ReferenceEquals(document, Model.Document))
            _services.Session.ExecuteTranscript(fillers ? Model.RemoveFillers() : Model.RemovePauses());
    }
}
