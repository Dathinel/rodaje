using Rodaje.App.Shell;

namespace Rodaje.App.Views.Relink;

/// <summary>Elegir una carpeta en Linux con zenity o kdialog, como <see cref="FileDialogs"/>.</summary>
internal static class FolderDialog
{
    /// <summary>La carpeta elegida, o <c>null</c> si se canceló.</summary>
    public static string? Pick(nint owner, string title, string? folder)
    {
        _ = owner;
        string start = folder is not null && Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? path = FileDialogs.Run(["--file-selection", "--directory", "--title=" + title, "--filename=" + start + "/"],
            ["--title", title, "--getexistingdirectory", start])?.Trim();
        return string.IsNullOrEmpty(path) ? null : path;
    }
}
