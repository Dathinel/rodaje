using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Shell;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Inspector.Properties;

/// <summary>Propiedades del clip seleccionado: transformación animable con keyframes, modo de mezcla y audio.</summary>
internal sealed partial class PropertiesPanel : UserControl, IEditorView
{
    private const string DefaultPresetName = "Mi transición";

    private readonly ParameterRow[] _videoRows;
    private readonly ParameterRow[] _audioRows;
    private EditorServices? _services;
    private ClipPropertyEditor? _editor;
    private ParameterRow? _dragRow;
    private string? _videoClipId;
    private string? _audioClipId;
    private (int Width, int Height) _transformRange;
    private double _audioRange = -1;
    private Time _shownPosition;
    private bool _updatingBlend;

    public PropertiesPanel()
    {
        InitializeComponent();
        _videoRows = [XRow, YRow, ScaleRow, RotationRow, OpacityRow];
        _audioRows = [GainRow, FadeInRow, FadeOutRow];
        foreach ((_, string name) in EffectUi.BlendModes)
        {
            BlendBox.Items.Add(name);
        }
    }

    private IEnumerable<ParameterRow> PlacementRows => _videoRows.Take(4);

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _editor = new ClipPropertyEditor(services.Session);
        foreach (ParameterRow row in _videoRows.Concat(_audioRows))
        {
            row.Changing += OnRowChanging;
            row.ChangeEnded += OnRowChangeEnded;
            row.Committed += OnRowCommitted;
            row.KeyframeToggled += OnRowKeyframeToggled;
            row.NavigateRequested += OnRowNavigate;
            row.InterpolationChosen += OnRowInterpolation;
            row.ResetRequested += OnRowReset;
            row.KeyframesClearRequested += OnRowClearKeyframes;
        }

        PlacementSection.Remember(services.Settings);
        OpacitySection.Remember(services.Settings);
        AudioSection.Remember(services.Settings);

        EditorSession session = services.Session;
        session.SelectionChanged += (_, _) => Refresh();
        session.ProjectEdited += (_, _) => Refresh();
        session.ProjectOpened += (_, _) => Refresh();
        session.TransportChanged += (_, _) =>
        {
            if (session.Position != _shownPosition && Body.Visibility == Visibility.Visible)
            {
                Refresh();
            }
        };
        Refresh();
    }

    private void Refresh()
    {
        if (_editor is not { } editor)
        {
            return;
        }

        EditorSession session = editor.Session;
        Clip? video = editor.VideoTarget();
        Clip? audio = editor.AudioTarget();
        if (video?.Id != _videoClipId)
        {
            FinishChanges(_videoRows);
        }

        if (audio?.Id != _audioClipId)
        {
            FinishChanges(_audioRows);
        }

        _videoClipId = video?.Id;
        _audioClipId = audio?.Id;
        _shownPosition = session.Position;
        bool any = video is not null || audio is not null;
        EmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        Body.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any)
        {
            return;
        }

        Clip titled = (video ?? audio)!;
        ClipTitle.Text = session.Project.FindMedia(titled.MediaId)?.Name ?? "";
        ToolTipService.SetToolTip(ClipTitle, ClipTitle.Text);
        Visibility videoVisible = video is null ? Visibility.Collapsed : Visibility.Visible;
        PlacementSection.Visibility = videoVisible;
        OpacitySection.Visibility = videoVisible;
        MoreButton.Visibility = videoVisible;
        KeyframeTotal.Visibility = videoVisible;
        TransformNote.Visibility = Visibility.Collapsed;
        AudioSection.Visibility = audio is null ? Visibility.Collapsed : Visibility.Visible;

        if (video is not null)
        {
            ShowVideo(editor, video);
        }

        if (audio is not null)
        {
            ShowAudio(editor, audio);
        }
    }

    private void ShowVideo(ClipPropertyEditor editor, Clip clip)
    {
        Sequence sequence = editor.Session.Sequence;
        if (_transformRange != (sequence.Width, sequence.Height))
        {
            _transformRange = (sequence.Width, sequence.Height);
            IReadOnlyList<ParameterSpec> specs = ParameterSpec.Transform(sequence.Width, sequence.Height);
            for (int i = 0; i < _videoRows.Length; i++)
            {
                ParameterSpec spec = specs[i];
                (string? label, string? axis) = spec.Name switch
                {
                    BuiltInEffects.TransformParameters.X => ("Posición", "X"),
                    BuiltInEffects.TransformParameters.Y => ("", "Y"),
                    _ => ((string?)null, (string?)null),
                };
                _videoRows[i].Configure(spec, label, axis);
            }
        }

        bool inside = editor.IsPlayheadInside(clip);
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        int placementKeyframes = 0;
        int opacityKeyframes = 0;
        foreach (ParameterRow row in _videoRows)
        {
            ParameterState state = editor.Read(clip, row.Spec.EffectType, row.Spec.Name, row.Spec.DefaultValue);
            values[row.Spec.Name] = row.Spec.ToDisplay(state.Value);
            if (ReferenceEquals(row, OpacityRow))
            {
                opacityKeyframes += state.KeyframeCount;
            }
            else
            {
                placementKeyframes += state.KeyframeCount;
            }

            row.Show(state, inside);
        }

        KeyframeTotal.Text = KeyframeText(placementKeyframes + opacityKeyframes);
        PlacementSection.KeyframeSummary = KeyframeText(placementKeyframes);
        OpacitySection.KeyframeSummary = KeyframeText(opacityKeyframes);
        PlacementSection.Summary = "X " + Number(values.GetValueOrDefault(BuiltInEffects.TransformParameters.X))
            + " · Y " + Number(values.GetValueOrDefault(BuiltInEffects.TransformParameters.Y))
            + " · " + Number(values.GetValueOrDefault(BuiltInEffects.TransformParameters.Scale)) + " %"
            + " · " + Number(values.GetValueOrDefault(BuiltInEffects.TransformParameters.Rotation)) + "°";

        ResetAllItem.IsEnabled = ClipPropertyEditor.CanReset(clip, BuiltInEffects.Transform, Defaults(_videoRows));
        PlacementReset.IsEnabled = ClipPropertyEditor.CanReset(clip, BuiltInEffects.Transform, Defaults(PlacementRows));
        EffectInstance? transform = ClipPropertyEditor.FindEffect(clip, BuiltInEffects.Transform);
        TransformNote.Visibility = transform is { Enabled: false } ? Visibility.Visible : Visibility.Collapsed;
        bool canSave = AnimationPreset.CanExport(transform);
        SavePresetItem.IsEnabled = canSave;
        SavePresetItem.Text = canSave ? "Guardar como transición…" : "Guardar como transición (necesita 2 keyframes)";

        string mode = BlendMode(clip);
        _updatingBlend = true;
        BlendBox.SelectedIndex = Math.Max(0, EffectRegistry.BlendModes.IndexOf(mode));
        _updatingBlend = false;
        OpacitySection.Summary = Number(values.GetValueOrDefault(BuiltInEffects.TransformParameters.Opacity)) + " % · "
            + EffectUi.BlendModeName(mode);
    }

    private static string BlendMode(Clip clip) =>
        ClipPropertyEditor.FindEffect(clip, BuiltInEffects.Blend)?.Options.GetValueOrDefault(BuiltInEffects.BlendOptions.Mode, "normal") ?? "normal";

    private static string KeyframeText(int count) => count == 0 ? "" : KeyframeDialog.Count(count);

    // «−120», «12,5»: coma decimal y signo menos tipográfico, como el resto de la interfaz.
    private static string Number(double value) =>
        Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',').Replace('-', '\u2212');

    private void ShowAudio(ClipPropertyEditor editor, Clip clip)
    {
        double seconds = clip.Duration.ToSeconds();
        if (Math.Abs(_audioRange - seconds) > 1e-6)
        {
            _audioRange = seconds;
            IReadOnlyList<ParameterSpec> specs = ParameterSpec.Audio(seconds);
            GainRow.Configure(specs[0]);
            FadeInRow.Configure(specs[1], "Entrada");
            FadeOutRow.Configure(specs[2], "Salida");
        }

        bool inside = editor.IsPlayheadInside(clip);
        int keyframes = 0;
        double gain = 0;
        foreach (ParameterRow row in _audioRows)
        {
            ParameterState state = editor.Read(clip, row.Spec.EffectType, row.Spec.Name, row.Spec.DefaultValue);
            keyframes += state.KeyframeCount;
            if (ReferenceEquals(row, GainRow))
            {
                gain = row.Spec.ToDisplay(state.Value);
            }

            row.Show(state, inside);
        }

        AudioSection.KeyframeSummary = KeyframeText(keyframes);
        AudioSection.Summary = Number(gain) + " " + GainRow.Spec.Unit;
        AudioReset.IsEnabled = ClipPropertyEditor.CanReset(clip, BuiltInEffects.Audio, Defaults(_audioRows));
    }

    private static IEnumerable<(string Name, double DefaultValue)> Defaults(IEnumerable<ParameterRow> rows) =>
        rows.Select(r => (r.Spec.Name, r.Spec.DefaultValue));

    private static void FinishChanges(ParameterRow[] rows)
    {
        foreach (ParameterRow row in rows)
        {
            row.FinishChange();
        }
    }

    private Clip? ClipFor(ParameterRow row)
    {
        string? id = Array.IndexOf(_videoRows, row) >= 0 ? _videoClipId : _audioClipId;
        return id is not null && _editor?.Session.Sequence.FindClip(id) is { } found ? found.Clip : null;
    }

    private void OnRowChanging(object? sender, double value)
    {
        if (sender is not ParameterRow row || _editor is not { } editor || ClipFor(row) is not { } clip)
        {
            return;
        }

        if (!ReferenceEquals(_dragRow, row) || !editor.IsDragging)
        {
            _dragRow = row;
            editor.BeginChange(clip, row.Spec.EffectType, row.Spec.Name, row.Spec.Description);
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
        if (sender is ParameterRow row && _editor is { } editor && ClipFor(row) is { } clip)
        {
            editor.SetValue(clip, row.Spec.EffectType, row.Spec.Name, value, row.Spec.Description);
        }
    }

    private void OnRowKeyframeToggled(object? sender, EventArgs e)
    {
        if (sender is ParameterRow row && _editor is { } editor && ClipFor(row) is { } clip)
        {
            editor.ToggleKeyframe(clip, row.Spec.EffectType, row.Spec.Name, row.Spec.DefaultValue);
        }
    }

    private void OnRowNavigate(object? sender, int direction)
    {
        if (sender is ParameterRow row && _editor is { } editor && ClipFor(row) is { } clip)
        {
            editor.GoToKeyframe(clip, row.Spec.EffectType, row.Spec.Name, direction);
        }
    }

    private void OnRowInterpolation(object? sender, Interpolation interpolation)
    {
        if (sender is ParameterRow row && _editor is { } editor && ClipFor(row) is { } clip)
        {
            editor.SetInterpolation(clip, row.Spec.EffectType, row.Spec.Name, interpolation);
        }
    }

    private async void OnRowReset(object? sender, EventArgs e)
    {
        if (sender is ParameterRow row && _editor is { } editor && ClipFor(row) is { } clip)
        {
            _dragRow = null;
            string label = row.Spec.Label;
            await editor.ResetAsync(clip, row.Spec.EffectType, [.. Defaults([row])], row.Spec.ResetDescription, KeyframeDialog.ForReset(XamlRoot, label));
        }
    }

    private async void OnRowClearKeyframes(object? sender, EventArgs e)
    {
        if (sender is ParameterRow row && _editor is { } editor && ClipFor(row) is { } clip)
        {
            _dragRow = null;
            string label = row.Spec.Label;
            await editor.ClearKeyframesAsync(clip, row.Spec.EffectType, row.Spec.Name, "Quitar keyframes de " + char.ToLowerInvariant(label[0]) + label[1..],
                KeyframeDialog.ForClear(XamlRoot, label));
        }
    }

    private async void OnResetAllClick(object sender, RoutedEventArgs e) =>
        await ResetRowsAsync(_videoRows, "Restablecer transformación", "la transformación");

    private async void OnPlacementResetClick(object sender, RoutedEventArgs e) =>
        await ResetRowsAsync([.. PlacementRows], "Restablecer posición y tamaño", "la posición y el tamaño");

    private async void OnAudioResetClick(object sender, RoutedEventArgs e) =>
        await ResetRowsAsync(_audioRows, "Restablecer audio", "el volumen y los fundidos");

    private async Task ResetRowsAsync(ParameterRow[] rows, string description, string what)
    {
        if (_editor is not { } editor || ClipFor(rows[0]) is not { } clip)
        {
            return;
        }

        FinishChanges(rows);
        _dragRow = null;
        await editor.ResetAsync(clip, rows[0].Spec.EffectType, [.. Defaults(rows)], description, KeyframeDialog.ForResetAll(XamlRoot, what));
    }

    private void OnBlendSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingBlend || BlendBox.SelectedIndex < 0 || _services is not { } services || ClipFor(XRow) is not { } clip)
        {
            return;
        }

        string mode = EffectRegistry.BlendModes[BlendBox.SelectedIndex];
        if (BlendMode(clip) != mode)
        {
            services.Session.Execute(EffectCommands.SetBlendMode(services.Session.SequenceId, clip.Id, mode));
        }
    }

    private async void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        if (DialogGate.IsOpen || _services is not { } services || _videoClipId is null
            || services.Session.Sequence.FindClip(_videoClipId) is not { } found
            || ClipPropertyEditor.FindEffect(found.Clip, BuiltInEffects.Transform) is not { } transform
            || !AnimationPreset.CanExport(transform))
        {
            return;
        }

        EffectInstance animation = transform.Clone();
        Sequence sequence = services.Session.Sequence;
        var nameBox = new TextBox { Text = DefaultPresetName, SelectionStart = 0, SelectionLength = DefaultPresetName.Length };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "La animación de la transformación quedará en tus complementos propios, lista para aplicarla a otros clips o publicarla como paquete.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(nameBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Guardar como transición",
            Content = content,
            PrimaryButtonText = "Guardar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };

        if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        string name = string.IsNullOrWhiteSpace(nameBox.Text) ? DefaultPresetName : nameBox.Text.Trim();
        try
        {
            string path = AnimationPreset.Save(AnimationPreset.UserFolder, name, animation, sequence.Width, sequence.Height);
            services.Session.Notify(NotificationSeverity.Success, "Transición guardada", $"«{name}» quedó en tus complementos propios: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            services.Session.Notify(NotificationSeverity.Error, "No se pudo guardar la transición", ex.Message);
        }
    }
}
