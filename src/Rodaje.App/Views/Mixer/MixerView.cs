using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services;
using Rodaje.App.Services.Audio;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Shell;
using Rodaje.App.Theme;
using Rodaje.Audio.Metering;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;

namespace Rodaje.App.Views.Mixer;

/// <summary>
/// El mezclador: una tira por pista de audio, una por bus y la de la mezcla, con medidores de lo que suena según el reloj
/// del dispositivo. Ocupa el lugar de la línea de tiempo (Ver › Mezclador, Alt+M). Cada cambio es un comando deshacible;
/// el A/B de las inserciones y lo que se mide solo cambian la vista previa.
/// </summary>
internal sealed partial class MixerView : UserControl, IEditorView, IStripHost
{
    private static readonly TimeSpan ReadInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMilliseconds(50);

    private readonly StackPanel _strips = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _masterHost = new();
    private readonly TextBlock _loudness = new();
    private readonly TextBlock _truePeak = new();
    private readonly HashSet<string> _bypassed = new(StringComparer.Ordinal);
    private readonly LoudnessIntegrator _integrated = new();
    private readonly List<StripView> _views = [];
    private EditorServices? _services;
    private StripView? _master;
    private string _layout = "";
    private bool _animating;
    private long _lastFrame;
    private long _lastRead;
    private long _lastTouch;
    private string? _gesture;
    private TouchRecorder? _touch;
    private double _touchValue;
    private InsertEditor? _openEditor;

    public MixerView()
    {
        var root = new Grid { Background = ThemeResources.Brush("BackgroundBrush") };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new Grid { Style = ThemeResources.Style("PanelHeaderStyle"), ColumnSpacing = 8, Padding = new Thickness(12, 4, 12, 4) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.Children.Add(new TextBlock { Text = "Mezclador", Style = ThemeResources.Style("PanelTitleTextStyle"), VerticalAlignment = VerticalAlignment.Center });
        var addBus = new DropDownButton { Content = "Añadir bus" };
        var busMenu = new MenuFlyout();
        busMenu.Items.Add(Item("Voces, Música y Efectos", () => AddBuses(MixerModel.StandardBuses)));
        busMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (string name in MixerModel.StandardBuses)
        {
            busMenu.Items.Add(Item(name, () => AddBuses([name])));
        }

        busMenu.Items.Add(Item("Otro…", () => UiTask.Run(async () =>
        {
            if (await AskNameAsync("Bus nuevo", "") is { } named)
            {
                AddBuses([named]);
            }
        })));
        addBus.Flyout = busMenu;
        ToolTipService.SetToolTip(addBus, "Un bus suma pistas (su salida o un envío) y les aplica sus efectos y su fader: una submezcla.");
        Grid.SetColumn(addBus, 1);
        bar.Children.Add(addBus);
        var help = new TextBlock
        {
            Text = "Las inserciones se aplican de arriba abajo · T: automatización «tocar» · Ctrl+S: solo exclusivo",
            Style = ThemeResources.Style("CaptionTextStyle"),
            Foreground = ThemeResources.Brush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(help, 2);
        bar.Children.Add(help);
        root.Children.Add(bar);

        var body = new Grid { Padding = new Thickness(8), ColumnSpacing = 8 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var scroller = new ScrollViewer
        {
            Content = _strips,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            // En ventanas bajas la tira con envíos e inserciones no cabe: se desplaza en vez de cortar el fader.
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        body.Children.Add(scroller);
        var masterColumn = new Grid { RowSpacing = 4 };
        masterColumn.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        masterColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        masterColumn.Children.Add(_masterHost);
        _loudness.Style = ThemeResources.Style("TimecodeTextStyle");
        _loudness.FontSize = ThemeResources.Size("CaptionFontSize");
        _truePeak.Style = ThemeResources.Style("TimecodeTextStyle");
        _truePeak.FontSize = ThemeResources.Size("CaptionFontSize");
        _truePeak.Foreground = ThemeResources.Brush("TextMutedBrush");
        var readouts = new StackPanel { Background = ThemeResources.Brush("TransparentBrush") };
        readouts.Children.Add(_loudness);
        readouts.Children.Add(_truePeak);
        readouts.Tapped += (_, _) => ResetReadouts();
        ToolTipService.SetToolTip(readouts, "Sonoridad (EBU R128): corta (3 s) e integrada desde el último clic, y el mayor pico verdadero. Clic para volver a empezar.");
        Grid.SetRow(readouts, 1);
        masterColumn.Children.Add(readouts);
        Grid.SetColumn(masterColumn, 1);
        body.Children.Add(masterColumn);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;
        ResetReadouts();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => OnVisibility());
    }

    public IReadOnlyList<AudioBus> Buses => _services?.Session.Sequence.AudioBuses ?? [];

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        EditorSession session = services.Session;
        session.ProjectEdited += (_, _) => Refresh();
        session.ProjectOpened += (_, _) =>
        {
            _bypassed.Clear();
            services.Player.BypassInserts(null);
            Refresh();
        };
        session.TransportChanged += (_, _) =>
        {
            if (Visibility == Visibility.Visible)
            {
                StartAnimating();
            }
        };
        Refresh();
        OnVisibility();
    }

    private EditorSession Session => _services!.Session;

    private void OnVisibility()
    {
        if (_services is null)
        {
            return;
        }

        if (Visibility == Visibility.Visible)
        {
            Refresh();
            StartAnimating();
        }
        else
        {
            StopAnimating();
            EndGesture();
            if (_bypassed.Count > 0)
            {
                _bypassed.Clear();
                _services.Player.BypassInserts(null);
                Refresh();
            }
        }
    }

    /// <summary>Rehace las tiras si cambió cuáles hay o qué llevan; si no, solo sus valores.</summary>
    private void Refresh()
    {
        if (_services is null || Visibility != Visibility.Visible)
        {
            return;
        }

        IReadOnlyList<MixerStrip> strips = MixerModel.Strips(Session.Sequence, Session.Position);
        string layout = string.Join("|", strips.Select(s => s.Kind + s.OwnerId)) + "/" + string.Join("|", Buses.Select(b => b.Id + b.Name));
        if (layout != _layout)
        {
            _layout = layout;
            _strips.Children.Clear();
            _views.Clear();
            foreach (MixerStrip strip in strips.Where(s => s.Kind != StripKind.Master))
            {
                if (strip.Kind == StripKind.Bus && _views.Count > 0 && _views[^1].Strip.Kind == StripKind.Track)
                {
                    _strips.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 0, 8, 0), Background = ThemeResources.Brush("StrokeBrush") });
                }

                var view = new StripView(this, strip);
                _views.Add(view);
                _strips.Children.Add(view);
            }

            _master = new StripView(this, strips[^1]);
            _masterHost.Children.Clear();
            _masterHost.Children.Add(_master);
            if (_views.Count == 0)
            {
                _strips.Children.Add(new TextBlock
                {
                    Text = "No hay pistas de audio. Añade una en la línea de tiempo.",
                    Foreground = ThemeResources.Brush("TextMutedBrush"),
                    Margin = new Thickness(12),
                });
            }

            return;
        }

        foreach (StripView view in _views)
        {
            if (strips.FirstOrDefault(s => s.OwnerId == view.Strip.OwnerId) is { } strip)
            {
                view.Update(strip);
            }
        }

        _master?.Update(strips[^1]);
    }

    private void StartAnimating()
    {
        if (_animating || _services is null)
        {
            return;
        }

        _animating = true;
        _lastFrame = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimating()
    {
        if (_animating)
        {
            _animating = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, object e)
    {
        if (_services is not { } services)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(_lastFrame, now).TotalSeconds;
        _lastFrame = now;
        bool idle = !services.Session.IsPlaying;
        foreach (StripView view in _views)
        {
            view.Meter.Update(view.Strip.MeterKey is { } key ? services.Player.MeasureStrip(key) : null, elapsed);
            idle &= view.Meter.IsIdle;
        }

        AudioLevels? mix = services.Player.MeasureMixNow();
        _master?.Meter.Update(mix, elapsed, truePeak: true);
        idle &= _master?.Meter.IsIdle ?? true;

        if (_touch is not null && Stopwatch.GetElapsedTime(_lastTouch, now) >= TouchInterval)
        {
            _lastTouch = now;
            WriteTouch();
        }

        if (Stopwatch.GetElapsedTime(_lastRead, now) >= ReadInterval)
        {
            _lastRead = now;
            UpdateLoudness(services.Player.TakeLoudness());
            if (services.Session.IsPlaying && _touch is null)
            {
                Refresh();
            }

            _openEditor?.ShowReading(services.Player.TakeDynamics());
        }

        if (idle)
        {
            StopAnimating();
        }
    }

    private void UpdateLoudness((double Momentary, double ShortTerm)? powers)
    {
        if (powers is { } p)
        {
            _integrated.Add(p.Momentary);
        }

        double shortTerm = powers is { } s ? Loudness.ToLufs(s.ShortTerm) : double.NegativeInfinity;
        _loudness.Text = $"{Lufs(shortTerm)} · I {Lufs(_integrated.IntegratedLufs)} LUFS";
        double peak = _master?.Meter.MaxPeak ?? double.NegativeInfinity;
        _truePeak.Text = "Pico " + (double.IsFinite(peak) ? peak.ToString("+0.0;−0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',') : "−∞") + " dBTP";
    }

    private void ResetReadouts()
    {
        _integrated.Reset();
        _master?.Meter.ResetClip();
        UpdateLoudness(null);
    }

    private static string Lufs(double lufs) =>
        double.IsFinite(lufs) && lufs > -70 ? lufs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',').Replace('-', '−') : "−∞";

    // ── IStripHost ──────────────────────────────────────────────────────────────────────────────────────────────────

    public bool IsBypassed(string effectId) => _bypassed.Contains(effectId);

    public void SetBypassed(string effectId, bool bypassed)
    {
        if (bypassed ? _bypassed.Add(effectId) : _bypassed.Remove(effectId))
        {
            _services?.Player.BypassInserts(_bypassed.Count == 0 ? null : new HashSet<string>(_bypassed, StringComparer.Ordinal));
            Refresh();
        }
    }

    public void FaderStarted(MixerStrip strip, string parameter)
    {
        EndGesture();
        StripView? view = _views.Find(v => v.Strip.OwnerId == strip.OwnerId);
        if (strip.Kind != StripKind.Master && view?.Touch == true && Session.IsPlaying)
        {
            _touch = new TouchRecorder(strip.OwnerId, parameter, Session.Position);
            _touchValue = parameter switch { A.Pan => strip.Pan, A.Balance => strip.Balance, _ => strip.GainDb };
        }

        _gesture = parameter switch
        {
            _ when _touch is not null => parameter == A.GainDb ? "Automatizar volumen" : "Automatizar paneo",
            A.Pan => "Paneo",
            A.Balance => "Balance",
            _ => "Volumen",
        };
        Session.BeginGesture(_gesture);
    }

    public void FaderChanging(MixerStrip strip, string parameter, double value)
    {
        if (_touch is not null)
        {
            _touchValue = value;
            return;
        }

        if (strip.Kind == StripKind.Master)
        {
            Session.TryExecute(Edits.SetMasterGain(Session.SequenceId, value, "Ganancia de la mezcla"));
        }
        else
        {
            Session.TryExecute(Edits.SetFader(Session.SequenceId, strip.OwnerId, parameter, value, Session.Position));
        }
    }

    public void FaderCompleted(MixerStrip strip, string parameter)
    {
        if (_touch is not null)
        {
            WriteTouch();
            _touch = null;
        }

        EndGesture();
    }

    /// <summary>Lo tocado hasta ahora va a la automatización; el gesto lo deja en un solo paso al soltar.</summary>
    private void WriteTouch()
    {
        if (_touch is not { } touch)
        {
            return;
        }

        touch.Add(Session.Position, _touchValue);
        if (touch.Command(Session.SequenceId) is { } command)
        {
            Session.TryExecute(command);
        }
    }

    private void EndGesture()
    {
        if (_gesture is not null)
        {
            _gesture = null;
            Session.EndGesture();
        }
    }

    public void ToggleMute(MixerStrip strip) => Session.Execute(strip.Kind == StripKind.Bus
        ? Edits.SetBusState(Session.SequenceId, strip.OwnerId, muted: !strip.Muted)
        : Edits.SetTrackState(Session.SequenceId, strip.OwnerId, muted: !strip.Muted));

    public void ToggleSolo(MixerStrip strip, bool exclusive) => Session.Execute(exclusive
        ? Edits.SoloExclusive(Session.SequenceId, strip.OwnerId)
        : strip.Kind == StripKind.Bus
            ? Edits.SetBusState(Session.SequenceId, strip.OwnerId, solo: !strip.Solo)
            : Edits.SetTrackMix(Session.SequenceId, strip.OwnerId, solo: !strip.Solo));

    public void ToggleArm(MixerStrip strip) => Session.Execute(Edits.SetTrackMix(Session.SequenceId, strip.OwnerId, armed: !strip.Armed));

    public void SetOutput(MixerStrip strip, string? busId)
    {
        if (strip.Output != busId)
        {
            Session.Execute(Edits.SetTrackOutput(Session.SequenceId, strip.OwnerId, busId));
        }
    }

    public void AddSend(MixerStrip strip, string busId) => Session.Execute(Edits.SetSend(Session.SequenceId, strip.OwnerId, busId, gainDb: -10));

    public void SendChanging(MixerStrip strip, string busId, double gainDb, bool dragging)
    {
        if (strip.Sends.FirstOrDefault(s => s.BusId == busId) is { } send && Math.Abs(send.GainDb - gainDb) > 1e-9)
        {
            Session.TryExecute(Edits.SetSend(Session.SequenceId, strip.OwnerId, busId, gainDb: gainDb));
        }

        if (!dragging)
        {
            Session.BreakMerge();
        }
    }

    public void SetSendPre(MixerStrip strip, string busId, bool preFader) =>
        Session.Execute(Edits.SetSend(Session.SequenceId, strip.OwnerId, busId, preFader: preFader));

    public void RemoveSend(MixerStrip strip, string busId) => Session.Execute(Edits.RemoveSend(Session.SequenceId, strip.OwnerId, busId));

    public void AddInsert(MixerStrip strip, string type)
    {
        var command = Edits.AddEffect(Session.SequenceId, strip.OwnerId, type);
        if (Session.Execute(command) && _views.Concat(_master is null ? [] : [_master]).FirstOrDefault(v => v.Strip.OwnerId == strip.OwnerId) is { } view
            && MixerModel.Strips(Session.Sequence, Session.Position).FirstOrDefault(s => s.OwnerId == strip.OwnerId) is { } updated
            && updated.Inserts.FirstOrDefault(e => e.Id == command.CreatedIds[0]) is { } effect)
        {
            OpenInsert(updated, effect, view);
        }
    }

    public void OpenInsert(MixerStrip strip, EffectInstance effect, FrameworkElement anchor)
    {
        if (_services is not { } services)
        {
            return;
        }

        var editor = new InsertEditor(services.Session, strip.OwnerId, effect, IsBypassed(effect.Id), on => SetBypassed(effect.Id, on));
        if (effect.Type == BuiltInEffects.Compressor)
        {
            var open = new Button { Content = "Abrir en Dinámica" };
            open.Click += (_, _) => OpenDynamics(strip);
            editor.Children.Add(open);
        }

        var flyout = new Flyout { Content = editor, Placement = FlyoutPlacementMode.RightEdgeAlignedTop };
        flyout.Opened += (_, _) =>
        {
            _openEditor = editor;
            services.Player.MonitorDynamics(new DynamicsMonitor(strip.MonitorKey) { Insert = effect.Id });
            StartAnimating();
        };
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openEditor, editor))
            {
                _openEditor = null;
                services.Player.MonitorDynamics(null);
            }
        };
        flyout.ShowAt(anchor);
    }

    public void SetInsertEnabled(MixerStrip strip, EffectInstance effect, bool enabled)
    {
        if (effect.Enabled != enabled)
        {
            Session.Execute(Edits.SetEffectEnabled(Session.SequenceId, strip.OwnerId, effect.Id, enabled));
        }
    }

    public void RemoveInsert(MixerStrip strip, EffectInstance effect)
    {
        SetBypassed(effect.Id, false);
        Session.Execute(Edits.RemoveEffect(Session.SequenceId, strip.OwnerId, effect.Id));
    }

    public void ApplyPreset(MixerStrip strip, EffectInstance effect, InsertPreset preset) =>
        Session.Execute(Edits.ApplyInsertPreset(Session.SequenceId, strip.OwnerId, effect.Id, preset));

    public void ReorderInserts(MixerStrip strip, string movedId, IReadOnlyList<string> order)
    {
        if (MixerModel.Effects(Session.Sequence, strip) is not { } effects)
        {
            return;
        }

        int index = MixerModel.MoveIndex(effects, movedId, order);
        if (effects.FindIndex(e => e.Id == movedId) != index)
        {
            Session.Execute(Edits.MoveEffect(Session.SequenceId, strip.OwnerId, movedId, index));
        }
        else
        {
            _layout = "";
            Refresh();
        }
    }

    public void RenameBus(MixerStrip strip) => UiTask.Run(RenameBusAsync(strip));

    private async Task RenameBusAsync(MixerStrip strip)
    {
        if (await AskNameAsync("Renombrar bus", strip.Name) is { } name && name != strip.Name)
        {
            Session.Execute(Edits.SetBusState(Session.SequenceId, strip.OwnerId, name: name));
        }
    }

    public void RemoveBus(MixerStrip strip) => Session.Execute(Edits.RemoveBus(Session.SequenceId, strip.OwnerId));

    public void OpenDynamics(MixerStrip strip)
    {
        DynamicsOwner owner = strip.Kind switch
        {
            StripKind.Track => new DynamicsOwner(DynamicsOwnerKind.Track, strip.OwnerId),
            StripKind.Bus => new DynamicsOwner(DynamicsOwnerKind.Bus, strip.OwnerId),
            _ => DynamicsOwner.Mix(Session.Sequence),
        };
        Session.ShowDynamics(owner);
    }

    private void AddBuses(IReadOnlyList<string> names)
    {
        IReadOnlyList<string> missing = [.. names.Where(n => !Buses.Any(b => b.Name == n))];
        if (missing.Count > 0)
        {
            Session.Execute(Edits.AddBuses(Session.SequenceId, missing));
        }
    }

    private async Task<string?> AskNameAsync(string title, string current)
    {
        var box = new TextBox { Text = current, PlaceholderText = "Nombre del bus", MaxLength = 64 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = "Aceptar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await DialogGate.ShowAsync(dialog).ConfigureAwait(true) == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text)
            ? box.Text.Trim()
            : null;
    }

    private static MenuFlyoutItem Item(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        return item;
    }
}
