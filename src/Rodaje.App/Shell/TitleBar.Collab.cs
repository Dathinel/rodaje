using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Collab;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Views.Collab;

namespace Rodaje.App.Shell;

/// <summary>El menú Colaborar y el botón «Sala»: invitar, unirse, el panel de la sala y salir.</summary>
internal sealed partial class TitleBar
{
    private RoomPanel? _roomPanel;
    private Flyout? _roomFlyout;
    private RoomState _collabLast = RoomState.Closed;

    /// <summary>Se eligió «Unirse a una sala»: la ventana abre el diálogo (necesita su manejador para elegir carpetas).</summary>
    public event EventHandler? CollabJoinRequested;

    private void AttachCollab(EditorServices services)
    {
        if (services.Collab is not { } collab)
        {
            return;
        }

        _roomPanel = new RoomPanel(services, collab, () => InviteDialog.ShowAsync(services));
        _roomFlyout = new Flyout { Content = new ScrollViewer { Content = _roomPanel.Content, MaxHeight = 640, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        _roomFlyout.Opened += (_, _) => _roomPanel.StartDiagnostics();
        _roomFlyout.Closed += (_, _) => _roomPanel.StopDiagnostics();
        collab.Room.StateChanged += state => UpdateCollab(state);
        UpdateCollab(collab.Room.State);
    }

    /// <summary>Abre el panel de la sala (por ejemplo, cuando alguien pide entrar).</summary>
    public void ShowRoomPanel()
    {
        if (_roomFlyout is null || CollabButton.XamlRoot is null || CollabButton.Visibility != Visibility.Visible)
        {
            return;
        }

        _roomFlyout.ShowAt(CollabButton);
    }

    private void UpdateCollab(RoomState state)
    {
        RoomState before = _collabLast;
        _collabLast = state;
        _roomPanel?.Render(state);

        bool inRoom = state.Phase != RoomPhase.Closed;
        CollabButton.Visibility = inRoom ? Visibility.Visible : Visibility.Collapsed;
        CollabButton.Content = state.Phase switch
        {
            RoomPhase.Reconnecting => "Sala · sin conexión",
            RoomPhase.Ended => "Sala terminada",
            RoomPhase.Starting => "Sala · abriendo…",
            _ => state.Requests.Count > 0
                ? $"Sala · {state.Requests.Count} en espera"
                : $"Sala · {Math.Max(state.OnlineCount, 1)} {(state.OnlineCount <= 1 ? "persona" : "personas")}",
        };
        CollabButton.Style = state.Requests.Count > 0 ? ThemeResourcesStyle("AccentButtonStyle") : null;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CollabButton, "Panel de la sala");

        CollabInviteItem.IsEnabled = !(state.InRoom && !state.IsHost) && state.Phase is not (RoomPhase.Starting or RoomPhase.Ended);
        CollabJoinItem.IsEnabled = state.Phase == RoomPhase.Closed;
        CollabPanelItem.IsEnabled = inRoom;
        CollabLeaveItem.IsEnabled = inRoom;
        CollabLeaveItem.Text = state.Phase == RoomPhase.Ended ? "Cerrar el aviso de la sala" : state.IsHost ? "Cerrar la sala para todos" : "Salir de la sala";

        if (state.Requests.Count > before.Requests.Count && _services is { } services)
        {
            RoomRequest newest = state.Requests[^1];
            services.Session.Notify(NotificationSeverity.Informational, $"{newest.Alias} quiere entrar", "Decide en el panel de la sala si la dejas pasar.");
            ShowRoomPanel();
        }
        else if (state.Phase == RoomPhase.Ended && before.Phase != RoomPhase.Ended && state.Problem is { } problem)
        {
            UiTask.Run(ShowEndedAsync(problem));
        }
    }

    private async Task ShowEndedAsync(string problem)
    {
        if (XamlRoot is not { } root || DialogGate.IsOpen || _services is not { } services)
        {
            _services?.Session.Notify(NotificationSeverity.Warning, "La sala terminó", problem);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "La sala terminó",
            Content = new TextBlock { Text = problem, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "Entendido",
            DefaultButton = ContentDialogButton.Close,
        };
        await DialogGate.ShowAsync(dialog);
        if (services.Collab?.Room.State.Phase == RoomPhase.Ended)
        {
            await services.Collab.Room.LeaveAsync();
        }
    }

    private static Style ThemeResourcesStyle(string key) => Theme.ThemeResources.Style(key);

    private void OnCollabInviteClick(object sender, RoutedEventArgs e)
    {
        if (_services is { } services)
        {
            UiTask.Run(InviteDialog.ShowAsync(services));
        }
    }

    private void OnCollabJoinClick(object sender, RoutedEventArgs e) => CollabJoinRequested?.Invoke(this, EventArgs.Empty);

    private void OnCollabPanelClick(object sender, RoutedEventArgs e) => ShowRoomPanel();

    private void OnCollabButtonClick(object sender, RoutedEventArgs e) => ShowRoomPanel();

    private void OnCollabLeaveClick(object sender, RoutedEventArgs e)
    {
        if (_services?.Collab is not { } collab)
        {
            return;
        }

        if (collab.Room.State.Phase == RoomPhase.Ended)
        {
            UiTask.Run(collab.Room.LeaveAsync());
            return;
        }

        if (_roomPanel is { } panel)
        {
            UiTask.Run(panel.LeaveAsync(collab.Room.State.IsHost));
        }
    }
}
