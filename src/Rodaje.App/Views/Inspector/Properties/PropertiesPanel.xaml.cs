using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Shell;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Inspector.Properties;

/// <summary>Propiedades del clip seleccionado: transformación animable con keyframes y audio.</summary>
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

    public PropertiesPanel()
    {
        InitializeComponent();
        _videoRows = [XRow, YRow, ScaleRow, RotationRow, OpacityRow];
        _audioRows = [GainRow, FadeInRow, FadeOutRow];
    }

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
        }

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
        VideoSection.Visibility = video is null ? Visibility.Collapsed : Visibility.Visible;
        AudioSection.Visibility = audio is null ? Visibility.Collapsed : Visibility.Visible;
        SectionSeparator.Visibility = video is not null && audio is not null ? Visibility.Visible : Visibility.Collapsed;

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
            Configure(_videoRows, ParameterSpec.Transform(sequence.Width, sequence.Height));
        }

        bool inside = editor.IsPlayheadInside(clip);
        int keyframes = 0;
        foreach (ParameterRow row in _videoRows)
        {
            ParameterState state = editor.Read(clip, row.Spec.EffectType, row.Spec.Name, row.Spec.DefaultValue);
            keyframes += state.KeyframeCount;
            row.Show(state, inside);
        }

        KeyframeTotal.Text = keyframes switch
        {
            0 => "",
            1 => "1 keyframe",
            _ => $"{keyframes} keyframes",
        };

        EffectInstance? transform = ClipPropertyEditor.FindEffect(clip, BuiltInEffects.Transform);
        TransformNote.Visibility = transform is { Enabled: false } ? Visibility.Visible : Visibility.Collapsed;
        TransformNote.Text = "Transformar está desactivado en Filtros: los cambios se guardan pero no se ven.";
        bool canSave = AnimationPreset.CanExport(transform);
        SavePresetButton.IsEnabled = canSave;
        SavePresetHint.Visibility = canSave ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowAudio(ClipPropertyEditor editor, Clip clip)
    {
        double seconds = clip.Duration.ToSeconds();
        if (Math.Abs(_audioRange - seconds) > 1e-6)
        {
            _audioRange = seconds;
            Configure(_audioRows, ParameterSpec.Audio(seconds));
        }

        bool inside = editor.IsPlayheadInside(clip);
        foreach (ParameterRow row in _audioRows)
        {
            row.Show(editor.Read(clip, row.Spec.EffectType, row.Spec.Name, row.Spec.DefaultValue), inside);
        }
    }

    private static void Configure(ParameterRow[] rows, IReadOnlyList<ParameterSpec> specs)
    {
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i].Configure(specs[i]);
        }
    }

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
