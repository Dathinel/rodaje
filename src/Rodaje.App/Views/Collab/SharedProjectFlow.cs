using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Collab;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Operations;
using Rodaje.App.Shell;
using Rodaje.App.Views.Relink;
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
        for (int attempt = 1; ; attempt++)
        {
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

            // El medio se queda con el nombre que trae el manifiesto (el archivo del anfitrión): así lo enseña cualquier
            // aviso de «Faltan medios» en vez del id interno o de «Medio sin nombre».
            MediaPlaceholders.FillNames(received.Project, received.Media);

            var proxies = new RoomProxies(room, roomFolder, received.Media);
            var originals = new RoomOriginals(room, roomFolder, received.Media, () => collab.OriginalCap?.Invoke());
            IReadOnlyDictionary<string, string>? found = await MediaSearchDialog.ShowAsync(services, hwnd, received, host, proxies, originals);
            if (found is null)
            {
                // Cerrar «Busca tus archivos» o pulsar «Salir de la sala» es salir: no se abre un proyecto que la persona no quiso abrir.
                await room.LeaveAsync();
                services.Session.Notify(NotificationSeverity.Informational, "Saliste de la sala", "Todavía no se abrió ningún proyecto. Puedes volver a entrar con la invitación.");
                return;
            }

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

            // «Busca tus archivos» ya resolvió estos medios: abrir la copia no debe sacar otro «Faltan medios» con lo mismo.
            using (RelinkDialog.SkipCheckWhileOpening(file))
            {
                while (!await services.Session.OpenProjectAsync(file))
                {
                    if (!await AskOpenAgainAsync(services, file))
                    {
                        await room.LeaveAsync();
                        services.Session.Notify(NotificationSeverity.Informational, "Saliste de la sala", $"Tu copia del proyecto está en «{file}»; puedes abrirla cuando quieras.");
                        return;
                    }
                }
            }

            if (collab.StartEditing(received))
            {
                int lost = received.Project.Media.Count - found.Count;
                services.Session.Notify(
                    lost > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success,
                    "Estás en la sala",
                    lost > 0
                        ? $"{lost} {(lost == 1 ? "medio no está" : "medios no están")} en tu equipo: se ven en la línea de tiempo, pero no se pueden reproducir hasta tener el archivo."
                        : "Ya tienes el proyecto y todos sus medios.");
                return;
            }

            // Mientras llegaba la copia se perdieron cambios de la sala: la copia nueva que se pida ya los trae.
            if (attempt >= 3)
            {
                await room.LeaveAsync();
                services.Session.Notify(NotificationSeverity.Warning, "Saliste de la sala", "La sala no paraba de cambiar y tu copia no llegó entera varias veces seguidas. Vuelve a entrar con la invitación cuando veas que está más tranquila.");
                return;
            }
        }
    }

    private static async Task<(ReceivedProject? Received, bool Retry)> ReceiveWithDialogAsync(EditorServices services, RoomService room, string roomFolder)
    {
        if (services.Shell.XamlRoot is not { } root)
        {
            return (null, false);
        }

        // Los pasos del reparto llegan como fases 2 a 4 del camino («Paso 3 de 5: …»); el diálogo ya ofrece Cancelar abajo.
        var operation = new OperationProgress();
        var view = new Views.Shared.OperationProgressView { Operation = operation, MinWidth = 440 };
        var dialog = new ContentDialog { XamlRoot = root, Title = "Entrando en la sala", Content = view, CloseButtonText = "Cancelar y salir de la sala" };

        using var cancel = new CancellationTokenSource();
        Task<ReceivedProject> work = ProjectShareGuest.ReceiveAsync(room, roomFolder, JoinFlowProgress.Receive(operation), cancel.Token);
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
            operation.Complete();
            return (await work, false);
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
