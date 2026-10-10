using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Inspector.History;

/// <summary>Una fila del historial tal como se muestra.</summary>
internal sealed partial class HistoryItem(HistoryEntry entry)
{
    public HistoryEntry Entry { get; } = entry;

    public int Position => Entry.Position;

    public string Description => Entry.Description;

    public string StepText => Entry.Position.ToString(CultureInfo.InvariantCulture);

    public double TextOpacity => Entry.IsApplied ? 1 : 0.4;

    /// <summary>El texto normal se ve salvo en el paso actual, que va en naranja.</summary>
    public double PlainOpacity => Entry.IsCurrent ? 0 : TextOpacity;

    public double CurrentOpacity => Entry.IsCurrent ? 1 : 0;

    public double SavedOpacity => Entry.IsSaved ? 1 : 0;
}

/// <summary>Historial de deshacer; un clic salta a ese estado con <see cref="EditorSession.JumpToHistory"/>.</summary>
/// <remarks>Solo se tocan las filas que cambiaron, y mientras el panel está oculto se espera a que se vea.</remarks>
internal sealed partial class HistoryPanel : UserControl, IEditorView
{
    private EditorSession? _session;
    private bool _stale;

    public HistoryPanel()
    {
        InitializeComponent();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (_stale && Visibility == Visibility.Visible)
            {
                Refresh();
            }
        });
    }

    public ObservableCollection<HistoryItem> Items { get; } = [];

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _session = services.Session;
        _session.HistoryChanged += (_, _) => Refresh();
        Refresh();
        Bindings.Update();
    }

    private void Refresh()
    {
        if (_session is null || _session.IsInGesture)
        {
            return;
        }

        if (Visibility != Visibility.Visible)
        {
            _stale = true;
            return;
        }

        _stale = false;
        IReadOnlyList<HistoryEntry> entries = _session.HistoryEntries;
        while (Items.Count > entries.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            if (i == Items.Count)
            {
                Items.Add(new HistoryItem(entries[i]));
            }
            else if (Items[i].Entry != entries[i])
            {
                Items[i] = new HistoryItem(entries[i]);
            }
        }

        int applied = _session.History.Count;
        SummaryText.Text = applied == 1 ? "1 paso" : $"{applied} pasos";
        if (entries.FirstOrDefault(e => e.IsCurrent) is { } current)
        {
            List.ScrollIntoView(Items[current.Position]);
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
