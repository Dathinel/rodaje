using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.App.Shell;
using Rodaje.App.Views.Inspector.Properties;
using Rodaje.Core.Commands;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Packages;
using Rodaje.Render.Effects;

namespace Rodaje.App.Views.Inspector.Filters;

/// <summary>Un efecto de la pila tal como se muestra.</summary>
internal sealed partial class FilterItem : IEquatable<FilterItem>
{
    public FilterItem(EffectInstance effect, EffectSupport support, int index, int count, bool previewOff = false,
        EffectCostEstimate? cost = null, bool reorderable = true)
    {
        EffectId = effect.Id;
        Name = EffectCatalog.DisplayName(effect.Type);
        IsEnabled = effect.Enabled;
        IsDisabled = !effect.Enabled;
        EnabledGlyph = effect.Enabled ? "" : "";
        EnabledTip = (effect.Enabled ? "Desactivar «" : "Activar «") + Name + "»";
        TextOpacity = effect.Enabled ? 1 : 0.5;
        Note = support switch
        {
            EffectSupport.Pending => "Todavía no se aplica",
            EffectSupport.Shadowed => effect.Type == BuiltInEffects.Blend
                ? "Solo se aplica el primer modo de mezcla activo"
                : "Solo se aplica la primera transformación activa",
            EffectSupport.Unknown => "De un complemento que no está: se conserva sin aplicarse",
            _ when effect.Type == BuiltInEffects.Blend => EffectUi.BlendModeName(
                effect.Options.GetValueOrDefault(BuiltInEffects.BlendOptions.Mode, "normal")),
            _ => "",
        };
        NoteVisibility = Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CanMoveUp = index > 0;
        CanMoveDown = index < count - 1;
        IsPreviewOff = previewOff;
        bool image = cost is { Milliseconds: > 0 };
        CompareVisibility = image && effect.Enabled ? Visibility.Visible : Visibility.Collapsed;
        CostVisibility = image ? Visibility.Visible : Visibility.Collapsed;
        HandleVisibility = reorderable ? Visibility.Visible : Visibility.Collapsed;
        if (cost is { } c)
        {
            CostGlyph = c.Level switch
            {
                CostLevel.High => "●●●",
                CostLevel.Medium => "●●○",
                _ => "●○○",
            };
            string level = c.Level switch
            {
                CostLevel.High => "alto",
                CostLevel.Medium => "medio",
                _ => "bajo",
            };
            CostTip = string.Create(Services.Format.SpanishNumbers.Format,
                $"Costo {level}: unos {c.Milliseconds:0.00} ms de GPU por cuadro en la resolución de la secuencia (medido en una RTX 3050).");
            CostBrush = Theme.ThemeResources.Brush(c.Level == CostLevel.High ? "KeyframeBrush" : "TextMutedBrush");
        }
    }

    public bool IsPreviewOff { get; }

    public Visibility CompareVisibility { get; }

    public Visibility CostVisibility { get; }

    public Visibility HandleVisibility { get; }

    public string CostGlyph { get; } = "";

    public string CostTip { get; } = "";

    public Microsoft.UI.Xaml.Media.Brush? CostBrush { get; }

    public string EffectId { get; }

    public string Name { get; }

    public bool IsEnabled { get; }

    public bool IsDisabled { get; }

    public string EnabledGlyph { get; }

    public string EnabledTip { get; }

    public double TextOpacity { get; }

    public string Note { get; }

    public Visibility NoteVisibility { get; }

    public bool CanMoveUp { get; }

    public bool CanMoveDown { get; }

    public bool Equals(FilterItem? other) =>
        other is not null && EffectId == other.EffectId && Name == other.Name && IsEnabled == other.IsEnabled
        && Note == other.Note && CanMoveUp == other.CanMoveUp && CanMoveDown == other.CanMoveDown
        && IsPreviewOff == other.IsPreviewOff && CostGlyph == other.CostGlyph && CostTip == other.CostTip;

    public override bool Equals(object? obj) => Equals(obj as FilterItem);

    public override int GetHashCode() => HashCode.Combine(EffectId, IsEnabled, Note, CanMoveUp, CanMoveDown);
}

/// <summary>
/// Pila de filtros del clip seleccionado y los parámetros del filtro elegido. Todo cambio es un comando deshacible;
/// restablecer y quitar keyframes avisan como en Propiedades (D-024).
/// </summary>
internal sealed partial class FiltersPanel : UserControl, IEditorView
{
    private const string LoadCubeLabel = "Cargar .cube…";

    private readonly List<ParameterRow> _rows = [];
    private readonly List<string?> _lutValues = [];
    private EditorServices? _services;
    private ClipPropertyEditor? _editor;
    private string? _clipId;
    private TrackKind _kind;
    private IReadOnlyList<EffectDescriptor> _addable = [];
    private string? _effectId;
    private string? _rowsType;
    private ParameterSpec.Context _rowsContext;
    private (PackageScan Scan, string Current)? _lutShown;
    private ParameterRow? _dragRow;
    private Time _shownPosition;
    private bool _updating;

    public FiltersPanel()
    {
        InitializeComponent();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            PublishCropEditing();
            if (Visibility == Visibility.Visible)
            {
                QueuePresetRefresh();
            }
            else
            {
                SetMaskView(false);
            }
        });
        Wheels.WheelChanged += OnWheelChanged;
        CurveEditor.CurveChanged += OnCurveChanged;
    }

    public ObservableCollection<FilterItem> Items { get; } = [];

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        StackSection.Remember(services.Settings);
        EditorSession session = services.Session;
        _editor = new ClipPropertyEditor(session);
        session.SelectionChanged += (_, _) => Refresh();
        session.ProjectEdited += (_, _) => Refresh();
        session.ProjectOpened += (_, _) => Refresh();
        session.TransportChanged += (_, _) =>
        {
            if (session.Position != _shownPosition && Details.Visibility == Visibility.Visible && Visibility == Visibility.Visible)
            {
                Refresh();
            }
        };
        services.Packages.Changed += (_, _) =>
        {
            Refresh();
            QueuePresetRefresh();
        };
        session.PreviewOffChanged += (_, _) => Refresh();
        PresetsSection.Remember(services.Settings);

        Refresh();
        Bindings.Update();
    }

    private (Track Track, Clip Clip)? Target()
    {
        if (_services is null)
        {
            return null;
        }

        Sequence sequence = _services.Session.Sequence;
        foreach (string id in _services.Session.SelectedClipIds)
        {
            if (sequence.FindClip(id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private Clip? CurrentClip() =>
        _clipId is not null && _services?.Session.Sequence.FindClip(_clipId) is { } found ? found.Clip : null;

    private void Refresh()
    {
        if (_services is null || _editor is null)
        {
            return;
        }

        EditorSession session = _services.Session;
        (Track Track, Clip Clip)? target = Target();
        bool any = target is not null;
        EmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        Header.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (target is not { } found)
        {
            FinishChanges();
            LeaveClip();
            _clipId = null;
            _effectId = null;
            Items.Clear();
            NoFiltersText.Visibility = Visibility.Collapsed;
            Details.Visibility = Visibility.Collapsed;
            PublishCropEditing();
            return;
        }

        (Track track, Clip clip) = found;
        if (clip.Id != _clipId)
        {
            FinishChanges();
            _effectId = null;
            LeaveClip();
            QueuePresetRefresh();
        }

        _clipId = clip.Id;
        _shownPosition = session.Position;
        ClipTitle.Text = ClipLabels.Name(session.Project, clip, "Clip");
        ToolTipService.SetToolTip(ClipTitle, ClipTitle.Text);
        TitleNote.Visibility = clip.Generator is null ? Visibility.Collapsed : Visibility.Visible;
        if (_kind != track.Kind || _addable.Count == 0)
        {
            _kind = track.Kind;
            _addable = EffectCatalog.For(track.Kind);
            _updating = true;
            AddBox.Items.Clear();
            foreach (EffectDescriptor descriptor in _addable)
            {
                AddBox.Items.Add(descriptor.Name);
            }

            AddBox.SelectedIndex = -1;
            _updating = false;
        }

        ShowList(track, clip);
        ShowDetails(clip);
    }

    private void ShowList(Track track, Clip clip)
    {
        var next = new List<FilterItem>(clip.Effects.Count);
        for (int i = 0; i < clip.Effects.Count; i++)
        {
            EffectInstance effect = clip.Effects[i];
            if (Shows(effect))
            {
                next.Add(new FilterItem(effect, EffectCatalog.Support(clip, effect, track.Kind), i, clip.Effects.Count,
                    _services!.Session.IsPreviewOff(effect.Id), track.Kind == TrackKind.Video ? Cost(effect) : null, true));
            }
        }

        if (_effectId is null || !clip.Effects.Exists(e => e.Id == _effectId && Shows(e)))
        {
            _effectId = next.Count > 0 ? next[0].EffectId : null;
        }

        _updating = true;
        // Arrastrar un deslizador edita el proyecto en cada paso: solo se rehace la lista si cambió.
        if (next.Count != Items.Count || !next.Zip(Items).All(p => p.First.Equals(p.Second)))
        {
            Items.Clear();
            foreach (FilterItem item in next)
            {
                Items.Add(item);
            }
        }

        List.SelectedItem = Items.FirstOrDefault(i => i.EffectId == _effectId);
        _updating = false;
        NoFiltersText.Visibility = next.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int active = next.Count(i => i.IsEnabled);
        StackSection.Summary = next.Count switch
        {
            0 => "ninguno",
            1 => active == 1 ? "1 filtro" : "1 filtro, desactivado",
            _ => active == next.Count ? $"{next.Count} filtros" : $"{next.Count} filtros, {active} activos",
        };
    }

    private void ShowDetails(Clip clip)
    {
        EffectInstance? effect = _effectId is null ? null : clip.Effects.Find(e => e.Id == _effectId);
        PublishCropEditing();
        if (effect is null || _editor is not { } editor || _services is null)
        {
            Details.Visibility = Visibility.Collapsed;
            return;
        }

        Details.Visibility = Visibility.Visible;
        DetailsSection.Title = EffectUi.Name(effect.Type);
        EffectDefinition? definition = EffectRegistry.Find(effect.Type);
        Sequence sequence = _services.Session.Sequence;
        var context = new ParameterSpec.Context(sequence.Width, sequence.Height, clip.Duration.ToSeconds());
        if (_rowsType != effect.Type || _rowsContext != context)
        {
            BuildRows(effect.Type, context);
        }

        bool inside = editor.IsPlayheadInside(clip);
        int keyframes = 0;
        foreach (ParameterRow row in _rows)
        {
            ParameterState state = editor.Read(clip, effect.Type, row.Spec.Name, row.Spec.DefaultValue, effect.Id);
            keyframes += state.KeyframeCount;
            row.Show(state, inside);
        }

        DetailsSection.KeyframeSummary = keyframes == 0 ? "" : KeyframeDialog.Count(keyframes);
        ResetAllButton.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResetAllButton.IsEnabled = ClipPropertyEditor.CanReset(clip, effect.Type, Defaults(), effect.Id);

        string note = definition switch
        {
            null => "Este filtro es de un complemento que no está instalado: se conserva en el proyecto sin aplicarse.",
            { Stage: EffectStage.Deferred } => "Se guarda con el proyecto; el motor todavía no lo aplica.",
            { Type: BuiltInEffects.Blend } => "El modo se elige en Propiedades, sección «Opacidad y mezcla».",
            _ => "",
        };
        if (effect.Options.TryGetValue(BuiltInEffects.CommonOptions.Source, out string? source))
        {
            note = (note + " Copiado de «" + LutReference.DisplayName(source, _services.Packages.Installed) + "».").Trim();
        }

        if (!effect.Enabled)
        {
            note = ("Está desactivado: los cambios se guardan pero no se ven. " + note).Trim();
        }

        DetailsNote.Text = note;
        DetailsNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowLut(effect);
        ShowImageOptions(clip, effect);
        ShowChoices(effect);
        ShowColorTools(clip, effect);
        ShowRowVisibility(effect);
    }

    /// <summary>Las máscaras tienen su pestaña: aquí no se listan.</summary>
    private static bool Shows(EffectInstance effect) => effect.Type is not (BuiltInEffects.Mask or BuiltInEffects.Track);

    private void BuildRows(string type, ParameterSpec.Context context)
    {
        FinishChanges();
        ParameterRows.Children.Clear();
        _rows.Clear();
        foreach (ParameterSpec spec in ParameterSpec.ForEffect(type, context).Where(s => !HidesRow(type, s.Name)))
        {
            var row = new ParameterRow();
            row.Configure(spec);
            row.Changing += OnRowChanging;
            row.ChangeEnded += OnRowChangeEnded;
            row.Committed += OnRowCommitted;
            row.KeyframeToggled += OnRowKeyframeToggled;
            row.NavigateRequested += OnRowNavigate;
            row.InterpolationChosen += OnRowInterpolation;
            row.ResetRequested += OnRowReset;
            row.KeyframesClearRequested += OnRowClearKeyframes;
            _rows.Add(row);
            ParameterRows.Children.Add(row);
        }

        if (type == BuiltInEffects.Compressor)
        {
            // El compresor tiene su propia pestaña, con curva, medidores, escucha y sidechain.
            var open = new Button { Content = "Abrir en Dinámica", Margin = new Thickness(12, 4, 12, 4) };
            open.Click += (_, _) =>
            {
                if (_services is not null && _clipId is not null)
                {
                    _services.Session.ShowDynamics(new Services.Audio.DynamicsOwner(Services.Audio.DynamicsOwnerKind.Clip, _clipId));
                }
            };
            ParameterRows.Children.Add(open);
        }

        _rowsType = type;
        _rowsContext = context;
        BuildChoices(type);
    }

    private void ShowLut(EffectInstance effect)
    {
        if (effect.Type != BuiltInEffects.Color || _services is null)
        {
            LutRow.Visibility = Visibility.Collapsed;
            _lutShown = null;
            return;
        }

        LutRow.Visibility = Visibility.Visible;
        PackageScan installed = _services.Packages.Installed;
        string current = effect.Options.GetValueOrDefault(BuiltInEffects.ColorOptions.Lut, "");
        if (_lutShown is { } shown && ReferenceEquals(shown.Scan, installed) && shown.Current == current)
        {
            return;
        }

        _lutShown = (installed, current);
        _updating = true;
        LutBox.Items.Clear();
        _lutValues.Clear();
        Add("Sin LUT", "");
        foreach (PackageItemEntry entry in installed.Items(ItemKind.Lut))
        {
            Add($"{entry.Item.Name} · {entry.Package.Manifest.Name}", entry.Reference.ToString());
        }

        if (current.Length > 0 && !_lutValues.Contains(current))
        {
            string name = LutReference.DisplayName(current, installed);
            Add(LutReference.IsFile(current) ? name + " (archivo)" : name + " (falta el paquete)", current);
        }

        Add(LoadCubeLabel, null);
        LutBox.SelectedIndex = _lutValues.IndexOf(current);
        _updating = false;

        void Add(string label, string? value)
        {
            LutBox.Items.Add(label);
            _lutValues.Add(value);
        }
    }

    private IEnumerable<(string Name, double DefaultValue)> Defaults() => _rows.Select(r => (r.Spec.Name, r.Spec.DefaultValue));

    private void FinishChanges()
    {
        foreach (ParameterRow row in _rows)
        {
            row.FinishChange();
        }
    }

    private void Run(Func<string, string, ICommand> command)
    {
        if (_services is not null && _clipId is not null)
        {
            _services.Session.Execute(command(_services.Session.SequenceId, _clipId));
        }
    }

    private int IndexOf(string effectId) => CurrentClip()?.Effects.FindIndex(e => e.Id == effectId) ?? -1;

    private void OnAddSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || AddBox.SelectedIndex < 0 || _services is null || _clipId is null)
        {
            return;
        }

        string type = _addable[AddBox.SelectedIndex].Type;
        _updating = true;
        AddBox.SelectedIndex = -1;
        _updating = false;
        var command = Edits.AddEffect(_services.Session.SequenceId, _clipId, type);
        if (_services.Session.Execute(command))
        {
            _effectId = command.CreatedIds[0];
            Refresh();
        }
    }

    private void OnLutSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || LutBox.SelectedIndex < 0 || _effectId is not { } effectId || _services is null)
        {
            return;
        }

        string? value = _lutValues[LutBox.SelectedIndex];
        if (value is null)
        {
            _lutShown = null;
            string? path = PickCube();
            if (path is null)
            {
                Refresh();
                return;
            }

            value = LutReference.ForFile(path, _services.Session.FilePath);
        }

        if (!_services.Session.Execute(EffectCommands.SetLut(_services.Session.SequenceId, _clipId!, effectId, value)))
        {
            _lutShown = null;
            Refresh();
        }
    }

    private string? PickCube()
    {
        nint owner = XamlRoot?.ContentIslandEnvironment is { } environment ? Win32Interop.GetWindowFromWindowId(environment.AppWindowId) : 0;
        string? folder = _services?.Session.FilePath is { } project ? Path.GetDirectoryName(project) : null;
        try
        {
            IReadOnlyList<string> picked = FileDialogs.Open(owner, "Cargar LUT", multiple: false, folder, ("Tablas de color", "*.cube"), FileDialogs.AllFilter);
            return picked.Count > 0 ? picked[0] : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _services?.Session.Notify(NotificationSeverity.Error, "No se pudo abrir el selector de archivos", ex.Message);
            return null;
        }
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || List.SelectedItem is not FilterItem item || item.EffectId == _effectId)
        {
            return;
        }

        FinishChanges();
        _effectId = item.EffectId;
        if (CurrentClip() is { } clip)
        {
            ShowDetails(clip);
        }
    }

    private void OnEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string effectId } toggle)
        {
            bool enabled = toggle.IsChecked != true;
            Run((sequence, clip) => Edits.SetEffectEnabled(sequence, clip, effectId, enabled));
        }
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => Move(sender, -1);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => Move(sender, 1);

    private void Move(object sender, int delta)
    {
        if (sender is FrameworkElement { Tag: string effectId } && IndexOf(effectId) is int index and >= 0)
        {
            Run((sequence, clip) => Edits.MoveEffect(sequence, clip, effectId, index + delta));
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string effectId })
        {
            Run((sequence, clip) => Edits.RemoveEffect(sequence, clip, effectId));
        }
    }

    // La lista ya movió la fila; se pasa al modelo con un comando y la lista se rehace desde ahí.
    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.Items.Count != 1 || args.Items[0] is not FilterItem item)
        {
            return;
        }

        int target = Items.IndexOf(item);
        if (target >= 0 && target != IndexOf(item.EffectId))
        {
            Run((sequence, clip) => Edits.MoveEffect(sequence, clip, item.EffectId, target));
        }
        else
        {
            Refresh();
        }
    }

    private (Clip Clip, string Type, string EffectId)? Editing() =>
        CurrentClip() is { } clip && _effectId is { } id && clip.Effects.Find(e => e.Id == id) is { } effect
            ? (clip, effect.Type, id)
            : null;

    private void OnRowChanging(object? sender, double value)
    {
        if (sender is not ParameterRow row || _editor is not { } editor || Editing() is not { } target)
        {
            return;
        }

        if (!ReferenceEquals(_dragRow, row) || !editor.IsDragging)
        {
            _dragRow = row;
            editor.BeginChange(target.Clip, target.Type, row.Spec.Name, row.Spec.Description, target.EffectId);
        }

        editor.Change(value);
    }

    private void OnRowChangeEnded(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _dragRow))
        {
            return;
        }

        _dragRow = null;
        _editor?.EndChange();
        Refresh();
    }

    private void OnRowCommitted(object? sender, double value)
    {
        if (sender is ParameterRow row && _editor is { } editor && Editing() is { } target)
        {
            editor.SetValue(target.Clip, target.Type, row.Spec.Name, value, row.Spec.Description, target.EffectId);
        }
    }

    private void OnRowKeyframeToggled(object? sender, EventArgs e)
    {
        if (sender is ParameterRow row && _editor is { } editor && Editing() is { } target)
        {
            editor.ToggleKeyframe(target.Clip, target.Type, row.Spec.Name, row.Spec.DefaultValue, target.EffectId);
        }
    }

    private void OnRowNavigate(object? sender, int direction)
    {
        if (sender is ParameterRow row && _editor is { } editor && Editing() is { } target)
        {
            editor.GoToKeyframe(target.Clip, target.Type, row.Spec.Name, direction, target.EffectId);
        }
    }

    private void OnRowInterpolation(object? sender, Interpolation interpolation)
    {
        if (sender is ParameterRow row && _editor is { } editor && Editing() is { } target)
        {
            editor.SetInterpolation(target.Clip, target.Type, row.Spec.Name, interpolation, target.EffectId);
        }
    }

    private async void OnRowReset(object? sender, EventArgs e)
    {
        if (sender is ParameterRow row && _editor is { } editor && Editing() is { } target)
        {
            _dragRow = null;
            await editor.ResetAsync(target.Clip, target.Type, [(row.Spec.Name, row.Spec.DefaultValue)], row.Spec.ResetDescription,
                KeyframeDialog.ForReset(XamlRoot, row.Spec.Label), target.EffectId);
        }
    }

    private async void OnRowClearKeyframes(object? sender, EventArgs e)
    {
        if (sender is ParameterRow row && _editor is { } editor && Editing() is { } target)
        {
            _dragRow = null;
            string label = row.Spec.Label;
            await editor.ClearKeyframesAsync(target.Clip, target.Type, row.Spec.Name,
                "Quitar keyframes de " + char.ToLowerInvariant(label[0]) + label[1..], KeyframeDialog.ForClear(XamlRoot, label), target.EffectId);
        }
    }

    private async void OnResetAllClick(object sender, RoutedEventArgs e)
    {
        if (_editor is not { } editor || Editing() is not { } target)
        {
            return;
        }

        FinishChanges();
        _dragRow = null;
        string name = EffectUi.Name(target.Type);
        await editor.ResetAsync(target.Clip, target.Type, [.. Defaults()], "Restablecer " + name,
            KeyframeDialog.ForResetAll(XamlRoot, "«" + name + "»"), target.EffectId);
    }
}
