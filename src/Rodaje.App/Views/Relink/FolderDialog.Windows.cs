using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.App.Views.Relink;

/// <summary>
/// Elegir una carpeta con el diálogo de Windows (IFileOpenDialog con FOS_PICKFOLDERS), igual que
/// <c>Shell/FileDialogs</c>: sin paquete, con Native AOT y aunque el proceso corra como administrador.
/// </summary>
internal static unsafe class FolderDialog
{
    private const uint FosPickFolders = 0x20;
    private const uint FosForceFileSystem = 0x40;
    private const uint FosNoChangeDir = 0x8;
    private const uint FosPathMustExist = 0x800;

    /// <summary>La carpeta elegida, o <c>null</c> si se canceló. Desde el hilo de la interfaz (STA).</summary>
    public static string? Pick(nint owner, string title, string? folder)
    {
        IFileOpenDialog* dialog = null;
        IShellItem* result = null;
        try
        {
            if (CoCreateInstance(__uuidof<FileOpenDialog>(), null, (uint)CLSCTX.CLSCTX_INPROC_SERVER,
                __uuidof<IFileOpenDialog>(), (void**)&dialog).FAILED)
            {
                return null;
            }

            uint options;
            dialog->GetOptions(&options);
            dialog->SetOptions(options | FosPickFolders | FosForceFileSystem | FosNoChangeDir | FosPathMustExist);
            fixed (char* text = title)
            {
                dialog->SetTitle(text);
            }

            if (folder is not null && Directory.Exists(folder))
            {
                IShellItem* start = null;
                fixed (char* path = folder)
                {
                    if (SHCreateItemFromParsingName(path, null, __uuidof<IShellItem>(), (void**)&start).SUCCEEDED)
                    {
                        dialog->SetFolder(start);
                        start->Release();
                    }
                }
            }

            if (dialog->Show((HWND)owner).FAILED || dialog->GetResult(&result).FAILED)
            {
                return null;
            }

            char* chosen = null;
            try
            {
                return result->GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &chosen).SUCCEEDED ? new string(chosen) : null;
            }
            finally
            {
                if (chosen != null)
                {
                    CoTaskMemFree(chosen);
                }
            }
        }
        finally
        {
            if (result != null)
            {
                result->Release();
            }

            if (dialog != null)
            {
                dialog->Release();
            }
        }
    }
}
