using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Shell;
using Rodaje.App.Views.Relink;

namespace Rodaje.App.Views.Shared;

/// <summary>
/// «Se guarda en: …  Cambiar…  Junto al proyecto»: la carpeta de los medios generados (D-034) en los diálogos que crean
/// archivos y en Archivo › Carpeta de medios generados. Lo elegido se recuerda en los ajustes.
/// </summary>
internal sealed class GeneratedFolderRow
{
    private readonly EditorServices _services;
    private readonly TextBlock _path = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, IsTextSelectionEnabled = true };
    private readonly HyperlinkButton _reset = new() { Content = "Junto al proyecto", Padding = new Thickness(0) };

    public GeneratedFolderRow(EditorServices services)
    {
        _services = services;
        var change = new HyperlinkButton { Content = "Cambiar…", Padding = new Thickness(0) };
        change.Click += (_, _) => Pick();
        _reset.Click += (_, _) => Set(null);
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        links.Children.Add(change);
        links.Children.Add(_reset);
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(_path);
        panel.Children.Add(links);
        Element = panel;
        Refresh();
    }

    public UIElement Element { get; }

    /// <summary>La carpeta elegida ahora, creada si falta.</summary>
    public string Folder => GeneratedMedia.Ensure(_services.Session.FilePath, _services.Settings.Current.GeneratedMediaFolder);

    private void Pick()
    {
        XamlRoot? root = Element.XamlRoot;
        nint owner = root?.ContentIslandEnvironment is { } environment ? Win32Interop.GetWindowFromWindowId(environment.AppWindowId) : 0;
        string current = GeneratedMedia.Folder(_services.Session.FilePath, _services.Settings.Current.GeneratedMediaFolder);
        if (FolderDialog.Pick(owner, "Carpeta para los medios que genera Rodaje", Directory.Exists(current) ? current : null) is { } picked)
        {
            Set(picked);
        }
    }

    private void Set(string? folder)
    {
        _services.Settings.Current.GeneratedMediaFolder = folder;
        Refresh();
    }

    private void Refresh()
    {
        string? chosen = _services.Settings.Current.GeneratedMediaFolder;
        _path.Text = "Se guarda en: " + GeneratedMedia.Describe(_services.Session.FilePath, chosen);
        _reset.Visibility = GeneratedMedia.IsChosen(chosen) ? Visibility.Visible : Visibility.Collapsed;
    }
}
