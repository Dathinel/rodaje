using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Inspector.History;

/// <summary>Una fila del historial tal como se muestra.</summary>
internal sealed partial class HistoryItem(HistoryEntry entry)
{
    public int Position { get; } = entry.Position;

    public string Description { get; } = entry.Description;

    public double TextOpacity { get; } = entry.IsApplied ? 1 : 0.45;

    public double CurrentOpacity { get; } = entry.IsCurrent ? 1 : 0;

    public string SavedText { get; } = entry.IsSaved ? "guardado" : "";
}

/// <summary>Historial de deshacer; un clic salta a ese estado con <see cref="EditorSession.JumpToHistory"/>.</summary>
internal sealed partial class HistoryPanel : UserControl, IEditorView
{
    private EditorSession? _session;

    public HistoryPanel()
    {
        InitializeComponent();
    }

    public ObservableCollection<HistoryItem> Items { get; } = [];

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _session = services.Session;
        _session.HistoryChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        if (_session is null || _session.IsInGesture)
        {
            return;
        }

        Items.Clear();
        foreach (HistoryEntry entry in _session.HistoryEntries)
        {
            Items.Add(new HistoryItem(entry));
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryItem item)
        {
            _session?.JumpToHistory(item.Position);
        }
    }
}
