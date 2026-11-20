using Dathinel.Sala;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Collab;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Shell;
using Rodaje.App.Theme;

namespace Rodaje.App.Views.Collab;

/// <summary>
/// «Invitar a otras personas»: pide el nombre, el permiso por defecto y cuánto dura la invitación; abre la sala del proyecto
/// y enseña la invitación para copiarla. Todo en un solo diálogo, sin cerrarlo entre un paso y otro.
/// </summary>
internal static class InviteDialog
{
    public static async Task ShowAsync(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Collab is not { } collab || services.Shell.XamlRoot is not { } root || DialogGate.IsOpen)
        {
            return;
        }

        RoomService room = collab.Room;
        EditorSession session = services.Session;
        if (room.State.InRoom && !room.State.IsHost)
        {
            session.Notify(NotificationSeverity.Informational, "Estás en la sala de otra persona", "Solo quien hospeda el proyecto puede invitar. Sal de la sala si quieres hospedar el tuyo.");
            return;
        }

        if (!room.State.InRoom && !await EnsureSavedAsync(services, root))
        {
            return;
        }

        var name = new TextBox { Header = "Tu nombre en la sala", PlaceholderText = "Escribe cómo te verán los demás", MaxLength = CollabNames.MaxAlias, Text = StartingName(collab, room) };
        var role = new RadioButtons { Header = "Permiso para quien entre", SelectedIndex = collab.DefaultRole == Rol.Editar ? 0 : 1 };
        role.Items.Add("Puede editar");
        role.Items.Add("Solo puede mirar");
        var validity = new RadioButtons { Header = "La invitación sirve durante", SelectedIndex = 1 };
        validity.Items.Add("1 hora");
        validity.Items.Add("24 horas");
        var oneUse = new CheckBox { Content = "Solo sirve para una persona", IsChecked = true };
        var roleHint = Hint("Podrás cambiar el permiso de cada persona cuando quieras, desde el panel de la sala.");
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ThemeResources.Brush("AccentBrush"), Visibility = Visibility.Collapsed };
        var progress = new ProgressRing { IsActive = false, Width = 20, Height = 20, Visibility = Visibility.Collapsed };

        var form = new StackPanel { Spacing = 12, MinWidth = 440 };
        form.Children.Add(Hint("Tus invitados verán y editarán este proyecto contigo. Cada persona que pida entrar tendrá que ser aprobada por ti."));
        form.Children.Add(name);
        form.Children.Add(role);
        form.Children.Add(roleHint);
        form.Children.Add(validity);
        form.Children.Add(oneUse);
        form.Children.Add(problem);
        form.Children.Add(progress);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Invitar a colaborar",
            Content = form,
            PrimaryButtonText = "Crear invitación",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = CollabNames.NormalizeAlias(name.Text) is not null,
        };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = CollabNames.NormalizeAlias(name.Text) is not null;

        bool created = false;
        bool busy = false;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (created)
            {
                return;
            }

            args.Cancel = true;
            if (busy)
            {
                return;
            }

            busy = true;
            dialog.IsPrimaryButtonEnabled = false;
            progress.IsActive = true;
            progress.Visibility = Visibility.Visible;
            problem.Visibility = Visibility.Collapsed;
            UiTask.Run(async () =>
            {
                try
                {
                    CollabInvitation invitation = await CreateAsync(services, collab, name.Text, role.SelectedIndex == 0 ? Rol.Editar : Rol.Mirar,
                        validity.SelectedIndex == 0 ? TimeSpan.FromHours(1) : TimeSpan.FromHours(24), oneUse.IsChecked == true);
                    created = true;
                    dialog.Title = "Invitación lista";
                    dialog.Content = ResultView(invitation, session);
                    dialog.PrimaryButtonText = "Listo";
                    dialog.CloseButtonText = "";
                    dialog.IsPrimaryButtonEnabled = true;
                }
                catch (CollabException ex)
                {
                    problem.Text = ex.Message;
                    problem.Visibility = Visibility.Visible;
                    dialog.IsPrimaryButtonEnabled = CollabNames.NormalizeAlias(name.Text) is not null;
                }
                finally
                {
                    busy = false;
                    progress.IsActive = false;
                    progress.Visibility = Visibility.Collapsed;
                }
            });
        };

        await DialogGate.ShowAsync(dialog);
    }

    private static async Task<CollabInvitation> CreateAsync(EditorServices services, CollabHost collab, string alias, Rol defaultRole, TimeSpan validity, bool oneUse)
    {
        RoomService room = collab.Room;
        collab.DefaultRole = defaultRole;
        if (!room.State.InRoom)
        {
            string? path = services.Session.FilePath;
            if (path is null)
            {
                throw new CollabException("Guarda el proyecto antes de invitar.");
            }

            await room.HostAsync(alias, CollabHost.RoomFolderFor(path));
            collab.Alias = alias;
            collab.StartEditing(null);
        }

        return await room.CreateInviteAsync(validity, oneUse);
    }

    private static StackPanel ResultView(CollabInvitation invitation, EditorSession session)
    {
        var link = new TextBox
        {
            Header = "Texto de la invitación",
            Text = invitation.Link,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MinHeight = 96,
            MaxHeight = 140,
        };
        var copy = new Button { Content = "Copiar invitación", Style = ThemeResources.Style("AccentButtonStyle") };
        var copied = new TextBlock { Text = "Copiada. Ya puedes pegarla en un mensaje.", Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        copy.Click += (_, _) =>
        {
            copied.Visibility = CollabClipboard.TrySetText(invitation.Link) ? Visibility.Visible : Visibility.Collapsed;
            if (copied.Visibility == Visibility.Collapsed)
            {
                session.Notify(NotificationSeverity.Warning, "No se pudo copiar", "Selecciona el texto de la invitación y cópialo con Ctrl+C.");
            }
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(copy);
        row.Children.Add(copied);

        var view = new StackPanel { Spacing = 12, MinWidth = 440 };
        view.Children.Add(link);
        view.Children.Add(row);
        view.Children.Add(Hint($"Mándasela por mensaje. La otra persona debe tener la misma versión de Rodaje ({CollabHost.CurrentVersion()}). "
            + "Cuando intente entrar, te preguntaremos si la dejas pasar."));
        view.Children.Add(Hint("La invitación caduca " + (invitation.ExpiresAt - DateTimeOffset.UtcNow).ToUserText() + (invitation.OneUse ? " y sirve para una sola persona." : ".")));
        return view;
    }

    private static async Task<bool> EnsureSavedAsync(EditorServices services, XamlRoot root)
    {
        EditorSession session = services.Session;
        if (session.FilePath is not null && !session.IsDirty)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Guarda el proyecto antes de invitar",
            Content = new TextBlock
            {
                Text = "Para compartir este proyecto con otras personas, primero debes guardarlo en tu equipo.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Guardar y continuar",
            CloseButtonText = "Volver al proyecto",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary && await session.SaveAsync() && session.FilePath is not null;
    }

    private static string StartingName(CollabHost collab, RoomService room) =>
        room.State.MyAlias.Length > 0 ? room.State.MyAlias : collab.Alias.Length > 0 ? collab.Alias : CollabHost.SuggestedAlias();

    internal static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = ThemeResources.Size("BodyFontSize"),
        Foreground = ThemeResources.Brush("TextMutedBrush"),
        FontWeight = FontWeights.Normal,
    };
}
