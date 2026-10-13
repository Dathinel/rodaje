using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.App.Theme;
using VirtualKey = Windows.System.VirtualKey;

namespace Rodaje.App.Views.Shortcuts;

/// <summary>
/// Página de atajos de teclado y ratón. La vista esencial muestra el teclado y el ratón con los atajos básicos y su
/// lista; la avanzada permite ver, buscar y cambiar las teclas de cada acción y lo que hace cada botón del ratón por
/// zona, con estilos, dibujos que se editan con un clic, hoja rápida y exportar o importar. La vista elegida se
/// recuerda en los ajustes. Mientras captura una combinación nueva suspende el <see cref="ShortcutRouter"/> y se queda
/// con todas las teclas hasta que se sueltan, para que ninguna lance una acción ni active el botón con el foco.
/// </summary>
internal sealed partial class ShortcutsView : UserControl, IEditorView, IShortcutRowHost, IBindingEditorHost
{
    private const double SingleColumnWidth = 760;
    private const double SideBySideWidth = 1100;

    private static readonly (string Name, string Spec) JsonFilter = ("Mapa de atajos", "*.json");

    private readonly Dictionary<string, ShortcutRowView> _rows = new(StringComparer.Ordinal);
    private readonly List<GroupSection> _groups = [];
    private readonly HashSet<VirtualKey> _swallowKeyUp = [];
    private EditorServices? _services;
    private ShortcutRegistry? _registry;
    private ShortcutCustomizer? _customizer;
    private DispatcherQueueTimer? _releaseTimer;
    private KeyCapture? _capture;
    private ShortcutRowView? _captureView;
    private BindingEditor? _captureEditor;
    private string? _captureAction;
    private KeyChord? _captureReplacing;
    private IDisposable? _suspension;
    private IDisposable? _mouseSuspension;
    private int? _keyFilter;
    private bool _syncingPreset;
    private bool _singleColumn;
    private bool _advanced;
    private int _essentialColumns = 2;

    public ShortcutsView()
    {
        InitializeComponent();
    }

    private static IKeyboardLayout? Layout => KeyNames.DisplayLayout;

    public ShortcutRegistry Registry => _registry ?? throw new InvalidOperationException("La página de atajos no está unida.");

    public ShortcutCustomizer Customizer => _customizer ?? throw new InvalidOperationException("La página de atajos no está unida.");

    /// <summary>El editor de la tecla o el botón elegido (para la autoprueba).</summary>
    internal BindingEditor BindingPanel => Editor;

    internal KeyboardView KeyboardDrawing => Keyboard;

    internal MouseView MouseDrawingView => MouseDrawing;

    internal ToggleButton DrawingsToggle => KeyboardToggle;

    internal RadioButton AdvancedModeTab => AdvancedTab;

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _registry = services.ShortcutRegistry;
        _customizer = new ShortcutCustomizer(_registry);
        _registry.Changed += (_, _) => OnRegistryChanged();
        Keyboard.KeySelected += (_, vk) => OpenKey(vk);
        Keyboard.LayerChanged += (_, layer) => MouseDrawing.SetLayer(layer);
        MouseDrawing.LayerChanged += (_, layer) => Keyboard.SetLayer(layer);
        MouseDrawing.PartSelected += (_, input) => OpenMouse(input);
        MouseDrawing.ContextChanged += (_, context) =>
        {
            if (Editor.MousePart is { } part)
            {
                Editor.ShowMouse(context, part.Input);
            }
        };
        EssentialKeyboard.LayerChanged += (_, layer) => EssentialMouse.SetLayer(layer);
        EssentialMouse.LayerChanged += (_, layer) => EssentialKeyboard.SetLayer(layer);
        Editor.Attach(this);
        Sheet.Saved += OnSheetSaved;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // Si una tecla capturada no llega a soltarse aquí (la ventana perdió el foco), los atajos vuelven igual.
        _releaseTimer = DispatcherQueue.CreateTimer();
        _releaseTimer.Interval = TimeSpan.FromSeconds(1);
        _releaseTimer.IsRepeating = false;
        _releaseTimer.Tick += (_, _) => ReleaseShortcuts();

        _advanced = services.Settings.Current.ShortcutsAdvanced;
        (_advanced ? AdvancedTab : EssentialTab).IsChecked = true;
        ApplyMode();
        RebuildRows();
        SyncPreset();
    }

    public void BeginCapture(ShortcutRowView view, KeyChord? replacing)
    {
        ArgumentNullException.ThrowIfNull(view);
        EndCapture();
        Editor.EndCapture();
        StartCapture();
        _captureView = view;
        _captureAction = view.Row.ActionId;
        _captureReplacing = replacing;
        string prompt = replacing is { } old ? $"En lugar de «{old}», pulsa la nueva combinación… Esc cancela" : KeyCapture.Prompt;
        view.Row.BeginCapture(prompt);
    }

    public void BeginKeyCapture(BindingEditor editor, string actionId, KeyChord replacing)
    {
        ArgumentNullException.ThrowIfNull(editor);
        EndCapture();
        StartCapture();
        _captureEditor = editor;
        _captureAction = actionId;
        _captureReplacing = replacing;
    }

    public void CancelKeyCapture() => EndCapture();

    public void HoldShortcuts(bool hold)
    {
        if (hold)
        {
            _mouseSuspension ??= _services?.Shortcuts.Suspend();
        }
        else
        {
            _mouseSuspension?.Dispose();
            _mouseSuspension = null;
        }
    }

    public void CloseEditor()
    {
        Editor.EndCapture();
        Editor.Visibility = Visibility.Collapsed;
        MouseDrawing.Select(null);
        Keyboard.Select(_keyFilter);
    }

    public async Task AssignKeyAsync(string actionId, KeyChord chord, KeyChord? replacing, bool toSlot)
    {
        ConflictChoice choice = ConflictChoice.Swap;
        if (ReservedKeys.Reason(chord) is null && Customizer.OwnerOf(chord, actionId) is { } owner)
        {
            bool canSwap = toSlot ? Registry.GetKeys(actionId).Count > 0 : replacing is not null;
            if (await AskConflictAsync(KeyNames.Format(chord), Customizer.LabelOf(owner), Customizer.LabelOf(actionId), canSwap) is not { } answer)
            {
                return;
            }

            choice = answer;
        }

        ShowChange(toSlot ? Customizer.AssignToKey(chord, actionId, choice) : Customizer.Assign(actionId, chord, replacing, choice));
    }

    public async Task AssignMouseAsync(MouseContext context, MouseChord chord, string actionId, MouseChord? from)
    {
        ConflictChoice choice = ConflictChoice.Swap;
        if (Registry.Mouse.Reason(context, chord, actionId) is null && Registry.Mouse.Get(context, chord) is { } owner && owner != actionId)
        {
            bool canSwap = (from ?? Registry.Mouse.FindChord(context, actionId, except: chord)) is { } back && Registry.Mouse.Reason(context, back, owner) is null;
            if (await AskConflictAsync($"{chord} ({MouseNames.Display(context).ToLowerInvariant()})", Customizer.LabelOf(owner), Customizer.LabelOf(actionId), canSwap) is not { } answer)
            {
                return;
            }

            choice = answer;
        }

        ShowChange(Customizer.AssignMouse(context, chord, actionId, from, choice));
    }

    /// <summary>
    /// La combinación ya era de otra acción: intercambiarlas (si se puede) o quitársela. <c>null</c> si se canceló. Si el
    /// diálogo no se puede abrir (otro abierto), se intercambia, que se puede deshacer.
    /// </summary>
    private async Task<ConflictChoice?> AskConflictAsync(string combination, string theirs, string mine, bool canSwap)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"«{combination}» ya es de «{theirs}»",
            Content = new TextBlock
            {
                Text = canSwap
                    ? $"Intercambiar: «{mine}» se queda con «{combination}» y «{theirs}» pasa a la combinación que deja «{mine}».\nQuitársela: «{theirs}» se queda sin «{combination}»."
                    : $"«{theirs}» se quedará sin «{combination}».",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = canSwap ? "Intercambiar" : "Quitársela",
            SecondaryButtonText = canSwap ? "Quitársela" : "",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        ContentDialogResult? result = await DialogGate.ShowAsync(dialog);
        return result switch
        {
            null => ConflictChoice.Swap,
            ContentDialogResult.Primary => canSwap ? ConflictChoice.Swap : ConflictChoice.Take,
            ContentDialogResult.Secondary => ConflictChoice.Take,
            _ => null,
        };
    }

    private void StartCapture()
    {
        _capture = new KeyCapture();
        _releaseTimer?.Stop();
        _suspension ??= _services?.Shortcuts.Suspend();
    }

    /// <summary>Abre una tecla del dibujo para cambiarla y filtra la lista por ella.</summary>
    internal void OpenKey(int virtualKey)
    {
        SetKeyFilter(virtualKey);
        MouseDrawing.Select(null);
        Editor.ShowKey(virtualKey);
        Editor.Visibility = Visibility.Visible;
        RevealEditor();
    }

    /// <summary>Abre una parte del ratón dibujado, en la zona elegida, para cambiarla.</summary>
    internal void OpenMouse(MouseInput input)
    {
        if (_keyFilter is not null)
        {
            _keyFilter = null;
            KeyFilterChip.Visibility = Visibility.Collapsed;
            ApplyFilter();
        }

        MouseDrawing.Select(input);
        Keyboard.Select(null);
        Editor.ShowMouse(MouseDrawing.Context, input);
        Editor.Visibility = Visibility.Visible;
        RevealEditor();
    }

    /// <summary>Lleva el editor a la vista, con el dibujo encima si cabe.</summary>
    private void RevealEditor()
    {
        Editor.UpdateLayout();
        Editor.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 1 });
    }

    private void OnDrawingsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var grid = (Grid)sender;
        bool side = e.NewSize.Width >= SideBySideWidth;
        var mouse = (FrameworkElement)grid.Children[1];
        Grid.SetColumn(mouse, side ? 1 : 0);
        Grid.SetRow(mouse, side ? 0 : 1);
        grid.ColumnDefinitions[1].Width = side ? GridLength.Auto : new GridLength(0);
    }

    private void SetCaptureText(string text)
    {
        if (_captureView is not null)
        {
            _captureView.Row.CaptureText = text;
        }
        else
        {
            _captureEditor?.SetCaptureText(text);
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (FileDialogs.Save(Owner(), "Exportar atajos", "atajos-rodaje.json", "json", null, JsonFilter) is not { } path)
            {
                return;
            }

            ShortcutStore.Export(Registry, path);
            ShowNotice($"Atajos exportados a {System.IO.Path.GetFileName(path)}", new NotificationAction("Abrir", () => Open(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            ShowNotice($"No se pudieron exportar los atajos: {ex.Message}", severity: NotificationSeverity.Warning);
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> picked;
        try
        {
            picked = FileDialogs.Open(Owner(), "Importar atajos", multiple: false, null, JsonFilter, FileDialogs.AllFilter);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            ShowNotice($"No se pudo abrir el diálogo: {ex.Message}", severity: NotificationSeverity.Warning);
            return;
        }

        if (picked.Count > 0)
        {
            EndCapture();
            Editor.EndCapture();
            ShowChange(Customizer.Import(picked[0]));
        }
    }

    private nint Owner() =>
        XamlRoot?.ContentIslandEnvironment is { } environment ? Microsoft.UI.Win32Interop.GetWindowFromWindowId(environment.AppWindowId) : 0;

    public void CancelCapture() => EndCapture();

    public void RemoveKey(ShortcutRow row, KeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(row);
        ShowChange(_customizer!.RemoveKey(row.ActionId, chord));
    }

    public void Reset(ShortcutRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ShowChange(_customizer!.Reset(row.ActionId));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // La distribución del teclado pudo cambiar mientras la página estaba cerrada.
        RefreshAll();
        (_advanced ? SearchBox : (Control)EssentialTab).Focus(FocusState.Programmatic);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EndCapture();
        _swallowKeyUp.Clear();
        ReleaseShortcuts();
    }

    private void OnRegistryChanged()
    {
        if (_registry is null)
        {
            return;
        }

        if (_rows.Count != _registry.Actions.Count)
        {
            RebuildRows();
        }
        else
        {
            RefreshAll();
        }

        SyncPreset();
    }

    private void RefreshAll()
    {
        RefreshRows();
        ApplyFilter();
        RefreshKeyboard();
        RefreshSheet();
        RefreshEssential();
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        bool advanced = ReferenceEquals(sender, AdvancedTab);
        if (advanced == _advanced)
        {
            return;
        }

        _advanced = advanced;
        if (_services is not null)
        {
            _services.Settings.Current.ShortcutsAdvanced = advanced;
        }

        if (!advanced)
        {
            EndCapture();
        }

        ApplyMode();
        if (_registry is not null)
        {
            RefreshAll();
        }
    }

    private void ApplyMode()
    {
        Visibility advanced = _advanced ? Visibility.Visible : Visibility.Collapsed;
        Visibility essential = _advanced ? Visibility.Collapsed : Visibility.Visible;
        AdvancedPanel.Visibility = advanced;
        AdvancedTools.Visibility = advanced;
        SearchRow.Visibility = advanced;
        EssentialPanel.Visibility = essential;
    }

    private void RefreshEssential()
    {
        if (_advanced)
        {
            return;
        }

        IReadOnlyList<CheatSheetEntry> entries = EssentialShortcuts.Select(Registry);
        EssentialKeyboard.Show(
            KeyboardGeometry.Map(Registry, Layout, include: EssentialShortcuts.Contains),
            _groups.Select(g => g.Name).Where(g => entries.Any(e => e.Group == g)));
        EssentialMouse.Show(Registry);
        BuildEssentialList(entries);
    }

    private void BuildEssentialList(IReadOnlyList<CheatSheetEntry> entries)
    {
        EssentialList.Children.Clear();
        EssentialList.ColumnDefinitions.Clear();
        EssentialList.RowDefinitions.Clear();
        int columns = _essentialColumns;
        int rows = (entries.Count + columns - 1) / columns;
        for (int c = 0; c < columns; c++)
        {
            EssentialList.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int r = 0; r < rows; r++)
        {
            EssentialList.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int i = 0; i < entries.Count; i++)
        {
            CheatSheetEntry entry = entries[i];
            var cell = new Grid
            {
                ColumnSpacing = 8,
                MinHeight = 32,
                BorderBrush = GroupBrushes.Resource("StrokeBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cell.Children.Add(new TextBlock
            {
                Text = entry.Label,
                Style = (Style)Application.Current.Resources["BodyTextStyle"],
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4),
            });
            var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            foreach (string key in entry.Keys)
            {
                keys.Children.Add(new Border
                {
                    Background = GroupBrushes.Resource("ControlBrush"),
                    BorderBrush = GroupBrushes.For(entry.Group),
                    BorderThickness = new Thickness(1, 1, 1, 2),
                    CornerRadius = ThemeResources.Corner("ControlCornerRadius"),
                    Padding = new Thickness(6, 1, 6, 1),
                    Child = new TextBlock { Text = key, FontSize = ThemeResources.Size("CaptionFontSize"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = GroupBrushes.Resource("TextBrush") },
                });
            }

            Grid.SetColumn(keys, 1);
            cell.Children.Add(keys);
            AutomationProperties.SetName(cell, $"{entry.Label}: {string.Join(" o ", entry.Keys)}");
            Grid.SetRow(cell, i % rows);
            Grid.SetColumn(cell, i / rows);
            EssentialList.Children.Add(cell);
        }
    }

    private void OnEssentialListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        int columns = e.NewSize.Width >= 900 ? 3 : e.NewSize.Width >= 520 ? 2 : 1;
        if (columns != _essentialColumns && _registry is not null)
        {
            _essentialColumns = columns;
            BuildEssentialList(EssentialShortcuts.Select(Registry));
        }
    }

    private void RebuildRows()
    {
        EndCapture();
        _rows.Clear();
        _groups.Clear();
        LeftColumn.Children.Clear();
        RightColumn.Children.Clear();
        foreach (ShortcutAction action in Registry.Actions)
        {
            GroupSection section = _groups.Find(g => g.Name == action.Group) ?? AddGroup(action.Group);
            var view = new ShortcutRowView(new ShortcutRow(action), this);
            section.Rows.Add(view);
            section.Panel.Children.Add(view);
            _rows[action.Id] = view;
        }

        // Los grupos que traen las vistas van detrás de los conocidos, en el orden en que aparecieron.
        List<string> appearance = [.. _groups.Select(g => g.Name)];
        List<string> known = [.. ShortcutGroups.Order];
        int Rank(string group)
        {
            int index = known.IndexOf(group);
            return index >= 0 ? index : known.Count + appearance.IndexOf(group);
        }

        _groups.Sort((a, b) => Rank(a.Name).CompareTo(Rank(b.Name)));
        RefreshAll();
    }

    private GroupSection AddGroup(string name)
    {
        var dot = new Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, Fill = GroupBrushes.For(name), VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = name, Style = (Style)Application.Current.Resources["PanelTitleTextStyle"] };
        var count = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextStyle"] };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(dot);
        header.Children.Add(title);
        header.Children.Add(count);
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
        var panel = new StackPanel();
        panel.Children.Add(header);
        var section = new GroupSection(name, panel, count);
        _groups.Add(section);
        return section;
    }

    private void RefreshRows()
    {
        var conflicts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (ShortcutConflict conflict in Registry.FindConflicts())
        {
            foreach (string id in conflict.ActionIds)
            {
                if (!conflicts.TryGetValue(id, out List<string>? others))
                {
                    conflicts[id] = others = [];
                }

                foreach (string other in conflict.ActionIds)
                {
                    string label = Registry.Find(other)?.Label ?? other;
                    if (other != id && !others.Contains(label))
                    {
                        others.Add(label);
                    }
                }
            }
        }

        foreach ((string id, ShortcutRowView view) in _rows)
        {
            view.Row.Update(Registry.GetKeys(id), Registry.IsCustomized(id), conflicts.GetValueOrDefault(id) ?? [], KeyNames.Format);
        }
    }

    private void ApplyFilter()
    {
        var matches = new HashSet<string>(ShortcutSearch.Filter(Registry, SearchBox.Text, Layout).Select(a => a.Id), StringComparer.Ordinal);
        int shown = 0;
        foreach (GroupSection section in _groups)
        {
            int visible = 0;
            foreach (ShortcutRowView view in section.Rows)
            {
                bool show = matches.Contains(view.Row.ActionId) && (_keyFilter is not { } vk || view.Row.Keys.Any(k => k.Key == vk));
                view.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                visible += show ? 1 : 0;
            }

            section.VisibleRows = visible;
            section.Count.Text = visible == section.Rows.Count ? $"{visible}" : $"{visible} de {section.Rows.Count}";
            shown += visible;
        }

        bool filtering = SearchBox.Text.Trim().Length > 0 || _keyFilter is not null;
        CountText.Text = filtering ? $"{shown} de {_rows.Count} acciones" : $"{_rows.Count} acciones";
        EmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _keyFilter is { } key && SearchBox.Text.Trim().Length == 0
            ? $"La tecla «{KeyNames.KeyName(key)}» no lanza ninguna acción en este estilo."
            : $"Ninguna acción coincide con «{SearchBox.Text.Trim()}». Prueba con otra palabra o con una tecla, como «Ctrl+Z».";
        LayoutColumns(force: false);
    }

    /// <summary>
    /// Reparte los grupos visibles en dos columnas sin cambiar su orden: la primera se llena hasta la mitad de las filas.
    /// Si el reparto no cambia, no se toca nada (así el foco no se pierde al cambiar una tecla).
    /// </summary>
    private void LayoutColumns(bool force)
    {
        List<GroupSection> visible = [.. _groups.Where(g => g.VisibleRows > 0)];
        foreach (GroupSection section in _groups)
        {
            section.Panel.Visibility = section.VisibleRows > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        var left = new List<StackPanel>();
        var right = new List<StackPanel>();
        double total = visible.Sum(Weight);
        double filled = 0;
        foreach (GroupSection section in visible)
        {
            if (_singleColumn || filled < total / 2)
            {
                left.Add(section.Panel);
                filled += Weight(section);
            }
            else
            {
                right.Add(section.Panel);
            }
        }

        if (!force && LeftColumn.Children.SequenceEqual(left) && RightColumn.Children.SequenceEqual(right))
        {
            return;
        }

        LeftColumn.Children.Clear();
        RightColumn.Children.Clear();
        left.ForEach(LeftColumn.Children.Add);
        right.ForEach(RightColumn.Children.Add);
        GroupsGrid.ColumnDefinitions[1].Width = _singleColumn ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        GroupsGrid.ColumnSpacing = _singleColumn ? 0 : 32;

        static double Weight(GroupSection section) => section.VisibleRows + 1.5;
    }

    private void OnGroupsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool single = e.NewSize.Width < SingleColumnWidth;
        if (single != _singleColumn)
        {
            _singleColumn = single;
            LayoutColumns(force: true);
        }
    }

    private void RefreshKeyboard()
    {
        if (KeyboardPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        Keyboard.Show(KeyboardGeometry.Map(Registry, Layout), _groups.Select(g => g.Name));
        Keyboard.Select(Editor.MousePart is null ? _keyFilter : null);
        MouseDrawing.Show(Registry);
        if (Editor.Visibility == Visibility.Visible && !Editor.IsCapturing)
        {
            Editor.Refresh();
        }
    }

    private void RefreshSheet()
    {
        if (CheatSheetPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        IReadOnlyList<CheatSheetEntry> entries = CheatSheet.Select(Registry);
        Sheet.Show(entries, "Atajos esenciales de Rodaje", $"Estilo {ShortcutPresetNames.Display(Registry.Preset)} · los {entries.Count} más útiles");
    }

    private void SyncPreset()
    {
        _syncingPreset = true;
        RadioButton tab = Registry.Preset switch
        {
            ShortcutPreset.Agile => AgileTab,
            ShortcutPreset.Classic => ClassicTab,
            _ => RodajeTab,
        };
        tab.IsChecked = true;
        _syncingPreset = false;
    }

    private void OnPresetChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingPreset || _registry is null)
        {
            return;
        }

        ShortcutPreset preset = ReferenceEquals(sender, AgileTab) ? ShortcutPreset.Agile
            : ReferenceEquals(sender, ClassicTab) ? ShortcutPreset.Classic
            : ShortcutPreset.Rodaje;
        if (preset == _registry.Preset)
        {
            return;
        }

        EndCapture();
        _registry.Preset = preset;
        if (_services is not null)
        {
            _services.Settings.Current.ShortcutPreset = preset;
        }

        ShowNotice($"Estilo «{ShortcutPresetNames.Display(preset)}». Lo que cambies aquí se guarda solo para este estilo.");
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_registry is not null)
        {
            ApplyFilter();
        }
    }

    private void SetKeyFilter(int virtualKey)
    {
        _keyFilter = virtualKey;
        string name = KeyNames.KeyName(virtualKey);
        KeyFilterText.Text = $"Tecla «{name}»";
        AutomationProperties.SetName(KeyFilterChip, $"Quitar el filtro por la tecla {name}");
        ToolTipService.SetToolTip(KeyFilterChip, "Ver todas las acciones otra vez");
        KeyFilterChip.Visibility = Visibility.Visible;
        Keyboard.Select(virtualKey);
        ApplyFilter();
    }

    private void OnKeyFilterClick(object sender, RoutedEventArgs e)
    {
        _keyFilter = null;
        KeyFilterChip.Visibility = Visibility.Collapsed;
        Keyboard.Select(null);
        if (Editor.Key is not null)
        {
            CloseEditor();
        }

        ApplyFilter();
        SearchBox.Focus(FocusState.Keyboard);
    }

    private void OnKeyboardToggleChanged(object sender, RoutedEventArgs e)
    {
        KeyboardPanel.Visibility = KeyboardToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshKeyboard();
    }

    private void OnCheatSheetToggleChanged(object sender, RoutedEventArgs e)
    {
        CheatSheetPanel.Visibility = CheatSheetToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshSheet();
    }

    private void OnResetAllClick(object sender, RoutedEventArgs e)
    {
        EndCapture();
        Editor.EndCapture();
        ShowChange(_customizer!.ResetAll());
    }

    private void OnSheetSaved(object? sender, CheatSheetSaved saved)
    {
        if (saved.Path is { } path)
        {
            ShowNotice($"Hoja guardada en Documentos: {System.IO.Path.GetFileName(path)}", new NotificationAction("Abrir", () => Open(path)));
        }
        else
        {
            ShowNotice(saved.Error ?? "No se pudo guardar la hoja.", severity: NotificationSeverity.Warning);
        }
    }

    private void Open(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ShowNotice($"No se pudo abrir {path}: {ex.Message}", severity: NotificationSeverity.Warning);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_capture is null)
        {
            // Ctrl, Mayús o Alt apretados de verdad muestran sus combinaciones en el teclado y el ratón dibujados.
            if (KeyChord.IsModifierKey((int)e.Key))
            {
                ShowLayer(KeyboardShortcutHost.CurrentModifiers() | KeyCapture.ModifierOf((int)e.Key));
            }

            return;
        }

        e.Handled = true;
        _swallowKeyUp.Add(e.Key);
        if (e.KeyStatus.WasKeyDown)
        {
            return;
        }

        CaptureResult result = _capture.KeyDown((int)e.Key, KeyboardShortcutHost.CurrentModifiers());
        switch (result.Status)
        {
            case CaptureStatus.Cancelled:
                EndCapture();
                break;
            case CaptureStatus.Accepted:
                KeyChord? replacing = _captureReplacing;
                string actionId = _captureAction!;
                EndCapture();
                UiTask.Run(AssignKeyAsync(actionId, result.Chord, replacing, toSlot: false));
                break;
            default:
                SetCaptureText(result.Message);
                break;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_swallowKeyUp.Remove(e.Key))
        {
            e.Handled = true;
        }

        if (_capture is not null)
        {
            SetCaptureText(_capture.KeyUp((int)e.Key, KeyboardShortcutHost.CurrentModifiers()).Message);
            return;
        }

        if (KeyChord.IsModifierKey((int)e.Key))
        {
            ShowLayer(KeyboardShortcutHost.CurrentModifiers() & ~KeyCapture.ModifierOf((int)e.Key));
        }

        if (_swallowKeyUp.Count == 0)
        {
            ReleaseShortcuts();
        }
    }

    private void ShowLayer(KeyModifiers layer)
    {
        Keyboard.SetLayer(layer);
        MouseDrawing.SetLayer(layer);
        EssentialKeyboard.SetLayer(layer);
        EssentialMouse.SetLayer(layer);
    }

    private void EndCapture()
    {
        if (_capture is null)
        {
            return;
        }

        ShortcutRowView? view = _captureView;
        BindingEditor? editor = _captureEditor;
        _capture = null;
        _captureView = null;
        _captureEditor = null;
        _captureAction = null;
        _captureReplacing = null;
        view?.Row.EndCapture();
        editor?.KeyCaptureEnded();
        if (_swallowKeyUp.Count == 0)
        {
            ReleaseShortcuts();
        }
        else
        {
            _releaseTimer?.Start();
        }
    }

    private void ReleaseShortcuts()
    {
        if (_capture is not null)
        {
            return;
        }

        _releaseTimer?.Stop();
        _suspension?.Dispose();
        _suspension = null;
    }

    public void ShowChange(ShortcutChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Editor.Visibility == Visibility.Visible && !Editor.IsCapturing)
        {
            // El historial se apunta después de avisar del cambio: «Deshacer» del editor se pone al día aquí.
            Editor.Refresh();
        }

        if (change.Changed)
        {
            string message = change.Warning is { } warning ? $"{change.Message}. Ojo: {warning}" : change.Message;
            ShowNotice(message, new NotificationAction("Deshacer", () =>
            {
                _customizer!.Undo(change);
                ShowNotice("Se deshizo el cambio.");
            }), change.Warning is null ? NotificationSeverity.Informational : NotificationSeverity.Warning);
        }
        else if (change.Message.Length > 0)
        {
            ShowNotice(change.Message);
        }
    }

    private void ShowNotice(string message, NotificationAction? action = null, NotificationSeverity severity = NotificationSeverity.Informational) =>
        _services?.Session.Notify(severity, "Atajos", message, action);

    private sealed class GroupSection(string name, StackPanel panel, TextBlock count)
    {
        public string Name { get; } = name;

        public StackPanel Panel { get; } = panel;

        public TextBlock Count { get; } = count;

        public List<ShortcutRowView> Rows { get; } = [];

        public int VisibleRows { get; set; }
    }
}
