using Dathinel.Sala;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Collab;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Shell;
using Rodaje.App.Theme;
using Windows.UI.Text;

namespace Rodaje.App.Views.Collab;

/// <summary>
/// El panel de la sala (el contenido del menú que cuelga del botón «Sala»): las solicitudes de entrada con su tarjeta de
/// aprobación, quién está y qué puede hacer, los últimos avisos y salir. Se vuelve a pintar entero cada vez que cambia el
/// estado; son pocas filas.
/// </summary>
internal sealed class RoomPanel
{
    private const int MaxNotices = 6;

    private readonly EditorServices _services;
    private readonly CollabHost _collab;
    private readonly Func<Task> _invite;
    private readonly List<RoomNotice> _notices = [];
    private readonly StackPanel _root = new() { Spacing = 12, MinWidth = 380, MaxWidth = 460 };

    public RoomPanel(EditorServices services, CollabHost collab, Func<Task> invite)
    {
        _services = services;
        _collab = collab;
        _invite = invite;
        collab.Room.Noticed += n => AddNotice(n);
        Render(collab.Room.State);
    }

    public FrameworkElement Content => _root;

    private RoomService Room => _collab.Room;

    private void AddNotice(RoomNotice notice)
    {
        _notices.Insert(0, notice);
        if (_notices.Count > MaxNotices)
        {
            _notices.RemoveAt(_notices.Count - 1);
        }

        Render(Room.State);
    }

    public void Render(RoomState state)
    {
        _root.Children.Clear();
        if (state.Phase == RoomPhase.Closed)
        {
            _root.Children.Add(Text("No estás en ninguna sala.", FontWeights.SemiBold));
            _root.Children.Add(InviteDialog.Hint("Invita a otras personas desde Colaborar › Invitar, o únete a la sala de alguien con su invitación."));
            return;
        }

        string host = state.Members.FirstOrDefault(m => m.IsHost)?.Alias ?? "";
        _root.Children.Add(Text(state.IsHost ? "Tu sala" : host.Length > 0 ? $"Sala de {host}" : "Sala", FontWeights.SemiBold, ThemeResources.Size("TitleFontSize")));
        _root.Children.Add(InviteDialog.Hint(StatusLine(state)));

        foreach (RoomRequest request in state.Requests)
        {
            _root.Children.Add(RequestCard(request, state.Requests.Count));
        }

        if (state.Members.Count > 0)
        {
            _root.Children.Add(Text($"En la sala: {state.OnlineCount} " + (state.OnlineCount == 1 ? "persona" : "personas") + " (máximo 8)", FontWeights.SemiBold));
            foreach (RoomMember member in state.Members)
            {
                _root.Children.Add(MemberRow(member, state));
            }
        }

        if (_notices.Count > 0)
        {
            _root.Children.Add(Text("Últimos avisos", FontWeights.SemiBold));
            foreach (RoomNotice n in _notices)
            {
                var line = InviteDialog.Hint($"{n.At.ToLocalTime():HH:mm}  {n.Text}");
                if (n.IsProblem)
                {
                    line.Foreground = ThemeResources.Brush("TextBrush");
                }

                _root.Children.Add(line);
            }
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (state.IsHost && state.Phase == RoomPhase.Hosting)
        {
            var invite = new Button { Content = "Invitar a otras personas…" };
            invite.Click += (_, _) => UiTask.Run(_invite());
            actions.Children.Add(invite);
        }

        if (state.Phase != RoomPhase.Ended)
        {
            var leave = new Button { Content = state.IsHost ? "Cerrar la sala para todos" : "Salir de la sala" };
            leave.Click += (_, _) => UiTask.Run(LeaveAsync(state.IsHost));
            actions.Children.Add(leave);
        }
        else
        {
            var done = new Button { Content = "Entendido" };
            done.Click += (_, _) => UiTask.Run(Room.LeaveAsync());
            actions.Children.Add(done);
        }

        _root.Children.Add(actions);
    }

    private static string StatusLine(RoomState state) => state.Phase switch
    {
        RoomPhase.Hosting => state.Requests.Count > 0 ? "Hay personas esperando que las dejes entrar." : "Quien tenga tu invitación puede pedir entrar; tú decides.",
        RoomPhase.Connected => state.CanEdit ? "Conectado. Puedes editar." : "Conectado. Solo puedes mirar.",
        RoomPhase.Reconnecting => "Sin conexión con quien hospeda la sala. Tus cambios se guardan aquí y se enviarán al volver. Rodaje reintenta solo.",
        RoomPhase.Ended => state.Problem ?? "La sala terminó.",
        _ => "Abriendo la sala…",
    };

    private Border RequestCard(RoomRequest request, int pending)
    {
        var card = new StackPanel { Spacing = 8 };
        card.Children.Add(Text($"{request.Alias} quiere entrar", FontWeights.SemiBold, ThemeResources.Size("TitleFontSize")));
        card.Children.Add(Text("Palabras de comprobación: " + request.Phrase, FontWeights.SemiBold));
        card.Children.Add(InviteDialog.Hint("Acepta solo si conoces a esta persona y las palabras de comprobación coinciden con las que ella ve."));
        if (pending > 1)
        {
            card.Children.Add(InviteDialog.Hint($"Hay {pending} personas esperando."));
        }

        var edit = new Button { Content = "Dejar entrar para editar" };
        var view = new Button { Content = "Dejar entrar solo para mirar" };
        var reject = new Button { Content = "Rechazar" };
        (_collab.DefaultRole == Rol.Editar ? edit : view).Style = ThemeResources.Style("AccentButtonStyle");
        edit.Click += (_, _) => Act(Room.ApproveAsync(request.Key, Rol.Editar));
        view.Click += (_, _) => Act(Room.ApproveAsync(request.Key, Rol.Mirar));
        reject.Click += (_, _) => Act(Room.RejectAsync(request.Key));
        var buttons = new StackPanel { Spacing = 8 };
        buttons.Children.Add(edit);
        buttons.Children.Add(view);
        buttons.Children.Add(reject);
        card.Children.Add(buttons);
        return new Border
        {
            Child = card,
            Padding = new Thickness(12),
            CornerRadius = ThemeResources.Corner("RodajeCornerRadius"),
            Background = ThemeResources.Brush("AccentSubtleBrush"),
            BorderBrush = ThemeResources.Brush("AccentBrush"),
            BorderThickness = new Thickness(1),
        };
    }

    private Grid MemberRow(RoomMember member, RoomState state)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 12, Height = 12, Fill = CollabPalette.BrushOf(member.ColorIndex), Opacity = member.Online ? 1 : 0.35, VerticalAlignment = VerticalAlignment.Center };
        string tag = member.IsMe ? " (tú)" : "";
        string who = member.IsHost ? " · quien hospeda" : "";
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = member.Alias + tag, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(InviteDialog.Hint(member.Online ? (member.Role == Rol.Editar ? "Puede editar" : "Solo puede mirar") + who : "Desconectado · puede volver sin pedir permiso"));
        grid.Children.Add(dot);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (state.IsHost && !member.IsMe && state.Phase == RoomPhase.Hosting)
        {
            var more = new Button { Content = "…", Padding = new Thickness(8, 2, 8, 2) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, $"Opciones de {member.Alias}");
            var menu = new MenuFlyout();
            var role = new MenuFlyoutItem { Text = member.Role == Rol.Editar ? "Cambiar a solo mirar" : "Cambiar a puede editar" };
            role.Click += (_, _) => Act(Room.SetRoleAsync(member.Key, member.Role == Rol.Editar ? Rol.Mirar : Rol.Editar));
            var kick = new MenuFlyoutItem { Text = "Expulsar de la sala…" };
            kick.Click += (_, _) => UiTask.Run(KickAsync(member));
            menu.Items.Add(role);
            menu.Items.Add(kick);
            more.Flyout = menu;
            Grid.SetColumn(more, 2);
            grid.Children.Add(more);
        }

        return grid;
    }

    private async Task KickAsync(RoomMember member)
    {
        if (_services.Shell.XamlRoot is not { } root || DialogGate.IsOpen)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"¿Quieres expulsar a {member.Alias} de la sala?",
            Content = new TextBlock
            {
                Text = "Esta persona se desconectará de inmediato, pero conservará la copia del proyecto que tiene en su equipo. No podrá volver a entrar con la misma invitación.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Expulsar",
            CloseButtonText = "Mantener en la sala",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            await RunAsync(Room.KickAsync(member.Key));
        }
    }

    public async Task LeaveAsync(bool host)
    {
        if (_services.Shell.XamlRoot is not { } root || DialogGate.IsOpen)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = host ? "¿Quieres cerrar la sala para todos?" : "¿Quieres salir de la sala?",
            Content = new TextBlock
            {
                Text = host
                    ? "Todos los invitados se desconectarán y no podrán seguir editando juntos, pero cada uno conservará una copia del proyecto en su equipo."
                    : "Dejarás de ver los cambios de los demás. Tu copia del proyecto se quedará en tu equipo como un proyecto normal e independiente.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = host ? "Cerrar la sala para todos" : "Salir de la sala",
            CloseButtonText = host ? "Mantener la sala abierta" : "Seguir en la sala",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            await Room.LeaveAsync();
        }
    }

    private void Act(Task task) => UiTask.Run(RunAsync(task));

    private async Task RunAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (CollabException ex)
        {
            _services.Session.Notify(NotificationSeverity.Warning, "No se pudo hacer", ex.Message);
        }
    }

    private static TextBlock Text(string text, FontWeight weight, double size = 0) => new()
    {
        Text = text,
        FontWeight = weight,
        FontSize = size > 0 ? size : ThemeResources.Size("BodyFontSize"),
        TextWrapping = TextWrapping.Wrap,
    };
}
