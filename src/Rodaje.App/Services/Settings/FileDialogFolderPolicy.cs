namespace Rodaje.App.Services.Settings;

/// <summary>Carpetas iniciales y recordadas para los diálogos de archivos.</summary>
public static class FileDialogFolderPolicy
{
    public static string OpenFolder(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Existing(settings.LastOpenFolder) ?? DefaultSaveFolder(SaveDialogKind.Project);
    }

    public static string SaveFolder(AppSettings settings, SaveDialogKind kind, string? projectFolder = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Existing(projectFolder) is { } current)
        {
            return current;
        }

        string? remembered = kind switch
        {
            SaveDialogKind.Project => settings.LastProjectSaveFolder,
            SaveDialogKind.Export => settings.Export.Folder,
            SaveDialogKind.Still => settings.LastStillExportFolder,
            SaveDialogKind.ExportPreset => settings.LastExportPresetFolder,
            SaveDialogKind.Shortcuts => settings.LastShortcutExportFolder,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return Existing(remembered) ?? DefaultSaveFolder(kind);
    }

    public static void RememberOpen(AppSettings settings, string? selectedPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (selectedPath is not null && Path.GetDirectoryName(selectedPath) is { Length: > 0 } folder)
        {
            settings.LastOpenFolder = folder;
        }
    }

    public static void RememberSave(AppSettings settings, SaveDialogKind kind, string? selectedPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (selectedPath is null || Path.GetDirectoryName(selectedPath) is not { Length: > 0 } folder)
        {
            return;
        }

        switch (kind)
        {
            case SaveDialogKind.Project:
                settings.LastProjectSaveFolder = folder;
                break;
            case SaveDialogKind.Export:
                settings.Export.Folder = folder;
                break;
            case SaveDialogKind.Still:
                settings.LastStillExportFolder = folder;
                break;
            case SaveDialogKind.ExportPreset:
                settings.LastExportPresetFolder = folder;
                break;
            case SaveDialogKind.Shortcuts:
                settings.LastShortcutExportFolder = folder;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static string DefaultSaveFolder(SaveDialogKind kind)
    {
        Environment.SpecialFolder preferred = kind is SaveDialogKind.Export or SaveDialogKind.Still
            ? Environment.SpecialFolder.MyVideos
            : Environment.SpecialFolder.MyDocuments;
        string folder = Environment.GetFolderPath(preferred);
        if (Directory.Exists(folder))
        {
            return folder;
        }

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(documents) ? documents : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string? Existing(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? folder : null;
}

public enum SaveDialogKind
{
    Project,
    Export,
    Still,
    ExportPreset,
    Shortcuts,
}
