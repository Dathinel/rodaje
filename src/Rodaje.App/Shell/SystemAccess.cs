using System.Diagnostics;
using Rodaje.App.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Rodaje.App.Shell;

/// <summary>Lo que sale de la app hacia el sistema (programas, enlaces, portapapeles); en el modo caos no hace nada.</summary>
internal static class SystemAccess
{
    /// <summary>El texto que el modo caos habría puesto en el portapapeles (el del usuario no se toca).</summary>
    public static DataPackage? FakeClipboard { get; private set; }

    public static Process? Start(ProcessStartInfo info) => AppDataPaths.Sandbox is null ? Process.Start(info) : null;

    public static void SetClipboard(DataPackage package)
    {
        if (AppDataPaths.Sandbox is null)
        {
            Clipboard.SetContent(package);
        }
        else
        {
            FakeClipboard = package;
        }
    }
}
