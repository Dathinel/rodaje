using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.App.Services.Tracking;
using Rodaje.App.Shell;
using Rodaje.Core.Commands;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using O = Rodaje.Core.Model.BuiltInEffects.TrackOptions;

namespace Rodaje.App.Views.Inspector.Tracking;

/// <summary>Un seguimiento de la lista tal como se muestra.</summary>
internal sealed partial class TrackItem(string effectId, string name, string note, Brush swatch) : IEquatable<TrackItem>
{
    public string EffectId { get; } = effectId;

    public string Name { get; } = name;

    public string Note { get; } = note;

    public Brush Swatch { get; } = swatch;

    public bool Equals(TrackItem? other) => other is not null && EffectId == other.EffectId && Name == other.Name && Note == other.Note;

    public override bool Equals(object? obj) => Equals(obj as TrackItem);

    public override int GetHashCode() => HashCode.Combine(EffectId, Name, Note);
}

/// <summary>
/// La pestaña «Seguimiento»: seguimientos guardados en el clip, seguir en vivo (un hilo de fondo escribe los cuadros
/// mientras avanza y el cursor lo acompaña; todo lo de una corrida es un paso del historial), la confianza por cuadro,
/// refinar y «Aplicar a…».
/// </summary>
internal sealed partial class TrackingPanel : UserControl, IEditorView
{
    private const string RunActionId = "seguimiento.seguir";

    private EditorServices? _services;
    private ClipPropertyEditor? _editor;
    private string? _clipId;
    private string? _trackId;
    private bool _updating;
    private string? _runMerge;
    private int _runs;
    // Lo que muestran ahora los desplegables (ver ComboBoxChoices): el panel se refresca con cada edición, también con la
    // lista abierta; rehacerlos entonces cerraba la app desde WinUI. Lo que no se tocó por eso se repite al cerrarla.
    private ChoiceList<TrackUse> _uses = new();
    private readonly ClosedListRetry _usesRetry = new();
    private ChoiceList<string?> _targets = new();
    private readonly ClosedListRetry _targetsRetry = new();
    private Time _shownPosition;
    private string? _shownTrack;

    public TrackingPanel()
    {
        InitializeComponent();
        UseBox.RetryWhenClosed(_usesRetry, Refresh);
        TargetBox.RetryWhenClosed(_targetsRetry, Refresh);
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            Publish();
            if (Visibility == Visibility.Visible)
            {
                Refresh();
            }
        });
    }

    public ObservableCollection<TrackItem> Items { get; } = [];

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        EditorSession session = services.Session;
        _editor = new ClipPropertyEditor(session);
        foreach (InspectorSection section in new[] { ListSection, RunSection, ConfidenceSection, RefineSection, ApplySection })
        {
            section.Remember(services.Settings);
        }

        session.SelectionChanged += (_, _) => Refresh();
        session.ProjectEdited += (_, _) => Refresh();
        session.ProjectOpened += (_, _) => Refresh();
        session.TransportChanged += (_, _) =>
        {
            if (session.Position != _shownPosition && Visibility == Visibility.Visible)
            {
                _shownPosition = session.Position;
                DrawGraph();
            }
        };
        services.LongActions.Changed += (_, _) => DispatcherQueue.RunLater(ShowRunning);
        Refresh();
        Bindings.Update();
    }

    private EditorSession Session => _services!.Session;

    private Clip? CurrentClip() => _clipId is not null && _services?.Session.Sequence.FindClip(_clipId) is { } found ? found.Clip : null;

    private EffectInstance? CurrentTrack() => CurrentClip()?.Effects.Find(e => e.Id == _trackId && e.Type == BuiltInEffects.Track);

    private Clip? Target()
    {
        if (_services is null)
        {
            return null;
        }

        foreach (string id in _services.Session.SelectedClipIds)
        {
            if (_services.Session.Sequence.FindClip(id) is { Track.Kind: TrackKind.Video } found && found.Clip.Generator is null && !found.Clip.IsCompound
                && _services.Session.Project.FindMedia(found.Clip.MediaId) is { Width: > 0 } media && !media.IsStillImage)
            {
                return found.Clip;
            }
        }

        return null;
    }

    private void Publish() => _services?.Session.SetTrackEditing(Visibility == Visibility.Visible && _clipId is not null, _clipId is null ? null : _trackId);

    private void Refresh()
    {
        if (_services is null || Visibility != Visibility.Visible)
        {
            return;
        }

        if (Target() is not { } clip)
        {
            _clipId = null;
            Items.Clear();
            bool isStillSelected = _services.Session.SelectedClipIds.Any(id =>
                _services.Session.Sequence.FindClip(id) is { } found
                && _services.Session.Project.FindMedia(found.Clip.MediaId)?.IsStillImage == true);
            EmptyText.Text = isStillSelected
                ? "No aplica a imágenes fijas."
                : "Selecciona un clip de video y lleva el cursor sobre él para seguir un punto, dos, cuatro esquinas o una región.";
            EmptyText.Visibility = Visibility.Visible;
            Header.Visibility = Visibility.Collapsed;
            Details.Visibility = Visibility.Collapsed;
            Publish();
            return;
        }

        _clipId = clip.Id;
        _shownPosition = Session.Position;
        EmptyText.Visibility = Visibility.Collapsed;
        Header.Visibility = Visibility.Visible;
        Details.Visibility = Visibility.Visible;
        ClipTitle.Text = ClipLabels.Name(Session.Project, clip, "Clip");
        ShowList(clip);
        ShowSelected(clip);
        Publish();
    }

    private void ShowList(Clip clip)
    {
        var next = new List<TrackItem>();
        foreach (EffectInstance effect in clip.Effects.Where(e => e.Type == BuiltInEffects.Track))
        {
            PointTrackKind kind = PointTrack.ParseKind(effect.Options.GetValueOrDefault(O.Kind));
            int frames = PointTrack.ParseCached(effect.Options.GetValueOrDefault(O.Samples))?.Count ?? 0;
            string note = KindName(kind) + (frames > 0 ? " · " + Frames(frames) : " · sin seguir");
            next.Add(new TrackItem(effect.Id, effect.Options.GetValueOrDefault(O.Name) ?? "Seguimiento", note,
                new SolidColorBrush(Color(effect.Options.GetValueOrDefault(O.Color)))));
        }

        if (_trackId is null || !next.Exists(i => i.EffectId == _trackId))
        {
            _trackId = next.Count > 0 ? next[^1].EffectId : null;
        }

        _updating = true;
        if (next.Count != Items.Count || !next.Zip(Items).All(p => p.First.Equals(p.Second)))
        {
            Items.Clear();
            foreach (TrackItem item in next)
            {
                Items.Add(item);
            }
        }

        List.SelectedItem = Items.FirstOrDefault(i => i.EffectId == _trackId);
        _updating = false;
        NoTracksText.Visibility = next.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = next.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ListSection.Summary = next.Count switch
        {
            0 => "ninguno",
            1 => "1 seguimiento",
            _ => $"{next.Count} seguimientos",
        };
    }

    private void ShowSelected(Clip clip)
    {
        // El resultado de seguir o aplicar es de un seguimiento: no queda a la vista al pasar a otro.
        if (_shownTrack != $"{_clipId}|{_trackId}")
        {
            _shownTrack = $"{_clipId}|{_trackId}";
            RunText.Text = ApplyText.Text = "";
        }

        if (CurrentTrack() is not { } track)
        {
            Selected.Visibility = Visibility.Collapsed;
            return;
        }

        Selected.Visibility = Visibility.Visible;
        PointTrackKind kind = PointTrack.ParseKind(track.Options.GetValueOrDefault(O.Kind));
        _updating = true;
        double threshold = Threshold(track);
        ThresholdSlider.Value = threshold;
        ThresholdText.Text = threshold.ToString("0.00", SpanishNumbers.Format);
        ShowUses(clip, kind);
        _updating = false;
        DrawGraph();
        ShowRunning();
    }

    private static string Frames(int count) => count == 1 ? "1 fotograma" : $"{count} fotogramas";

    private static double Threshold(EffectInstance track) =>
        track.Parameters.TryGetValue(BuiltInEffects.TrackParameters.Threshold, out Parameter? p) ? p.Value : 0.7;

    private void ShowUses(Clip clip, PointTrackKind kind)
    {
        TrackUse? requested = Session.RequestedTrackUse;
        TrackUse? chosen = requested ?? (_uses.TryGet(UseBox.SelectedIndex, out TrackUse shown) ? shown : null);
        (ChoiceList<TrackUse> uses, int selected) = TrackingChoices.ForKind(kind, chosen);
        _uses = ComboBoxChoices.Show(UseBox, _uses, uses, selected, retry: _usesRetry);
        ShowTargets(clip, requested is null ? null : Session.RequestedTrackTargetId);
        if (requested is not null)
        {
            Session.ClearTrackPanelRequest();
        }
    }

    private void ShowTargets(Clip clip, string? preferredTarget = null)
    {
        TrackUse use = _uses.TryGet(UseBox.SelectedIndex, out TrackUse picked) ? picked : TrackUse.FollowPosition;
        string? chosen = preferredTarget ?? (_targets.TryGet(TargetBox.SelectedIndex, out string? shown) ? shown : null);
        (ChoiceList<string?> targets, int selected) = TrackingChoices.Targets(Session.Project, Session.Sequence, clip, use, chosen);
        _targets = ComboBoxChoices.Show(TargetBox, _targets, targets, selected, retry: _targetsRetry);
        TargetBox.Visibility = _targets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutoCropBox.Visibility = use == TrackUse.Stabilize ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string KindName(PointTrackKind kind) => kind switch
    {
        PointTrackKind.Point => "Un punto",
        PointTrackKind.Two => "Dos puntos",
        PointTrackKind.Corners => "Cuatro esquinas",
        _ => "Región",
    };

    private static Windows.UI.Color Color(string? text) =>
        text is ['#', ..] && text.Length == 7 && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)
            ? ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : ColorHelper.FromArgb(255, 0xFF, 0xD5, 0x4F);

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && List.SelectedItem is TrackItem item && item.EffectId != _trackId)
        {
            _trackId = item.EffectId;
            Refresh();
        }
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag } || CurrentClip() is not { } clip
            || Session.Project.FindMedia(clip.MediaId) is not { Width: > 0, Height: > 0 } media)
        {
            return;
        }

        PointTrackKind kind = PointTrack.ParseKind(tag);
        int count = clip.Effects.Count(x => x.Type == BuiltInEffects.Track);
        SequenceEditCommand command = TrackEdits.Create(Session.SequenceId, clip.Id, kind, TrackEdits.DefaultPoints(kind, (double)media.Width / media.Height),
            $"Seguimiento {count + 1}", TrackEdits.Palette[count % TrackEdits.Palette.Length]);
        if (Session.Execute(command))
        {
            _trackId = command.CreatedIds[0];
            Refresh();
            Session.Notify(NotificationSeverity.Informational, "Nuevo seguimiento",
                "Lleva cada punto en el visor sobre algo con textura (una esquina, un logo, un ojo); el recuadro interior es el patrón y el punteado, dónde se busca. Después, «Adelante».");
        }
    }

    private void OnItemMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } button || CurrentClip() is not { } clip || clip.Effects.Find(x => x.Id == id) is not { } track)
        {
            return;
        }

        var menu = new MenuFlyout();
        void Item(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Item("Renombrar…", () => Rename(button, clip, track));
        Item("Borrar lo seguido", () => Session.Execute(TrackEdits.SetSamples(Session.SequenceId, clip.Id, id, null, "Borrar lo seguido")));
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("Quitar", () => Session.Execute(Edits.RemoveEffect(Session.SequenceId, clip.Id, id, description: "Quitar seguimiento")));
        menu.ShowAt(button);
    }

    private void Rename(FrameworkElement anchor, Clip clip, EffectInstance track)
    {
        var box = new TextBox { Text = track.Options.GetValueOrDefault(O.Name) ?? "", Width = 220, MaxLength = 120, PlaceholderText = "Nombre del seguimiento" };
        var flyout = new Flyout { Content = box };
        box.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                args.Handled = true;
                flyout.Hide();
                string name = box.Text.Trim();
                if (name.Length > 0)
                {
                    Session.Execute(Edits.SetEffectOption(Session.SequenceId, clip.Id, track.Id, O.Name, name, description: "Renombrar seguimiento"));
                }
            }
        };
        flyout.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        flyout.ShowAt(anchor);
    }

    // --- Seguir ---

    private void ShowRunning()
    {
        if (_services is null)
        {
            return;
        }

        bool running = _services.LongActions.IsRunning(RunActionId);
        ProgressRow.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RunButtons.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _services?.LongActions.Cancel(RunActionId);

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            UiTask.Run(RunAsync(forward: tag.StartsWith("forward", StringComparison.Ordinal), single: tag.EndsWith('1')));
        }
    }

    /// <summary>
    /// Sigue desde el cuadro del cursor con <see cref="PointTracking"/> y escribe los cuadros mientras avanza (el cursor los
    /// acompaña); lo de una corrida reemplaza solo su tramo y se deshace en un paso. Parar deja lo seguido hasta ahí.
    /// </summary>
    private async Task RunAsync(bool forward, bool single)
    {
        if (_services is null || _editor is null || CurrentClip() is not { } clip || CurrentTrack() is not { } track
            || Session.Project.FindMedia(clip.MediaId) is not { FrameRate: { } rate } media)
        {
            return;
        }

        if (clip.Speed is { Freeze: true } || !_editor.IsPlayheadInside(clip))
        {
            Session.Notify(NotificationSeverity.Informational, "Seguir", "Lleva el cursor sobre el clip (no un fotograma congelado) para seguir.");
            return;
        }

        PointTrackKind kind = PointTrack.ParseKind(track.Options.GetValueOrDefault(O.Kind));
        Time start = _editor.KeyframeTimeAtPlayhead(clip);
        TrackPointSpec[] points = TrackEdits.StartAt(track, start);
        if (points.Length != PointTrack.PointsOf(kind))
        {
            Session.Notify(NotificationSeverity.Warning, "Seguir", "Los puntos de este seguimiento no se pueden leer; crea uno nuevo.");
            return;
        }

        Time frame = Time.FromFrames(1, rate);
        Time limit = single ? (forward ? start + frame : start - frame) : forward ? clip.SourceOut - frame : clip.SourceIn;
        EffectInstance? crop = clip.Effects.Find(x => x.Enabled && x.Type == BuiltInEffects.Crop);
        var request = new PointTrackRequest(media.Path, start, limit, forward, kind, points, (float)Threshold(track),
            t => crop is null ? CropRect.None : CropGeometry.Sanitize(CropGeometry.Evaluate(crop, t)));
        PointTrack? existing = PointTrack.ParseCached(track.Options.GetValueOrDefault(O.Samples));
        if (existing is not null && existing.PointCount != points.Length)
        {
            existing = null;
        }

        var collected = new SortedDictionary<Time, TrackSample[]>();
        string clipId = clip.Id, trackId = track.Id;
        _runMerge = $"seguimiento-seguir|{trackId}|{++_runs}";
        int pending = 0, lost = 0, doubtful = 0, saved = 0;
        bool gone = false, unsaved = false;
        float threshold = request.Threshold;
        void Commit()
        {
            // Solo el cuadro de partida no es nada seguido: no deja paso en el historial.
            if (collected.Count > 1 && !unsaved)
            {
                var update = new PointTrack([.. collected.Keys], [.. collected.Values]);
                if (Session.TryExecute(TrackEdits.SetSamples(Session.SequenceId, clipId, trackId, existing?.Merge(update) ?? update,
                    forward ? "Seguir hacia adelante" : "Seguir hacia atrás", _runMerge)))
                {
                    saved = collected.Count - 1;
                }
                else
                {
                    // Ya no cabe (o el clip ya no está): seguir sería perder el cálculo sin aviso.
                    unsaved = true;
                    _services?.LongActions.Cancel(RunActionId);
                }
            }

            pending = 0;
        }

        void Tracked(PointTrackFrame f)
        {
            collected[f.Time] = f.Samples;
            lost += f.Samples.Any(s => s.IsLost) ? 1 : 0;
            doubtful += f.Samples.All(s => !s.IsLost) && f.Samples.Any(s => s.Confidence < threshold) ? 1 : 0;
            gone |= f.Gone;
            if (++pending >= 6)
            {
                Commit();
                if (Session.Sequence.FindClip(clipId) is { } found)
                {
                    Session.Seek(found.Clip.TimeMap.TimelineOf(f.Time));
                }
            }
        }

        RunText.Text = "";
        var progress = new Progress<double>(p => Progress.Value = p);
        await _services.LongActions.RunAsync(RunActionId, "Seguir", async scope =>
        {
            var both = new Progress<double>(p =>
            {
                ((IProgress<double>)progress).Report(p);
                scope.Progress.Report(p);
            });
            try
            {
                await PointTracking.RunAsync(request, f => DispatcherQueue.RunLater(() => Tracked(f)), both, scope.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException or ArgumentException
                or Rodaje.Media.MediaException)
            {
                Session.Notify(NotificationSeverity.Warning, "Seguir", ex.Message);
            }
        }, canCancel: true);

        // Lo último que llegó a la cola de la interfaz entra antes de guardar.
        await Task.Yield();
        Commit();
        if (collected.Count > 1 && Session.Sequence.FindClip(clipId) is { } done)
        {
            Session.Seek(done.Clip.TimeMap.TimelineOf(forward ? collected.Keys.Last() : collected.Keys.First()));
        }

        Session.BreakMerge();
        int frames = Math.Max(0, collected.Count - 1);
        RunText.Text = unsaved
            ? $"Se detuvo: lo seguido ya no se pudo guardar (es demasiado largo o el clip ya no está). Quedaron guardados {Frames(saved)}."
            : frames == 0
            ? "No había fotogramas para seguir en esa dirección."
            : $"Seguidos {Frames(frames)} hacia {(forward ? "adelante" : "atrás")}"
                + (doubtful > 0 ? $"; {doubtful} dudosos" : "")
                + (lost > 0 ? $"; en {lost} se perdió algún punto y se estimó" : "")
                + (gone ? ". Se detuvo porque no se encontraba ningún punto." : ".");
        ShowRunning();
    }

    // --- Confianza ---

    private void OnGraphSizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();

    private void DrawGraph()
    {
        Graph.Children.Clear();
        if (CurrentClip() is not { } clip || CurrentTrack() is not { } track)
        {
            return;
        }

        double width = Graph.ActualWidth, height = Graph.ActualHeight;
        PointTrack? samples = PointTrack.ParseCached(track.Options.GetValueOrDefault(O.Samples));
        double threshold = Threshold(track);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double span = Math.Max(1, (clip.SourceOut - clip.SourceIn).Ticks);
        double X(Time t) => Math.Clamp((t - clip.SourceIn).Ticks / span, 0, 1) * width;
        double Y(double confidence) => height - 2 - (Math.Clamp(confidence, 0, 1) * (height - 4));
        var muted = (Brush)Application.Current.Resources["TextMutedBrush"];
        Graph.Children.Add(new Line { X1 = 0, X2 = width, Y1 = Y(threshold), Y2 = Y(threshold), Stroke = muted, StrokeThickness = 1, StrokeDashArray = [3, 3] });
        int doubtful = 0, lost = 0;
        if (samples is not null)
        {
            var line = new Polyline { Stroke = (Brush)Application.Current.Resources["AccentBrush"], StrokeThickness = 1.2 };
            var bad = new SolidColorBrush(ColorHelper.FromArgb(200, 0xEF, 0x53, 0x50));
            var doubt = new SolidColorBrush(ColorHelper.FromArgb(170, 0xFF, 0xA7, 0x26));
            double step = Math.Max(1.5, width / span * (samples.Count > 1 ? (samples.Last - samples.First).Ticks / (double)(samples.Count - 1) : 1));
            for (int i = 0; i < samples.Count; i++)
            {
                float c = samples.ConfidenceAt(i);
                double x = X(samples.TimeAt(i));
                line.Points.Add(new Windows.Foundation.Point(x, Y(c)));
                if (c <= 0 || c < threshold)
                {
                    bool isLost = c <= 0;
                    lost += isLost ? 1 : 0;
                    doubtful += isLost ? 0 : 1;
                    var mark = new Rectangle { Width = step, Height = isLost ? height : 6, Fill = isLost ? bad : doubt };
                    Canvas.SetLeft(mark, x - (step / 2));
                    Canvas.SetTop(mark, isLost ? 0 : height - 6);
                    Graph.Children.Add(mark);
                }
            }

            Graph.Children.Add(line);
        }

        double cursor = X(clip.ToEffectTime(Session.Position));
        Graph.Children.Add(new Line { X1 = cursor, X2 = cursor, Y1 = 0, Y2 = height, Stroke = (Brush)Application.Current.Resources["TextBrush"], StrokeThickness = 1 });
        GraphText.Text = samples is null
            ? "Todavía no se siguió nada."
            : $"{Frames(samples.Count)} · {doubtful} dudosos · {lost} perdidos (línea punteada: umbral)";
        ConfidenceSection.Summary = samples is null ? "" : lost + doubtful == 0 ? "todo bien" : $"{lost + doubtful} por revisar";
        PreviousDoubtButton.IsEnabled = NextDoubtButton.IsEnabled = lost + doubtful > 0;
    }

    private void OnGraphPressed(object sender, PointerRoutedEventArgs e)
    {
        if (CurrentClip() is not { } clip || Graph.ActualWidth <= 0)
        {
            return;
        }

        double fraction = Math.Clamp(e.GetCurrentPoint(Graph).Position.X / Graph.ActualWidth, 0, 1);
        Time source = clip.SourceIn + Time.FromTicks((long)((clip.SourceOut - clip.SourceIn).Ticks * fraction));
        Seek(clip, source);
    }

    private void Seek(Clip clip, Time source)
    {
        Time timeline = clip.TimeMap.TimelineOf(source);
        Session.Seek(timeline < clip.Start ? clip.Start : timeline >= clip.End ? clip.End - Time.FromTicks(1) : timeline);
    }

    private void OnDoubtClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || CurrentClip() is not { } clip || CurrentTrack() is not { } track
            || PointTrack.ParseCached(track.Options.GetValueOrDefault(O.Samples)) is not { } samples)
        {
            return;
        }

        double threshold = Threshold(track);
        Time now = clip.ToEffectTime(Session.Position);
        int found = -1;
        for (int i = 0; i < samples.Count; i++)
        {
            bool doubt = samples.ConfidenceAt(i) < threshold;
            if (doubt && (tag == "1" ? samples.TimeAt(i) > now && found < 0 : samples.TimeAt(i) < now))
            {
                found = i;
            }
        }

        if (found >= 0)
        {
            Seek(clip, samples.TimeAt(found));
        }
    }

    private void OnThresholdChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ThresholdText is null)
        {
            return;
        }

        ThresholdText.Text = e.NewValue.ToString("0.00", SpanishNumbers.Format);
        if (_updating || CurrentClip() is not { } clip || CurrentTrack() is not { } track)
        {
            return;
        }

        Session.Execute(Edits.SetParameter(Session.SequenceId, clip.Id, track.Id, BuiltInEffects.TrackParameters.Threshold, Math.Round(e.NewValue, 2),
            clip.ToEffectTime(Session.Position), "Cambiar el umbral de confianza"));
    }

    // --- Refinar ---

    private PointTrack? Samples(out Clip clip, out EffectInstance track)
    {
        clip = CurrentClip()!;
        track = CurrentTrack()!;
        PointTrack? samples = track is null ? null : PointTrack.ParseCached(track.Options.GetValueOrDefault(O.Samples));
        if (samples is null)
        {
            Session.Notify(NotificationSeverity.Informational, "Seguimiento", "Primero sigue algo con «Adelante» o «Atrás».");
        }

        return samples;
    }

    private void OnFillClick(object sender, RoutedEventArgs e)
    {
        if (Samples(out Clip clip, out EffectInstance track) is { } samples)
        {
            Session.Execute(TrackEdits.SetSamples(Session.SequenceId, clip.Id, track.Id, samples.FillDoubtful((float)Threshold(track)), "Rellenar los dudosos"));
        }
    }

    private void OnSmoothClick(object sender, RoutedEventArgs e)
    {
        if (Samples(out Clip clip, out EffectInstance track) is { } samples)
        {
            Session.Execute(TrackEdits.SetSamples(Session.SequenceId, clip.Id, track.Id,
                samples.Smooth((int)SmoothSlider.Value, (float)Threshold(track)), "Suavizar el seguimiento"));
        }
    }

    private void OnSmoothValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (SmoothText is not null)
        {
            SmoothText.Text = "±" + ((int)e.NewValue).ToString(SpanishNumbers.Format);
        }
    }

    private void OnClearFromHereClick(object sender, RoutedEventArgs e)
    {
        if (Samples(out Clip clip, out EffectInstance track) is { } samples)
        {
            Time now = clip.ToEffectTime(Session.Position);
            Session.Execute(TrackEdits.SetSamples(Session.SequenceId, clip.Id, track.Id, samples.Slice(samples.First, now),
                "Borrar lo seguido después del cursor"));
        }
    }

    // --- Aplicar ---

    private void OnUseSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && TargetBox is not null && CurrentClip() is { } clip)
        {
            ShowTargets(clip);
        }
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (CurrentClip() is not { } clip || CurrentTrack() is not { } track || !_uses.TryGet(UseBox.SelectedIndex, out TrackUse use))
        {
            return;
        }

        string? target = _targets.TryGet(TargetBox.SelectedIndex, out string? picked) ? picked : null;
        TrackApplication? application = TrackApply.Prepare(Session.Project, Session.Sequence, clip, track.Id, use, Session.Position, target,
            AutoCropBox.IsChecked == true, out string problem);
        if (application is null)
        {
            ApplyText.Text = problem;
            return;
        }

        if (Session.Execute(application.Command))
        {
            ApplyText.Text = $"Listo: {application.Keyframes} keyframes por parámetro, en un paso del historial."
                + (application.Zoom > 1.0001 ? $" Acercamiento para no ver bordes: {((application.Zoom - 1) * 100).ToString("0.#", SpanishNumbers.Format)} %." : "")
                + (application.Zoom >= TrackApply.MaxStabilizeZoom ? " El movimiento es grande: aun así puede verse algún borde." : "");
        }
    }
}
