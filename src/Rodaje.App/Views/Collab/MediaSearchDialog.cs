using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services.Collab;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Operations;
using Rodaje.App.Shell;
using Rodaje.App.Theme;
using Rodaje.App.Views.Relink;
using Rodaje.Core.Model;
using Microsoft.UI.Text;

namespace Rodaje.App.Views.Collab;

/// <summary>
/// «Busca tus archivos»: el proyecto de la sala trae los medios sin ruta; aquí se buscan en las carpetas que elija la persona
/// por su contenido (tamaño y SHA-256), no por su nombre. Lo que no aparece queda sin conexión, con su aviso.
/// </summary>
internal static class MediaSearchDialog
{
    /// <summary>Devuelve medio → archivo para lo que se encontró (puede estar vacío), o <c>null</c> si la persona cerró el diálogo.</summary>
    /// <param name="proxies">Las copias ligeras que se pueden pedir a quien hospeda por cada medio que no aparezca.</param>
    /// <param name="originals">Los originales que se pueden pedir a quien hospeda; <c>null</c>, sin esa opción.</param>
    public static async Task<IReadOnlyDictionary<string, string>?> ShowAsync(EditorServices services, nint hwnd, ReceivedProject received, string hostName, RoomProxies proxies, RoomOriginals? originals = null)
    {
        ArgumentNullException.ThrowIfNull(proxies);
        if (services.Shell.XamlRoot is not { } root)
        {
            return new Dictionary<string, string>();
        }

        List<MediaRef> projectMedia = received.Project.Media;
        Dictionary<string, MediaManifestEntry> known = received.Media.Where(e => projectMedia.Any(m => m.Id == e.MediaId)).ToDictionary(e => e.MediaId, StringComparer.Ordinal);
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        // Con copia ligera el medio ya se ve, pero el original sigue interesando: la búsqueda en carpeta lo sigue intentando.
        HashSet<string> proxied = new(StringComparer.Ordinal);
        List<string> folders = [];
        var rows = new Dictionary<string, TextBlock>(StringComparer.Ordinal);
        var askButtons = new Dictionary<string, Button>(StringComparer.Ordinal);
        var originalButtons = new Dictionary<string, Button>(StringComparer.Ordinal);

        var list = new StackPanel { Spacing = 6 };

        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progress = new Views.Shared.OperationProgressView();
        var choose = new Button { Content = "Elegir carpetas donde buscar…" };
        var askMissing = new Button { Content = "Pedir copia ligera de los que faltan", Visibility = Visibility.Collapsed };
        var top = new StackPanel { Spacing = 12, MinWidth = 480 };
        top.Children.Add(InviteDialog.Hint("Para editar sin esperas, Rodaje usa los archivos de video y audio que tengas en tu equipo. Los cambios del proyecto se comparten, pero los archivos no viajan por internet."));
        top.Children.Add(new ScrollViewer { Content = list, MaxHeight = 280 });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(choose);
        actions.Children.Add(askMissing);
        top.Children.Add(actions);
        top.Children.Add(progress);
        top.Children.Add(summary);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Busca tus archivos",
            Content = top,
            PrimaryButtonText = "Entrar al proyecto",
            CloseButtonText = "Salir de la sala",
            DefaultButton = ContentDialogButton.Primary,
        };

        var cancel = new CancellationTokenSource();
        CancellationTokenSource? current = null;
        OperationProgress? searching = null;
        // Cada archivo verificado es un avance de verdad: sin él, el «último avance» pararía durante las huellas largas.
        async Task<string?> HashAsync(string path, CancellationToken ct)
        {
            string? hash = await MediaMatch.Sha256Async(path, ct).ConfigureAwait(false);
            searching?.Pulse();
            return hash;
        }

        var matcher = new MediaMatch(hasher: HashAsync);
        progress.OnCancel = () =>
        {
            searching?.MarkCancelling();
            current?.Cancel();
        };

        void Summarize()
        {
            int total = projectMedia.Count;
            int missing = total - found.Count;
            int requestable = projectMedia.Count(m => askButtons.ContainsKey(m.Id) && !found.ContainsKey(m.Id));
            summary.Text = total == 0
                ? "Este proyecto no tiene medios."
                : $"Encontré {found.Count} de {total} medios."
                    + (missing > 0 ? $" Los {(missing == 1 ? "que falta queda" : "que faltan quedan")} sin conexión: se ven en la línea de tiempo pero no se pueden reproducir hasta tener el archivo. Pídele a {hostName} el original" + (requestable > 0 ? " o pide su copia ligera." : ".") : "");
            foreach ((string id, Button ask) in askButtons)
            {
                ask.Visibility = found.ContainsKey(id) ? Visibility.Collapsed : Visibility.Visible;
            }

            foreach ((string id, Button ask) in originalButtons)
            {
                ask.Visibility = found.ContainsKey(id) && !proxied.Contains(id) ? Visibility.Collapsed : Visibility.Visible;
            }

            askMissing.Visibility = requestable > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Una operación a la vez: antes, un segundo clic cancelaba en silencio la que seguía en curso (un original ya aprobado
        // por quien hospeda se perdía). Mientras dura, los demás botones esperan; la barra de progreso trae Cancelar.
        void Busy(bool on)
        {
            foreach (Button b in askButtons.Values.Concat(originalButtons.Values).Append(askMissing).Append(choose))
            {
                b.IsEnabled = !on;
            }
        }

        bool stopAsking = false;

        // Las filas se arman aquí: sus botones usan las funciones de abajo, que leen el diálogo ya montado.
        askMissing.Click += (_, _) => UiTask.Run(RequestMissingAsync());
        foreach (MediaRef media in projectMedia)
        {
            string title = known.TryGetValue(media.Id, out MediaManifestEntry? entry) && entry.Name.Length > 0
                ? entry.Name
                : media.Name.Length > 0 ? media.Name : media.Id;
            var name = new TextBlock { Text = MediaManifest.DisplayName(title), TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
            var state = new TextBlock { Text = known.ContainsKey(media.Id) ? "Buscando…" : "No está en tu equipo", FontSize = ThemeResources.Size("BodyFontSize"), Foreground = ThemeResources.Brush("TextMutedBrush") };
            rows[media.Id] = state;
            var item = new StackPanel();
            item.Children.Add(name);
            item.Children.Add(state);
            if (proxies.CanRequest(media.Id))
            {
                var ask = new Button { Content = "Pedir la copia ligera" };
                ask.Click += (_, _) => UiTask.Run(RequestProxyAsync(media.Id));
                askButtons[media.Id] = ask;
                item.Children.Add(ask);
            }

            if (originals?.MenuText(media.Id) is { } originalText)
            {
                var askOriginal = new Button { Content = originalText.Replace(" a quien hospeda", "", StringComparison.Ordinal) };
                askOriginal.Click += (_, _) => UiTask.Run(RequestOriginalAsync(media.Id));
                originalButtons[media.Id] = askOriginal;
                item.Children.Add(askOriginal);
            }

            list.Children.Add(item);
        }

        /// <summary>Pide a quien hospeda la copia ligera del medio, con el mismo reparto que usa la biblioteca.</summary>
        async Task RequestProxyAsync(string mediaId)
        {
            if (!proxies.CanRequest(mediaId) || found.ContainsKey(mediaId) || cancel.IsCancellationRequested)
            {
                return;
            }

            current?.Cancel();
            using CancellationTokenSource mine = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            current = mine;
            Busy(true);
            var operation = new OperationProgress();
            searching = operation;
            progress.Operation = operation;
            rows[mediaId].Text = "Pidiendo la copia ligera…";
            string? problem = null;
            try
            {
                await proxies.RequestAsync(mediaId, operation, mine.Token);
                if (proxies.AbsolutePathFor(mediaId) is { } file)
                {
                    found[mediaId] = file;
                    proxied.Add(mediaId);
                    rows[mediaId].Text = "Copia ligera recibida (el original sigue faltando)";
                }
            }
            catch (OperationCanceledException)
            {
                stopAsking = true;
                if (!found.ContainsKey(mediaId))
                {
                    rows[mediaId].Text = "No está en tu equipo";
                }
            }
            catch (Exception ex) when (ex is CollabException or InvalidOperationException or IOException)
            {
                if (!found.ContainsKey(mediaId))
                {
                    rows[mediaId].Text = "No llegó la copia ligera";
                    problem = ex.Message;
                }
            }
            finally
            {
                if (ReferenceEquals(current, mine))
                {
                    current = null;
                    searching = null;
                    progress.Operation = null;
                    Busy(false);
                }
            }

            Summarize();
            if (problem is not null)
            {
                summary.Text += " " + problem;
            }
        }

        /// <summary>Pide a quien hospeda el archivo original: llega comprobado y sustituye a la copia ligera si la había.</summary>
        async Task RequestOriginalAsync(string mediaId)
        {
            if (originals is null || (found.ContainsKey(mediaId) && !proxied.Contains(mediaId)) || cancel.IsCancellationRequested)
            {
                return;
            }

            current?.Cancel();
            using CancellationTokenSource mine = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            current = mine;
            Busy(true);
            var operation = new OperationProgress();
            searching = operation;
            progress.Operation = operation;
            string before = rows[mediaId].Text;
            rows[mediaId].Text = "Pidiendo el original…";
            string? problem = null;
            try
            {
                found[mediaId] = await originals.RequestOriginalAsync(mediaId, operation, mine.Token);
                proxied.Remove(mediaId);
                rows[mediaId].Text = "Original recibido";
            }
            catch (OperationCanceledException)
            {
                rows[mediaId].Text = before;
            }
            catch (Exception ex) when (ex is CollabException or InvalidOperationException or IOException)
            {
                rows[mediaId].Text = before;
                problem = ex.Message;
            }
            finally
            {
                if (ReferenceEquals(current, mine))
                {
                    current = null;
                    searching = null;
                    progress.Operation = null;
                    Busy(false);
                }
            }

            Summarize();
            if (problem is not null)
            {
                summary.Text += " " + problem;
            }
        }

        /// <summary>Pide la copia ligera de cada medio del manifiesto que siga sin archivo; una tras otra, como las sirve el anfitrión.</summary>
        async Task RequestMissingAsync()
        {
            stopAsking = false;
            foreach (MediaManifestEntry entry in known.Values)
            {
                if (stopAsking || found.ContainsKey(entry.MediaId))
                {
                    continue;
                }

                await RequestProxyAsync(entry.MediaId);
            }
        }

        async Task SearchAsync(IReadOnlyList<string> where)
        {
            List<MediaManifestEntry> wanted = [.. known.Values.Where(e => !found.ContainsKey(e.MediaId) || proxied.Contains(e.MediaId))];
            if (wanted.Count == 0 || where.Count == 0)
            {
                return;
            }

            current?.Cancel();
            using CancellationTokenSource mine = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            current = mine;
            Busy(true);
            var operation = new OperationProgress();
            operation.SetCancellable(true);
            JoinFlowProgress.Searching(operation);
            searching = operation;
            progress.Operation = operation;
            foreach (MediaManifestEntry e in wanted)
            {
                rows[e.MediaId].Text = "Buscando…";
            }

            try
            {
                // El avance se crea aquí, en el hilo de la interfaz: dentro de Task.Run avisaría desde un hilo de trabajo.
                var scanned = new Progress<int>(n => operation.Report(null, $"{n} archivos revisados"));
                MediaMatchReport report = await Task.Run(() => matcher.SearchAsync(wanted, where, scanned, mine.Token));
                foreach ((string id, string path) in report.Found)
                {
                    found[id] = path;
                    proxied.Remove(id);
                    rows[id].Text = "Encontrado";
                }

                foreach (MediaManifestEntry e in report.Missing)
                {
                    rows[e.MediaId].Text = proxied.Contains(e.MediaId)
                        ? "Copia ligera recibida (el original sigue faltando)"
                        : "No está en tu equipo";
                }

                Summarize();
                if (report.CloudOnly > 0)
                {
                    summary.Text += $" {report.CloudOnly} archivos parecidos solo están en la nube (OneDrive): descárgalos y vuelve a buscar.";
                }

                operation.Complete();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (ReferenceEquals(current, mine))
                {
                    current = null;
                    searching = null;
                    progress.Operation = null;
                    Busy(false);
                }
            }
        }

        choose.Click += (_, _) =>
        {
            if (FolderDialog.Pick(hwnd, "Carpeta donde buscar tus videos y audios", folders.Count > 0 ? folders[^1] : null) is { } picked)
            {
                if (!folders.Contains(picked))
                {
                    folders.Add(picked);
                }

                UiTask.Run(SearchAsync([picked]));
            }
        };
        dialog.Opened += (_, _) =>
        {
            Summarize();
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (videos.Length > 0 && Directory.Exists(videos))
            {
                folders.Add(videos);
                UiTask.Run(SearchAsync([videos]));
            }
            else
            {
                foreach (MediaManifestEntry e in known.Values)
                {
                    rows[e.MediaId].Text = "No está en tu equipo";
                }
            }
        };
        dialog.Closed += (_, _) => cancel.Cancel();

        ContentDialogResult result = await DialogGate.ShowAsync(dialog) ?? ContentDialogResult.None;
        cancel.Dispose();
        return result == ContentDialogResult.Primary ? found : null;
    }
}
