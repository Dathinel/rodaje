using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services.Collab;
using Rodaje.App.Services.Crashes;
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
    public static async Task<IReadOnlyDictionary<string, string>?> ShowAsync(EditorServices services, nint hwnd, ReceivedProject received, string hostName)
    {
        if (services.Shell.XamlRoot is not { } root)
        {
            return new Dictionary<string, string>();
        }

        List<MediaRef> projectMedia = received.Project.Media;
        Dictionary<string, MediaManifestEntry> known = received.Media.Where(e => projectMedia.Any(m => m.Id == e.MediaId)).ToDictionary(e => e.MediaId, StringComparer.Ordinal);
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        List<string> folders = [];
        var rows = new Dictionary<string, TextBlock>(StringComparer.Ordinal);

        var list = new StackPanel { Spacing = 6 };
        foreach (MediaRef media in projectMedia)
        {
            var name = new TextBlock { Text = MediaManifest.DisplayName(media.Name.Length > 0 ? media.Name : media.Id), TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
            var state = new TextBlock { Text = known.ContainsKey(media.Id) ? "Buscando…" : "No está en tu equipo", FontSize = ThemeResources.Size("BodyFontSize"), Foreground = ThemeResources.Brush("TextMutedBrush") };
            rows[media.Id] = state;
            var item = new StackPanel();
            item.Children.Add(name);
            item.Children.Add(state);
            list.Children.Add(item);
        }

        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressRing { IsActive = false, Width = 20, Height = 20 };
        var choose = new Button { Content = "Elegir carpetas donde buscar…" };
        var top = new StackPanel { Spacing = 12, MinWidth = 520 };
        top.Children.Add(InviteDialog.Hint("Para editar sin esperas, Rodaje usa los archivos de video y audio que tengas en tu equipo. Los cambios del proyecto se comparten, pero los archivos no viajan por internet."));
        top.Children.Add(new ScrollViewer { Content = list, MaxHeight = 280 });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(choose);
        actions.Children.Add(progress);
        top.Children.Add(actions);
        top.Children.Add(summary);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Busca tus archivos",
            Content = top,
            PrimaryButtonText = "Continuar al proyecto",
            DefaultButton = ContentDialogButton.Primary,
        };

        var cancel = new CancellationTokenSource();
        var matcher = new MediaMatch();
        bool searching = false;

        void Summarize()
        {
            int total = projectMedia.Count;
            int missing = total - found.Count;
            summary.Text = total == 0
                ? "Este proyecto no tiene medios."
                : $"Encontré {found.Count} de {total} medios."
                    + (missing > 0 ? $" Los {(missing == 1 ? "que falta queda" : "que faltan quedan")} sin conexión: se ven en la línea de tiempo pero no se pueden reproducir hasta tener el archivo. Pídele a {hostName} el original." : "");
        }

        async Task SearchAsync(IReadOnlyList<string> where)
        {
            List<MediaManifestEntry> wanted = [.. known.Values.Where(e => !found.ContainsKey(e.MediaId))];
            if (searching || wanted.Count == 0 || where.Count == 0)
            {
                return;
            }

            searching = true;
            progress.IsActive = true;
            choose.IsEnabled = false;
            foreach (MediaManifestEntry e in wanted)
            {
                rows[e.MediaId].Text = "Buscando…";
            }

            try
            {
                // El avance se crea aquí, en el hilo de la interfaz: dentro de Task.Run avisaría desde un hilo de trabajo.
                var scanned = new Progress<int>(n => summary.Text = $"Buscando… {n} archivos");
                MediaMatchReport report = await Task.Run(() => matcher.SearchAsync(wanted, where, scanned, cancel.Token));
                foreach ((string id, string path) in report.Found)
                {
                    found[id] = path;
                    rows[id].Text = "Encontrado";
                }

                foreach (MediaManifestEntry e in report.Missing)
                {
                    rows[e.MediaId].Text = "No está en tu equipo";
                }

                Summarize();
                if (report.CloudOnly > 0)
                {
                    summary.Text += $" {report.CloudOnly} archivos parecidos solo están en la nube (OneDrive): descárgalos y vuelve a buscar.";
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                searching = false;
                progress.IsActive = false;
                choose.IsEnabled = true;
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
