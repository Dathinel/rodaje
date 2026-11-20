using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

/// <summary>DeleteTemporary corre en el <c>finally</c> del hilo de exportación: una carpeta sin acceso no puede escaparse de él.</summary>
public sealed class ExportOutputGuardDeleteTests
{
    [Fact]
    public void An_unlistable_folder_leaves_the_temporary_instead_of_throwing()
    {
        using var scratch = new ExportScratch();
        string folder = scratch.Combine("sin-acceso");
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, "salida" + ExportOutputGuard.TemporaryMarker + ".mp4");
        if (OperatingSystem.IsWindows())
        {
            DenyListing(folder, deny: true);
        }
        else
        {
            File.SetUnixFileMode(folder, UnixFileMode.None);
        }

        try
        {
            // Si el sistema deja listar igualmente (administrador, root), no hay nada que borrar y también vale.
            _ = ExportOutputGuard.DeleteTemporary(temporary);
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                DenyListing(folder, deny: false);
            }
            else
            {
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void DenyListing(string folder, bool deny)
    {
        var info = new DirectoryInfo(folder);
        DirectorySecurity security = info.GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        if (deny)
        {
            security.AddAccessRule(rule);
        }
        else
        {
            security.RemoveAccessRule(rule);
        }

        info.SetAccessControl(security);
    }
}
