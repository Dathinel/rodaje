using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Shell;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;

namespace Rodaje.App.Views.Inspector.Filters;

/// <summary>Un efecto de la pila tal como se muestra.</summary>
internal sealed partial class FilterItem : IEquatable<FilterItem>
{
    public FilterItem(EffectInstance effect, EffectSupport support, int index, int count)
    {
        EffectId = effect.Id;
        Name = EffectCatalog.DisplayName(effect.Type);
        IsEnabled = effect.Enabled;
        TextOpacity = effect.Enabled ? 1 : 0.5;
        Note = support switch
        {
            EffectSupport.Pending => "Se aplica en la fase 5",
            EffectSupport.Shadowed => "Solo se aplica la primera transformación activa",
            _ => "",
        };
        NoteVisibility = Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CanMoveUp = index > 0;
        CanMoveDown = index < count - 1;
    }

    public string EffectId { get; }

    public string Name { get; }

    public bool IsEnabled { get; }

    public double TextOpacity { get; }

    public string Note { get; }

    public Visibility NoteVisibility { get; }

    public bool CanMoveUp { get; }

    public bool CanMoveDown { get; }

    public bool Equals(FilterItem? other) =>
        other is not null && EffectId == other.EffectId && Name == other.Name && IsEnabled == other.IsEnabled
        && Note == other.Note && CanMoveUp == other.CanMoveUp && CanMoveDown == other.CanMoveDown;

    public override bool Equals(object? obj) => Equals(obj as FilterItem);

    public override int GetHashCode() => HashCode.Combine(EffectId, IsEnabled, Note, CanMoveUp, CanMoveDown);
}

/// <summary>Pila de filtros del clip seleccionado; todo cambio es un comando deshacible.</summary>
internal sealed partial class FiltersPanel : UserControl, IEditorView
{
    private EditorSession? _session;
    private string? _clipId;
    private TrackKind _kind;
    private IReadOnlyList<EffectDescriptor> _addable = [];
    private bool _updating;

    public FiltersPanel()
    {
        InitializeComponent();
    }

    public ObservableCollection<FilterItem> Items { get; } = [];

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _session = services.Session;
        _session.SelectionChanged += (_, _) => Refresh();
        _session.ProjectEdited += (_, _) => Refresh();
        _session.ProjectOpened += (_, _) => Refresh();
        Refresh();
        Bindings.Update();
    }

    private (Track Track, Clip Clip)? Target()
    {
        if (_session is null)
        {
            return null;
        }

        Sequence sequence = _session.Sequence;
        foreach (string id in _session.SelectedClipIds)
        {
            if (sequence.FindClip(id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void Refresh()
    {
        if (_session is null)
        {
            return;
        }

        (Track Track, Clip Clip)? target = Target();
        bool any = target is not null;
        EmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        Header.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (target is not { } found)
        {
            _clipId = null;
            Items.Clear();
            NoFiltersText.Visibility = Visibility.Collapsed;
            return;
        }

        (Track track, Clip clip) = found;
        _clipId = clip.Id;
        ClipTitle.Text = _session.Project.FindMedia(clip.MediaId)?.Name ?? "Clip";
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

        var next = new List<FilterItem>(clip.Effects.Count);
        for (int i = 0; i < clip.Effects.Count; i++)
        {
            EffectInstance effect = clip.Effects[i];
            next.Add(new FilterItem(effect, EffectCatalog.Support(clip, effect, track.Kind), i, clip.Effects.Count));
        }

        // Arrastrar un deslizador de Propiedades edita el proyecto en cada paso: solo se rehace la lista si cambió.
        if (next.Count == Items.Count && next.Zip(Items).All(p => p.First.Equals(p.Second)))
        {
            return;
        }

        Items.Clear();
        foreach (FilterItem item in next)
        {
            Items.Add(item);
        }

        NoFiltersText.Visibility = next.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Run(Func<string, string, ICommand> command)
    {
        if (_session is not null && _clipId is not null)
        {
            _session.Execute(command(_session.SequenceId, _clipId));
        }
    }

    private int IndexOf(string effectId) =>
        _clipId is not null && _session?.Sequence.FindClip(_clipId) is { } found ? found.Clip.Effects.FindIndex(e => e.Id == effectId) : -1;

    private void OnAddSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || AddBox.SelectedIndex < 0)
        {
            return;
        }

        string type = _addable[AddBox.SelectedIndex].Type;
        _updating = true;
        AddBox.SelectedIndex = -1;
        _updating = false;
        Run((sequence, clip) => Edits.AddEffect(sequence, clip, type));
    }

    private void OnEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string effectId } box)
        {
            bool enabled = box.IsChecked == true;
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
}
