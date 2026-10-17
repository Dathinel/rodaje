using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Relink;
using Rodaje.App.Services.Settings;
using Rodaje.App.Shell;
using Rodaje.App.Theme;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Relink;

/// <summary>
/// «Faltan medios»: al abrir un proyecto cuyos archivos se movieron o renombraron, la lista de lo que falta, «Buscar en
/// carpeta» (encuentra todos de una vez) y «Elegir…» para cada uno. Todo se aplica en un paso del historial; si se deja
/// para más tarde, un aviso permite volver a abrirlo. La lógica está en <see cref="MediaRelinkService"/>.
/// </summary>
internal static class RelinkDialog
{
    private static Func<Task>? s_reopen;

    /// <summary>Muestra el diálogo cada vez que se abre un proyecto con medios que faltan.</summary>
    public static void Attach(EditorServices services, nint hwnd)
    {
        ArgumentNullException.ThrowIfNull(services);
        var relink = new MediaRelinkService(new FfmpegMediaProber());
        relink.Attach(services.Session, missing => ShowAsync(services, hwnd, relink, missing));
        s_reopen = async () =>
        {
            Project project = services.Session.Project;
            IReadOnlyList<MediaRef> missing = await relink.FindMissingAsync(project);
            if (!ReferenceEquals(project, services.Session.Project))
            {
                return;
            }

            if (missing.Count > 0)
            {
                await ShowAsync(services, hwnd, relink, missing);
            }
            else
            {
                services.Session.Notify(NotificationSeverity.Success, "Ya están todos los medios", "Ningún medio del proyecto falta en el disco.");
            }
        };
    }

    /// <summary>«Volver a enlazar…» de la biblioteca: mira qué falta ahora y abre el diálogo.</summary>
    public static Task ReopenAsync() => s_reopen?.Invoke() ?? Task.CompletedTask;

    private sealed class Row(MediaRef media, TextBlock status, Button pick, Button confirm)
    {
        public MediaRef Media { get; } = media;

        public TextBlock Status { get; } = status;

        public Button Pick { get; } = pick;

        /// <summary>«Confirmar», visible solo cuando hay un archivo con otro nombre propuesto para el medio.</summary>
        public Button Confirm { get; } = confirm;

        /// <summary>Carpeta donde empieza «Elegir…» (la de un candidato dudoso, si lo hubo).</summary>
        public string? StartFolder { get; set; }
    }

    public static async Task ShowAsync(EditorServices services, nint hwnd, MediaRelinkService relink, IReadOnlyList<MediaRef> missing)
    {
        EditorSession session = services.Session;
        Project project = session.Project;
        if (services.Shell.XamlRoot is not { } root || DialogGate.IsOpen)
        {
            NotifyRemaining(services, hwnd, relink, missing);
            return;
        }

        var pending = new Dictionary<string, MediaRelinkTarget>(StringComparer.Ordinal);

        // Encontrados con otro nombre: no se comparó el contenido, así que esperan a que el usuario los confirme.
        var proposed = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = new List<Row>();
        var panel = new StackPanel { Spacing = 8, MinWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = missing.Count == 1
                ? "Este medio no está donde estaba. Búscalo en una carpeta o elige el archivo; sus clips, efectos y keyframes no cambian."
                : "Estos medios no están donde estaban. Búscalos en una carpeta o elige cada archivo; sus clips, efectos y keyframes no cambian.",
            TextWrapping = TextWrapping.Wrap,
        });

        var list = new StackPanel { Spacing = 8 };
        foreach (MediaRef media in missing)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            int clips = MediaRelinkEdits.CountClips(project, media.Id);
            text.Children.Add(new TextBlock { Text = MediaRelinkSearch.FileName(media.Path), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = media.Path, FontSize = ThemeResources.Size("BodyFontSize"), Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
            var status = new TextBlock { Text = clips == 1 ? "Falta · en 1 clip" : $"Falta · en {clips} clips", FontSize = ThemeResources.Size("BodyFontSize"), TextWrapping = TextWrapping.Wrap };
            text.Children.Add(status);
            var pick = new Button { Content = "Elegir…", VerticalAlignment = VerticalAlignment.Center };
            var confirm = new Button { Content = "Confirmar", VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(confirm);
            buttons.Children.Add(pick);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(text);
            grid.Children.Add(buttons);
            list.Children.Add(grid);
            rows.Add(new Row(media, status, pick, confirm));
        }

        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320 });
        var search = new Button { Content = "Buscar en carpeta…" };
        var progress = new ProgressRing { IsActive = false, Width = 20, Height = 20 };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(search);
        actions.Children.Add(progress);
        actions.Children.Add(summary);
        panel.Children.Add(actions);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = missing.Count == 1 ? "Falta un medio" : $"Faltan {missing.Count} medios",
            Content = panel,
            PrimaryButtonText = "Volver a enlazar",
            CloseButtonText = "Más tarde",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            RequestedTheme = ElementTheme.Dark,
        };

        var cancel = new CancellationTokenSource();
        bool searching = false;

        void Refresh()
        {
            dialog.IsPrimaryButtonEnabled = pending.Count > 0 && !searching;
            search.IsEnabled = !searching && pending.Count + proposed.Count < missing.Count;
            progress.IsActive = searching;
            foreach (Row row in rows)
            {
                row.Pick.IsEnabled = !searching;
                row.Confirm.IsEnabled = !searching;
                row.Confirm.Visibility = proposed.ContainsKey(row.Media.Id) ? Visibility.Visible : Visibility.Collapsed;
            }

            if (!searching)
            {
                summary.Text = (pending.Count == 0 ? "" : $"{pending.Count} de {missing.Count} listos.")
                    + (proposed.Count == 0 ? "" : $" {proposed.Count} por confirmar.");
            }
        }

        async Task SearchAsync(string folder)
        {
            List<MediaRef> wanted = [.. missing.Where(m => !pending.ContainsKey(m.Id) && !proposed.ContainsKey(m.Id))];
            if (wanted.Count == 0)
            {
                return;
            }

            searching = true;
            summary.Text = "Buscando…";
            Refresh();
            try
            {
                var scanned = new Progress<int>(n => summary.Text = $"Buscando… {n} archivos");
                RelinkSearchResult result = await relink.SearchAsync(
                    project, wanted, folder, scanned, MediaRelinkService.TakenPaths(pending.Values, proposed.Values), cancel.Token);
                foreach (RelinkMatch match in result.Found)
                {
                    Row row = rows.First(r => r.Media.Id == match.MediaId);
                    if (match.NeedsConfirmation)
                    {
                        proposed[match.MediaId] = match.Path;
                        row.Status.Text = $"¿Es este? Tiene otro nombre: {match.Path}. Coinciden la duración, el tamaño y las pistas, "
                            + "pero no se comparó el contenido: confírmalo o elige otro.";
                        continue;
                    }

                    pending[match.MediaId] = new MediaRelinkTarget(match.MediaId, match.Path);
                    row.Status.Text = $"Encontrado: {match.Path}";
                }

                foreach ((string id, IReadOnlyList<string> candidates) in result.Ambiguous)
                {
                    Row row = rows.First(r => r.Media.Id == id);
                    row.Status.Text = $"Hay {candidates.Count} archivos distintos que podrían ser este: elígelo con «Elegir…».";
                    row.StartFolder = Path.GetDirectoryName(candidates[0]);
                }

                searching = false;
                Refresh();
                int toConfirm = result.Found.Count(m => m.NeedsConfirmation);
                int left = missing.Count - pending.Count - proposed.Count;
                summary.Text = (left == 0 && proposed.Count == 0
                        ? "Se encontraron todos."
                        : $"Se encontraron {result.Found.Count}" + (toConfirm > 0 ? $" ({toConfirm} por confirmar)" : "") + (left > 0 ? $"; faltan {left}." : "."))
                    + (result.Truncated ? $" La carpeta tiene demasiados archivos: se miraron {result.FilesScanned}." : "");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                summary.Text = $"No se pudo buscar en la carpeta: {ex.Message}";
            }
            finally
            {
                if (searching)
                {
                    searching = false;
                    Refresh();
                }
            }
        }

        search.Click += (_, _) =>
        {
            string? folder = FolderDialog.Pick(hwnd, "Buscar los medios en esta carpeta", services.Settings.Current.LastMediaFolder);
            if (folder is not null)
            {
                services.Settings.Current.LastMediaFolder = folder;
                UiTask.Run(SearchAsync(folder));
            }
        };

        foreach (Row row in rows)
        {
            row.Pick.Click += (_, _) => UiTask.Run(PickAsync(row));
            row.Confirm.Click += (_, _) =>
            {
                if (proposed.Remove(row.Media.Id, out string? path))
                {
                    pending[row.Media.Id] = new MediaRelinkTarget(row.Media.Id, path);
                    row.Status.Text = $"Confirmado (con otro nombre): {path}";
                    Refresh();
                }
            };
        }

        async Task PickAsync(Row row)
        {
            string name = MediaRelinkSearch.FileName(row.Media.Path);
            string extension = Path.GetExtension(name);
            (string Name, string Spec)[] filters = extension.Length > 1
                ? [($"Archivos {extension}", "*" + extension), FileDialogs.MediaFilter, FileDialogs.AllFilter]
                : [FileDialogs.MediaFilter, FileDialogs.AllFilter];
            IReadOnlyList<string> picked = FileDialogs.Open(hwnd, $"Buscar «{name}»", multiple: false,
                FileDialogFolderPolicy.OpenFolder(services.Settings.Current), filters);
            FileDialogFolderPolicy.RememberOpen(services.Settings.Current, picked.Count > 0 ? picked[0] : null);
            if (picked.Count == 0)
            {
                return;
            }

            searching = true;
            Refresh();
            (MediaRelinkTarget? target, string? problem) = await relink.CheckFileAsync(
                project, row.Media, picked[0], MediaRelinkService.PendingForOthers(pending.Values, row.Media.Id));
            searching = false;
            if (target is null)
            {
                row.Status.Text = problem ?? "Ese archivo no sirve.";
                Refresh();
                return;
            }

            pending[row.Media.Id] = target;
            proposed.Remove(row.Media.Id);

            // Lo elegido a mano manda: si ese archivo estaba propuesto para otro medio, deja de estarlo.
            foreach (Row other in rows.Where(r => r != row && proposed.TryGetValue(r.Media.Id, out string? p)
                && string.Equals(p, target.Path, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                proposed.Remove(other.Media.Id);
                other.Status.Text = $"El archivo que se proponía se eligió para «{MediaRelinkSearch.FileName(row.Media.Path)}»: busca este de nuevo o elígelo.";
            }

            row.Status.Text = target.Probe is null
                ? $"Elegido: {target.Path}"
                : $"Elegido (distinto del original; se actualizan sus datos): {target.Path}";
            Refresh();

            // Lo habitual es que se haya movido la carpeta entera: se buscan ahí los demás.
            if (Path.GetDirectoryName(picked[0]) is { } folder)
            {
                services.Settings.Current.LastMediaFolder = folder;
                await SearchAsync(folder);
            }
        }

        Refresh();
        ContentDialogResult? answer = await DialogGate.ShowAsync(dialog);
        await cancel.CancelAsync();
        cancel.Dispose();
        if (!ReferenceEquals(project, session.Project))
        {
            return;
        }

        bool applied = answer == ContentDialogResult.Primary
            && MediaRelinkService.Apply(session, [.. missing.Where(m => pending.ContainsKey(m.Id)).Select(m => pending[m.Id])]);
        List<MediaRef> remaining = [.. missing.Where(m => !applied || !pending.ContainsKey(m.Id))];
        if (remaining.Count > 0)
        {
            NotifyRemaining(services, hwnd, relink, remaining);
        }
        else if (applied)
        {
            session.Notify(NotificationSeverity.Success, "Medios enlazados",
                pending.Count == 1 ? "Se volvió a enlazar el medio." : $"Se volvieron a enlazar {pending.Count} medios.");
        }
    }

    /// <summary>Un aviso con «Volver a enlazar», que vuelve a mirar qué falta (pudo cambiar) y abre el diálogo.</summary>
    private static void NotifyRemaining(EditorServices services, nint hwnd, MediaRelinkService relink, IReadOnlyList<MediaRef> missing)
    {
        EditorSession session = services.Session;
        session.Notify(NotificationSeverity.Warning, missing.Count == 1 ? "Falta un medio" : "Faltan medios", MediaRelinkService.Describe(missing),
            new NotificationAction("Volver a enlazar", () => UiTask.Run(ReopenAsync())));

        async Task ReopenAsync()
        {
            Project project = session.Project;
            IReadOnlyList<MediaRef> now = await relink.FindMissingAsync(project);
            if (!ReferenceEquals(project, session.Project))
            {
                return;
            }

            if (now.Count > 0)
            {
                await ShowAsync(services, hwnd, relink, now);
                return;
            }

            // Los archivos volvieron a su sitio fuera de Rodaje (la ruta no cambió, así que no hubo edición): el reproductor
            // pudo quedarse con ese medio como fallido; se recarga la secuencia en la misma posición para que lo reabra.
            bool reloaded = false;
            if (services.Player.LatestStats is { FailedMedia: > 0 })
            {
                Time position = session.Position;
                services.Player.Load(project.Clone(), session.SequenceId);
                services.Player.Seek(position);
                reloaded = true;
            }

            session.Notify(NotificationSeverity.Success, "Ya están todos los medios",
                reloaded ? "Los archivos están en su sitio; se recargó la vista previa." : "Los archivos están en su sitio; no hay nada que volver a enlazar.");
        }
    }
}
