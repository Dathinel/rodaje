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
using Rodaje.App.Services.Format;
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
    private readonly Dictionary<Run, int> _wordIndices = [];
    private readonly Dictionary<Run, RemovedTranscriptSpan> _removedRuns = [];
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
    internal bool HasVisibleRemovedWords => _realized.Keys.Any(t => t.ActualHeight > 0 && t.Inlines.OfType<Run>().Any(_removedRuns.ContainsKey));
    internal bool HasVisibleSpeakerNames => _realized.Keys.Any(t => t.ActualHeight > 0
        && t.Inlines.OfType<Run>().Any(r => !_wordIndices.ContainsKey(r) && !_removedRuns.ContainsKey(r)));
    internal int RealizedWordCount => _realized.Values.Sum(p => p.Count);
    internal void ShowRemovedWords(bool show) => ShowRemovedToggle.IsOn = show;

    // Para la autoprueba: lo que el panel dibuja de verdad y si un clic sobre una palabra la encuentra.
    internal int HighlightShapeCount => _realized.Keys.Sum(t => VisualTreeHelper.GetParent(t) is Grid grid && grid.Children.Count > 0 && grid.Children[0] is Canvas canvas ? canvas.Children.Count : 0);

    internal void SelectWordAsClick(int word)
    {
        Model.SelectWord(word);
        SelectionChanged();
    }

    /// <summary>La palabra que da un clic en medio de la palabra <paramref name="word"/> (-2 si no está a la vista): debe ser ella.</summary>
    internal int WordFoundInsideItself(int word)
    {
        foreach ((TextBlock text, _) in _realized)
        {
            foreach (Run run in text.Inlines.OfType<Run>())
            {
                if (_wordIndices.GetValueOrDefault(run, -1) != word) continue;
                Rect rect = CharacterRects(run)[run.Text.Length / 2];
                return WordAt(text, new Point(rect.X + (rect.Width * 0.75), rect.Y + (rect.Height / 2)));
            }
        }

        return -2;
    }

    public void ShowGuideTrack(string? trackId)
    {
        _guideTrackId = trackId;
        Refresh();
    }

    public void Attach(EditorServices services)
    {
        _services = services;
        services.Session.ProjectEdited += (_, _) => Refresh();
        services.Session.ProjectOpened += (_, _) =>
        {
            _guideTrackId = null;
            Refresh();
        };
        services.Session.TransportChanged += (_, _) => FollowCursor();
        Theme.ThemeResources.ThemeChanged += (_, _) =>
        {
            foreach ((TextBlock text, DocParagraph paragraph) in _realized) RenderParagraph(text, paragraph);
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
        _wordIndices.Clear();
        _removedRuns.Clear();
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
        RenderParagraph(text, paragraph);
    }

    private void RenderParagraph(TextBlock text, DocParagraph paragraph)
    {
        ForgetRuns(text);
        text.Inlines.Clear();
        for (int i = paragraph.FirstWord; i < paragraph.FirstWord + paragraph.Count; i++)
        {
            if (i == paragraph.FirstWord && Model.SpeakerOf(paragraph) is { } speaker)
            {
                text.Inlines.Add(new Run { Text = speaker.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = SpeakerBrush(speaker.Color) });
                text.Inlines.Add(new LineBreak());
            }

            var run = new Run { Text = Model.Document.Words[i].Text + " " };
            _wordIndices[run] = i;
            text.Inlines.Add(run);
            if (Model.ShowRemoved)
            {
                foreach (RemovedTranscriptSpan span in Model.RemovedWords.Where(s => s.BeforeWord == i + 1))
                {
                    MediaTranscript? transcript = _services?.Session.Project.Transcripts.Find(t => t.Id == span.TranscriptId);
                    if (transcript is null) continue;
                    IEnumerable<SpokenWord> words = transcript.Words.Skip(span.FirstWord).Take(span.Count);
                    if (_services?.Session.Sequence.GetClip(Model.Document.Words[i].ClipId).Clip.TimeMap.Reverse == true)
                        words = words.Reverse();
                    var removed = new Run
                    {
                        Text = string.Join(' ', words.Select(w => w.Text)) + " ",
                        Foreground = new SolidColorBrush(Colors.Gray),
                        TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough,
                    };
                    _removedRuns[removed] = span;
                    text.Inlines.Add(removed);
                }
            }

        }

        Highlight(text, paragraph);
    }

    private void OnParagraphClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (TextOf(args.Element) is { } text)
        {
            ForgetRuns(text);
            _realized.Remove(text);
        }
    }

    private void ForgetRuns(TextBlock text)
    {
        foreach (Run run in text.Inlines.OfType<Run>())
        {
            _wordIndices.Remove(run);
            _removedRuns.Remove(run);
        }
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
        bool contrast = Theme.ThemeResources.IsHighContrast;
        var selectedBackground = new SolidColorBrush(Theme.ThemeResources.SolidColor("AccentBrush"));
        var selectedForeground = new SolidColorBrush(Theme.ThemeResources.SolidColor("AccentForegroundBrush"));
        // La palabra que suena: un tinte del acento con el texto de siempre; con contraste alto, los colores de selección del sistema.
        var activeBackground = new SolidColorBrush(Theme.ThemeResources.SolidColor(contrast ? "AccentBrush" : "AccentSubtleBrush"));
        Brush? activeForeground = contrast ? selectedForeground : null;
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

        foreach (Run run in text.Inlines.OfType<Run>())
        {
            if (!_wordIndices.TryGetValue(run, out int i)) continue;
            bool active = Model.ActiveWord == i;
            bool selected = Model.IsSelected(i);
            Brush? foreground = selected ? selectedForeground : active ? activeForeground : null;
            if (foreground is not null) run.Foreground = foreground;
            else run.ClearValue(TextElement.ForegroundProperty);
            if ((!active && !selected) || text.ActualHeight <= 0) continue;
            // La seleccionada manda sobre la que suena: se dibuja con su color.
            bool asActive = active && !selected;
            foreach (Rect rect in CharacterRects(run))
            {
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                if (pending is { } prior && pendingActive == asActive && Math.Abs(prior.Y - rect.Y) < 0.5
                    && Math.Abs(prior.Height - rect.Height) < 0.5 && Math.Abs(prior.Right - rect.X) < 1)
                {
                    pending = new Rect(prior.X, prior.Y, rect.Right - prior.X, Math.Max(prior.Height, rect.Height));
                }
                else
                {
                    Flush();
                    pending = rect;
                    pendingActive = asActive;
                }
            }
        }

        Flush();
    }

    // GetCharacterRect da ancho 0 en cada carácter (medido con la autoprueba en WinUI 3): sin ancho no se dibuja el fondo de
    // lo elegido ni se encuentra la palabra bajo el puntero. El ancho de cada uno sale de dónde empieza el siguiente.
    private static List<Rect> CharacterRects(Run run)
    {
        string text = run.Text;
        var starts = new Rect[text.Length + 1];
        for (int i = 0; i <= text.Length; i++)
        {
            TextPointer? pointer = i == text.Length ? run.ContentEnd : run.ContentStart!.GetPositionAtOffset(i, LogicalDirection.Forward);
            starts[i] = pointer?.GetCharacterRect(LogicalDirection.Forward) ?? default;
        }

        var rects = new List<Rect>(text.Length);
        double last = 6;
        for (int i = 0; i < text.Length; i++)
        {
            Rect rect = starts[i], next = starts[i + 1];
            if (rect.Height <= 0)
            {
                rects.Add(default);
                continue;
            }

            double width = rect.Width > 0 ? rect.Width
                : next.Height > 0 && Math.Abs(next.Y - rect.Y) < 0.5 && next.X > rect.X ? next.X - rect.X
                : last;
            last = width;
            rects.Add(new Rect(rect.X, rect.Y, width, rect.Height));
        }

        return rects;
    }

    private int WordAt(TextBlock text, Point point)
    {
        return RunAt(text, point) is { } run && _wordIndices.TryGetValue(run, out int index) ? index : -1;
    }

    private static Run? RunAt(TextBlock text, Point point)
    {
        foreach (Inline inline in text.Inlines)
        {
            if (inline is Run run && CharacterRects(run).Exists(rect => rect.Width > 0 && rect.Contains(point))) return run;
        }

        return null;
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
        int previous = Model.ActiveWord;
        if (_services is null || Visibility != Visibility.Visible || !Model.UpdateActiveWord(_services.Session.Position)) return;
        // Solo los párrafos de la palabra que deja de sonar y de la que empieza: esto corre en cada cuadro de la reproducción.
        foreach ((TextBlock text, DocParagraph paragraph) in _realized)
        {
            if (Holds(paragraph, previous) || Holds(paragraph, Model.ActiveWord)) Highlight(text, paragraph);
        }

        RevealWord(Model.ActiveWord);
    }

    private static bool Holds(DocParagraph paragraph, int word) => word >= paragraph.FirstWord && word < paragraph.FirstWord + paragraph.Count;

    // Con el foco en un cuadro de texto (el buscador, el de corregir), las teclas son suyas: WinUI puede dar el foco a una
    // parte interna de la plantilla, así que se sube hasta encontrar el cuadro.
    private static bool IsInTextBox(DependencyObject? element)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is TextBox) return true;
        }

        return false;
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
            if (TextOf(row) is { } text && text.Inlines.OfType<Run>().FirstOrDefault(r => _wordIndices.GetValueOrDefault(r, -1) == word) is { } run)
            {
                target = run.ContentStart!.GetCharacterRect(LogicalDirection.Forward);
                Point wordAt = row.TransformToVisual(Scroller).TransformPoint(new Point(target.X, target.Y));
                if (wordAt.Y >= 0 && wordAt.Y + target.Height <= Scroller.ViewportHeight) return;
            }

            row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, TargetRect = target });
        }
    }

    public bool TryHandleShortcut(KeyChord chord)
    {
        if (_services is null || Visibility != Visibility.Visible) return false;
        bool textInput = XamlRoot is { } root && IsInTextBox(FocusManager.GetFocusedElement(root) as DependencyObject);
        foreach ((string id, KeyChord keys) in TranscriptShortcuts.PanelKeys)
        {
            if (keys != chord) continue;
            if (textInput && id != TranscriptShortcuts.Find) return false;
            switch (id)
            {
                case TranscriptShortcuts.Delete: _services.Session.ExecuteTranscript(Model.DeleteSelection(true)); break;
                case TranscriptShortcuts.Lift: _services.Session.ExecuteTranscript(Model.DeleteSelection(false)); break;
                case TranscriptShortcuts.Cut: Model.Cut(); break;
                case TranscriptShortcuts.Paste: _services.Session.ExecuteTranscript(Model.Paste(Model.SelectionFirst < 0 ? Model.Document.Words.Count : Model.SelectionFirst)); break;
                case TranscriptShortcuts.Find: SearchBox.Focus(FocusState.Programmatic); SearchBox.SelectAll(); break;
                case TranscriptShortcuts.SelectAll: Model.SelectAll(); SelectionChanged(); break;
                case TranscriptShortcuts.ExtendLeft: Model.ExtendSelection(-1); SelectionChanged(); RevealWord(Model.SelectionFirst); break;
                case TranscriptShortcuts.ExtendRight: Model.ExtendSelection(1); SelectionChanged(); RevealWord(Model.SelectionFirst + Model.SelectionCount - 1); break;
                case TranscriptShortcuts.CorrectWord: ShowWordEditor(); break;
                default:
                    if (id.StartsWith(TranscriptShortcuts.AssignSpeaker, StringComparison.Ordinal) && Model.SelectionCrossesMedia)
                        _services.Session.Notify(NotificationSeverity.Informational, "Transcripción", "Elige palabras de un solo medio para asignarles hablante.");
                    else if (id.StartsWith(TranscriptShortcuts.AssignSpeaker, StringComparison.Ordinal))
                        _services.Session.ExecuteTranscript(Model.AssignSpeaker(id[^1] - '0'));
                    break;
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
                var box = new CheckBox { Content = string.Create(SpanishNumbers.Format, $"{document.Words[choice.Word].Text} · {document.Words[choice.Word].SeqStart.ToSeconds():0.0} s{(choice.Safe ? "" : " · revisar")}"), IsChecked = choice.IsChecked };
                box.Checked += (_, _) => choice.IsChecked = true;
                box.Unchecked += (_, _) => choice.IsChecked = false;
                choices.Children.Add(box);
            }
        }
        else
        {
            foreach (PauseChoice choice in Model.Pauses)
            {
                var box = new CheckBox { Content = string.Create(SpanishNumbers.Format, $"{choice.Start.ToSeconds():0.0} s · pausa de {(choice.End - choice.Start).ToSeconds():0.0} s"), IsChecked = choice.IsChecked };
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
