using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Collab;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Shell;
using Rodaje.App.Theme;
using Rodaje.App.Views.Relink;

namespace Rodaje.App.Views.Collab;

/// <summary>
/// «Unirse a una sala»: pega la invitación, pone su nombre y elige dónde guardar su copia. Después espera, en el mismo
/// diálogo, a que quien hospeda la sala lo deje entrar; cualquier fallo sale con qué pasó y qué hacer, sin cerrarlo.
/// </summary>
internal static class JoinDialog
{
    public static async Task ShowAsync(EditorServices services, nint hwnd, string? pastedInvite = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Collab is not { } collab || services.Shell.XamlRoot is not { } root || DialogGate.IsOpen)
        {
            return;
        }

        RoomService room = collab.Room;
        if (room.State.InRoom)
        {
            services.Session.Notify(NotificationSeverity.Informational, "Ya estás en una sala", "Sal de ella antes de unirte a otra.");
            return;
        }

        var invite = new TextBox
        {
            Header = "Enlace o texto de invitación",
            PlaceholderText = "Pega aquí el enlace rodaje:// o el texto que te enviaron",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 72,
            MaxHeight = 120,
            Text = pastedInvite ?? "",
        };
        var paste = new Button { Content = "Pegar" };
        paste.Click += async (_, _) =>
        {
            if (await CollabClipboard.TryGetTextAsync() is { Length: > 0 } text)
            {
                invite.Text = text.Trim();
            }
        };
        var name = new TextBox { Header = "Tu nombre en la sala", PlaceholderText = "Escribe cómo te verán los demás", MaxLength = CollabNames.MaxAlias, Text = collab.Alias.Length > 0 ? collab.Alias : CollabHost.SuggestedAlias() };
        string folder = DefaultFolder();
        var folderText = new TextBlock { Text = folder, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var pick = new Button { Content = "Elegir carpeta…" };
        pick.Click += (_, _) =>
        {
            if (FolderDialog.Pick(hwnd, "Carpeta donde guardar tu copia del proyecto", Directory.Exists(folder) ? folder : null) is { } picked)
            {
                folder = picked;
                folderText.Text = folder;
            }
        };
        var folderRow = new Grid { ColumnSpacing = 12 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.Children.Add(folderText);
        Grid.SetColumn(pick, 1);
        folderRow.Children.Add(pick);

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ThemeResources.Brush("AccentBrush"), Visibility = Visibility.Collapsed };
        var progress = new ProgressRing { IsActive = false, Width = 20, Height = 20, Visibility = Visibility.Collapsed };
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        statusRow.Children.Add(progress);
        statusRow.Children.Add(status);

        var form = new StackPanel { Spacing = 12, MinWidth = 460 };
        form.Children.Add(invite);
        form.Children.Add(paste);
        form.Children.Add(name);
        form.Children.Add(new TextBlock { Text = "Carpeta donde guardar tu copia del proyecto", FontSize = ThemeResources.Size("BodyFontSize"), Foreground = ThemeResources.Brush("TextMutedBrush") });
        form.Children.Add(folderRow);
        form.Children.Add(InviteDialog.Hint("Guardamos una copia en tu equipo para que tus cambios no se pierdan si se corta la conexión."));
        form.Children.Add(problem);
        form.Children.Add(statusRow);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Unirse a una sala",
            Content = form,
            PrimaryButtonText = "Unirme",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        var cancel = new CancellationTokenSource();
        bool busy = false;
        bool joined = false;
        string roomFolder = "";

        void Refresh() => dialog.IsPrimaryButtonEnabled = !busy && !string.IsNullOrWhiteSpace(invite.Text) && CollabNames.NormalizeAlias(name.Text) is not null;
        invite.TextChanged += (_, _) => Refresh();
        name.TextChanged += (_, _) => Refresh();
        Refresh();

        dialog.PrimaryButtonClick += (sender, args) =>
        {
            if (joined)
            {
                return;
            }

            args.Cancel = true;
            if (busy)
            {
                return;
            }

            InviteProblem check = CollabInvite.TryParse(invite.Text, DateTimeOffset.UtcNow, out _);
            if (check != InviteProblem.None)
            {
                problem.Text = CollabMessages.ForInvite(check);
                problem.Visibility = Visibility.Visible;
                return;
            }

            busy = true;
            Refresh();
            invite.IsEnabled = name.IsEnabled = pick.IsEnabled = paste.IsEnabled = false;
            problem.Visibility = Visibility.Collapsed;
            status.Text = "Conectando con la sala y esperando a que quien te invitó te deje entrar…";
            status.Visibility = progress.Visibility = Visibility.Visible;
            progress.IsActive = true;
            UiTask.Run(async () =>
            {
                try
                {
                    string alias = CollabNames.NormalizeAlias(name.Text)!;
                    roomFolder = Path.Combine(folder, "Rodaje - sala " + DateTime.Now.ToString("yyyy-MM-dd HH.mm", System.Globalization.CultureInfo.InvariantCulture));
                    await room.JoinAsync(invite.Text, alias, roomFolder, cancel.Token);
                    collab.Alias = alias;
                    joined = true;
                    dialog.Hide();
                }
                catch (OperationCanceledException)
                {
                    // La persona cerró el diálogo mientras esperaba: no hay nada que decirle.
                }
                catch (CollabException ex)
                {
                    problem.Text = ex.Message;
                    problem.Visibility = Visibility.Visible;
                    busy = false;
                    invite.IsEnabled = name.IsEnabled = pick.IsEnabled = paste.IsEnabled = true;
                    status.Visibility = progress.Visibility = Visibility.Collapsed;
                    progress.IsActive = false;
                    Refresh();
                }
            });
        };
        dialog.CloseButtonClick += (_, _) => cancel.Cancel();
        dialog.Closed += (_, _) => cancel.Cancel();

        await DialogGate.ShowAsync(dialog);
        cancel.Dispose();
        if (joined)
        {
            await SharedProjectFlow.RunAsync(services, hwnd, roomFolder);
        }
    }

    private static string DefaultFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rodaje", "Salas");
}
