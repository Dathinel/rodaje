using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.App.Shell;
using Rodaje.App.Views.Inspector.Properties;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Paquetes;

namespace Rodaje.App.Views.Inspector.Transition;

/// <summary>
/// Inspector de la transición seleccionada (su id en la selección de la sesión). Los parámetros de las transiciones no
/// tienen keyframes: un arrastre funde sus pasos en uno del historial.
/// </summary>
internal sealed partial class TransitionPanel : UserControl, IEditorView
{
    private const string DurationName = "duration";

    private readonly List<ParameterRow> _rows = [];
    private readonly List<(string Type, string Preset)> _types = [];
    private EditorServices? _services;
    private TransitionEditor? _editor;
    private string? _transitionId;
    private string? _rowsType;
    private (PackageScan Scan, string Type, string Preset)? _typesShown;
    private (double Minimum, double Maximum) _durationRange;
    private bool _updating;

    public TransitionPanel()
    {
        InitializeComponent();
        foreach ((_, string name) in EffectUi.PushDirections)
        {
            DirectionBox.Items.Add(name);
        }

        Wire(DurationRow);
    }

    /// <summary>Cambió si hay una transición seleccionada (el anfitrión decide qué se ve en Propiedades).</summary>
    public event EventHandler? ActiveChanged;

    public bool IsActive => _transitionId is not null;

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _editor = new TransitionEditor(services.Session);
        services.Session.SelectionChanged += (_, _) => Refresh();
        services.Session.ProjectEdited += (_, _) => Refresh();
        services.Session.ProjectOpened += (_, _) => Refresh();
        services.Packages.Changed += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        if (_editor is null || _services is null)
        {
            return;
        }

        bool wasActive = IsActive;
        TransitionTarget? target = _editor.Selected();
        if (target?.Transition.Id != _transitionId)
        {
            FinishChanges();
        }

        _transitionId = target?.Transition.Id;
        if (target is not null)
        {
            Show(target);
        }

        if (wasActive != IsActive)
        {
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Show(TransitionTarget target)
    {
        TransitionInstance transition = target.Transition;
        EditorSession session = _editor!.Session;
        CutText.Text = $"Entre «{ClipName(session, target.From)}» y «{ClipName(session, target.To)}».";
        ToolTipService.SetToolTip(CutText, CutText.Text);
        string preset = transition.Type == BuiltInTransitions.Preset
            ? TransitionEditor.ReadOption(transition, BuiltInTransitions.PresetOptions.Preset)
            : "";
        ShowTypes(transition.Type, preset);

        bool missing = preset.Length > 0 && !PresetInstalled(preset);
        TypeNote.Text = missing ? "Falta el paquete de esta transición: se ve como fundido cruzado." : EffectUi.Description(transition.Type);
        TypeNote.Visibility = TypeNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        (double minimum, double maximum) = _editor.DurationRange(target);
        if (_durationRange != (minimum, maximum))
        {
            _durationRange = (minimum, maximum);
            DurationRow.Configure(new ParameterSpec("transition", DurationName, "Duración", "Cambiar duración de la transición",
                minimum, maximum, 0.05, 1, EffectRegistry.DefaultTransitionDuration.ToSeconds(), 2, "s", false)
            {
                ModelMinimum = minimum,
            });
        }

        DurationRow.Show(new ParameterState(transition.Duration.ToSeconds(), 0, null, null, null), true);

        if (_rowsType != transition.Type)
        {
            BuildRows(transition.Type);
        }

        foreach (ParameterRow row in _rows)
        {
            row.Show(new ParameterState(TransitionEditor.Read(transition, row.Spec.Name), 0, null, null, null), true);
        }

        bool push = transition.Type == BuiltInTransitions.Push;
        DirectionRow.Visibility = push ? Visibility.Visible : Visibility.Collapsed;
        if (push)
        {
            _updating = true;
            DirectionBox.SelectedIndex = EffectRegistry.PushDirections.IndexOf(
                TransitionEditor.ReadOption(transition, BuiltInTransitions.PushOptions.Direction));
            _updating = false;
        }
    }

    private bool PresetInstalled(string reference) =>
        PackageReference.TryParse(reference, out PackageReference? parsed)
        && _services!.Packages.Installed.Find(parsed.PackageId)?.Manifest.Items.Any(i => i.Id == parsed.ItemId && i.Kind == ItemKind.Transition) == true;

    private void ShowTypes(string type, string preset)
    {
        PackageScan installed = _services!.Packages.Installed;
        if (_typesShown is { } shown && ReferenceEquals(shown.Scan, installed) && shown.Type == type && shown.Preset == preset)
        {
            return;
        }

        _typesShown = (installed, type, preset);
        _updating = true;
        TypeBox.Items.Clear();
        _types.Clear();
        foreach (EffectDefinition definition in TransitionEditor.BuiltInTypes)
        {
            TypeBox.Items.Add(EffectUi.Name(definition.Type));
            _types.Add((definition.Type, ""));
        }

        foreach (PackageItemEntry entry in installed.Items(ItemKind.Transition))
        {
            string source = entry.Package.Origin == PackageOrigin.Own ? "Mía" : entry.Package.Manifest.Name;
            TypeBox.Items.Add($"{entry.Item.Name} · {source}");
            _types.Add((BuiltInTransitions.Preset, entry.Reference.ToString()));
        }

        int index = _types.IndexOf((type, preset));
        if (index < 0 && preset.Length > 0)
        {
            TypeBox.Items.Add(preset + " (falta el paquete)");
            _types.Add((type, preset));
            index = _types.Count - 1;
        }

        TypeBox.SelectedIndex = index;
        _updating = false;
    }

    private void BuildRows(string type)
    {
        FinishChanges();
        ParameterRows.Children.Clear();
        _rows.Clear();
        foreach (ParameterSpec spec in ParameterSpec.ForEffect(type, default))
        {
            var row = new ParameterRow();
            row.Configure(spec);
            Wire(row);
            _rows.Add(row);
            ParameterRows.Children.Add(row);
        }

        _rowsType = type;
    }

    private void Wire(ParameterRow row)
    {
        row.Changing += (_, value) => Apply(row, value);
        row.Committed += (_, value) =>
        {
            Apply(row, value);
            _editor?.Session.BreakMerge();
        };
        row.ChangeEnded += (_, _) =>
        {
            _editor?.Session.BreakMerge();
            Refresh();
        };
        row.ResetRequested += (_, _) =>
        {
            Apply(row, row.Spec.DefaultValue);
            _editor?.Session.BreakMerge();
        };
    }

    private void Apply(ParameterRow row, double value)
    {
        if (_editor is not { } editor || _transitionId is not { } id)
        {
            return;
        }

        if (row.Spec.Name == DurationName && ReferenceEquals(row, DurationRow))
        {
            editor.SetDuration(id, value);
        }
        else
        {
            editor.SetParameter(id, row.Spec.Name, value);
        }
    }

    private void FinishChanges()
    {
        DurationRow.FinishChange();
        foreach (ParameterRow row in _rows)
        {
            row.FinishChange();
        }
    }

    private static string ClipName(EditorSession session, Clip clip) => ClipLabels.Name(session.Project, clip, "clip");

    private void OnTypeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || TypeBox.SelectedIndex < 0 || _editor is not { } editor || _transitionId is not { } id)
        {
            return;
        }

        (string type, string preset) = _types[TypeBox.SelectedIndex];
        bool done = preset.Length > 0 ? editor.SetPreset(id, preset) : editor.SetType(id, type);
        if (!done)
        {
            _typesShown = null;
            Refresh();
        }
    }

    private void OnDirectionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && DirectionBox.SelectedIndex >= 0 && _editor is { } editor && _transitionId is { } id)
        {
            editor.SetOption(id, BuiltInTransitions.PushOptions.Direction, EffectRegistry.PushDirections[DirectionBox.SelectedIndex]);
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (_editor is { } editor && _transitionId is { } id)
        {
            FinishChanges();
            editor.Remove(id);
        }
    }
}
