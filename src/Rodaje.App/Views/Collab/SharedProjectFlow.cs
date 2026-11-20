using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Collab;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Shell;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Views.Collab;

/// <summary>
/// Lo que pasa justo después de entrar en una sala: recibir el proyecto del anfitrión, buscar sus medios en este equipo,
/// guardarlo en la carpeta de la copia y abrirlo. Si algo falla, el diálogo dice qué pasó y deja reintentar o salir.
/// </summary>
internal static class SharedProjectFlow
{
    public static async Task RunAsync(EditorServices services, nint hwnd, string roomFolder)
    {
        if (services.Collab is not { } collab)
        {
            return;
        }

        RoomService room = collab.Room;
        string host = room.State.Members.FirstOrDefault(m => m.IsHost)?.Alias ?? "quien hospeda la sala";
        ReceivedProject? received = null;
        while (received is null)
        {
            (received, bool retry) = await ReceiveWithDialogAsync(services, room, roomFolder);
            if (received is null && !retry)
            {
                await room.LeaveAsync();
                services.Session.Notify(NotificationSeverity.Informational, "Saliste de la sala", "No se recibió el proyecto. Puedes volver a entrar con la invitación o pedir otra.");
                return;
            }
        }

        IReadOnlyDictionary<string, string>? found = await MediaSearchDialog.ShowAsync(services, hwnd, received, host);
        found ??= new Dictionary<string, string>();
        foreach ((string id, string path) in found)
        {
            if (received.Project.FindMedia(id) is { } media)
            {
                media.Path = path;
            }
        }

        string file = UniquePath(roomFolder, received.Name);
        try
        {
            Project copy = ProjectPaths.ForSaving(received.Project, roomFolder);
            await Task.Run(() => ProjectSerializer.Save(copy, file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectFormatException)
        {
            services.Session.Notify(NotificationSeverity.Error, "No se pudo guardar tu copia del proyecto", ex.Message);
            await room.LeaveAsync();
            return;
        }

        while (!await services.Session.OpenProjectAsync(file))
        {
            if (!await AskOpenAgainAsync(services, file))
            {
                await room.LeaveAsync();
                services.Session.Notify(NotificationSeverity.Informational, "Saliste de la sala", $"Tu copia del proyecto está en «{file}»; puedes abrirla cuando quieras.");
                return;
            }
        }

        int lost = received.Project.Media.Count - found.Count;
        services.Session.Notify(
            lost > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success,
            "Estás en la sala",
            lost > 0
                ? $"{lost} {(lost == 1 ? "medio no está" : "medios no están")} en tu equipo: se ven en la línea de tiempo, pero no se pueden reproducir hasta tener el archivo."
                : "Ya tienes el proyecto y todos sus medios.");
    }

    private static async Task<(ReceivedProject? Received, bool Retry)> ReceiveWithDialogAsync(EditorServices services, RoomService room, string roomFolder)
    {
        if (services.Shell.XamlRoot is not { } root)
        {
            return (null, false);
        }

        var bar = new ProgressBar { IsIndeterminate = true, MinWidth = 420 };
        var text = new TextBlock { Text = "Pidiendo el proyecto a quien hospeda la sala…", TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(text);
        panel.Children.Add(bar);
        var dialog = new ContentDialog { XamlRoot = root, Title = "Recibiendo el proyecto", Content = panel, CloseButtonText = "Salir de la sala" };

        using var cancel = new CancellationTokenSource();
        var progress = new Progress<double>(p =>
        {
            bar.IsIndeterminate = false;
            bar.Value = p * 100;
            text.Text = $"Recibiendo el proyecto ({(int)(p * 100)} %)…";
        });
        Task<ReceivedProject> work = ProjectShareGuest.ReceiveAsync(room, roomFolder, progress, cancel.Token);
        string? problem = null;
        _ = work.ContinueWith(
            t =>
            {
                problem = t.Exception?.GetBaseException() is CollabException c ? c.Message : t.IsFaulted ? CollabMessages.ForUnexpected() : null;
                dialog.Hide();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());

        dialog.Opened += (_, _) =>
        {
            if (work.IsCompleted)
            {
                dialog.Hide();
            }
        };
        await DialogGate.ShowAsync(dialog);
        if (!work.IsCompleted)
        {
            await cancel.CancelAsync();
            return (null, false);
        }

        if (work.IsCompletedSuccessfully)
        {
            return (work.Result, false);
        }

        var retry = new ContentDialog
        {
            XamlRoot = root,
            Title = "No se pudo recibir el proyecto",
            Content = new TextBlock { Text = problem ?? CollabMessages.ForUnexpected(), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Reintentar",
            CloseButtonText = "Salir de la sala",
            DefaultButton = ContentDialogButton.Primary,
        };
        return (null, await DialogGate.ShowAsync(retry) == ContentDialogResult.Primary);
    }

    private static async Task<bool> AskOpenAgainAsync(EditorServices services, string file)
    {
        if (services.Shell.XamlRoot is not { } root)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "El proyecto de la sala no se abrió",
            Content = new TextBlock
            {
                Text = $"Para trabajar en la sala hay que abrir su proyecto. Tu copia está guardada en «{file}». Si sales de la sala, la copia se queda ahí.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Abrir ahora",
            CloseButtonText = "Salir de la sala",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private static string UniquePath(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        string basePath = Path.Combine(folder, name);
        string path = basePath + ProjectSerializer.FileExtension;
        for (int i = 2; File.Exists(path) && i < 1000; i++)
        {
            path = $"{basePath} ({i}){ProjectSerializer.FileExtension}";
        }

        return path;
    }
}
