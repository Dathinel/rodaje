using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Relink;
using Rodaje.App.Shell;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;

namespace Rodaje.App.Views.Relink;

/// <summary>
/// «Faltan medios»: al abrir un proyecto cuyos archivos se movieron o renombraron, la lista de lo que falta, «Buscar en
/// carpeta» (encuentra todos de una vez) y «Elegir…» para cada uno. Todo se aplica en un paso del historial; si se deja
/// para más tarde, un aviso permite volver a abrirlo. La lógica está en <see cref="MediaRelinkService"/>.
/// </summary>
internal static class RelinkDialog
{
    /// <summary>Muestra el diálogo cada vez que se abre un proyecto con medios que faltan.</summary>
    public static void Attach(EditorServices services, nint hwnd)
    {
        ArgumentNullException.ThrowIfNull(services);
        var relink = new MediaRelinkService(new FfmpegMediaProber());
        relink.Attach(services.Session, missing => ShowAsync(services, hwnd, relink, missing));
    }

    private sealed class Row(MediaRef media, TextBlock status, Button pick)
    {
        public MediaRef Media { get; } = media;

        public TextBlock Status { get; } = status;

        public Button Pick { get; } = pick;

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
        var rows = new List<Row>();
        var panel = new StackPanel { Spacing = 8, MinWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = missing.Count == 1
                ? "Este medio no está donde estaba. Búscalo en una carpeta o elige el archivo; sus clips, efectos y keyframes no cambian."
                : "Estos medios no están donde estaban. Búscalos en una carpeta o elige cada archivo; sus clips, efectos y keyframes no cambian.",
            TextWrapping = TextWrapping.Wrap,
        });

        var list = new StackPanel { Spacing = 6 };
        foreach (MediaRef media in missing)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            int clips = MediaRelinkEdits.CountClips(project, media.Id);
            text.Children.Add(new TextBlock { Text = MediaRelinkSearch.FileName(media.Path), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = media.Path, FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
            var status = new TextBlock { Text = clips == 1 ? "Falta · en 1 clip" : $"Falta · en {clips} clips", FontSize = 12, TextWrapping = TextWrapping.Wrap };
            text.Children.Add(status);
            var pick = new Button { Content = "Elegir…", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(pick, 1);
            grid.Children.Add(text);
            grid.Children.Add(pick);
            list.Children.Add(grid);
            rows.Add(new Row(media, status, pick));
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
            search.IsEnabled = !searching && pending.Count < missing.Count;
            progress.IsActive = searching;
            foreach (Row row in rows)
            {
                row.Pick.IsEnabled = !searching;
            }

            if (!searching)
            {
                summary.Text = pending.Count == 0 ? "" : $"{pending.Count} de {missing.Count} listos.";
            }
        }

        async Task SearchAsync(string folder)
        {
            List<MediaRef> wanted = [.. missing.Where(m => !pending.ContainsKey(m.Id))];
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
                RelinkSearchResult result = await relink.SearchAsync(project, wanted, folder, scanned, cancel.Token);
                foreach (RelinkMatch match in result.Found)
                {
                    pending[match.MediaId] = new MediaRelinkTarget(match.MediaId, match.Path);
                    Row row = rows.First(r => r.Media.Id == match.MediaId);
                    row.Status.Text = match.Kind == RelinkMatchKind.Renamed
                        ? $"Encontrado con otro nombre: {match.Path}"
                        : $"Encontrado: {match.Path}";
                }

                foreach ((string id, IReadOnlyList<string> candidates) in result.Ambiguous)
                {
                    Row row = rows.First(r => r.Media.Id == id);
                    row.Status.Text = $"Hay {candidates.Count} archivos distintos que podrían ser este: elígelo con «Elegir…».";
                    row.StartFolder = Path.GetDirectoryName(candidates[0]);
                }

                searching = false;
                Refresh();
                int left = missing.Count - pending.Count;
                summary.Text = (left == 0 ? "Se encontraron todos." : $"Se encontraron {result.Found.Count}; faltan {left}.")
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
                _ = SearchAsync(folder);
            }
        };

        foreach (Row row in rows)
        {
            row.Pick.Click += (_, _) => _ = PickAsync(row);
        }

        async Task PickAsync(Row row)
        {
            string name = MediaRelinkSearch.FileName(row.Media.Path);
            string extension = Path.GetExtension(name);
            (string Name, string Spec)[] filters = extension.Length > 1
                ? [($"Archivos {extension}", "*" + extension), FileDialogs.MediaFilter, FileDialogs.AllFilter]
                : [FileDialogs.MediaFilter, FileDialogs.AllFilter];
            IReadOnlyList<string> picked = FileDialogs.Open(hwnd, $"Buscar «{name}»", multiple: false,
                row.StartFolder ?? services.Settings.Current.LastMediaFolder, filters);
            if (picked.Count == 0)
            {
                return;
            }

            searching = true;
            Refresh();
            (MediaRelinkTarget? target, string? problem) = await relink.CheckFileAsync(project, row.Media, picked[0]);
            searching = false;
            if (target is null)
            {
                row.Status.Text = problem ?? "Ese archivo no sirve.";
                Refresh();
                return;
            }

            pending[row.Media.Id] = target;
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
            new NotificationAction("Volver a enlazar", () => _ = ReopenAsync()));

        async Task ReopenAsync()
        {
            IReadOnlyList<MediaRef> now = await relink.FindMissingAsync(session.Project);
            if (now.Count > 0)
            {
                await ShowAsync(services, hwnd, relink, now);
            }
        }
    }
}
