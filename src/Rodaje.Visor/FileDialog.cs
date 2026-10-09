using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;
using static Rodaje.Visor.Win32;

namespace Rodaje.Visor;

/// <summary>Diálogo de abrir de Windows (IFileOpenDialog) por su vtable, sin COM generado en ejecución.</summary>
internal static unsafe class FileDialog
{
    private const uint FosForceFileSystem = 0x40;
    private const uint FosFileMustExist = 0x1000;
    private const uint FosPathMustExist = 0x800;

    /// <summary>Ruta elegida, o <c>null</c> si se canceló. Debe llamarse desde un hilo STA.</summary>
    public static string? PickVideo(HWND owner)
    {
        IFileOpenDialog* dialog = null;
        IShellItem* item = null;
        char* path = null;
        try
        {
            HRESULT hr = CoCreateInstance(__uuidof<FileOpenDialog>(), null, (uint)CLSCTX.CLSCTX_INPROC_SERVER,
                __uuidof<IFileOpenDialog>(), (void**)&dialog);
            if (hr.FAILED)
            {
                throw new InvalidOperationException($"No se pudo crear el diálogo de abrir (HRESULT 0x{hr.Value:X8}).");
            }

            uint options;
            dialog->GetOptions(&options);
            dialog->SetOptions(options | FosForceFileSystem | FosFileMustExist | FosPathMustExist);
            fixed (char* videoName = "Videos")
            fixed (char* videoSpec = "*.mp4;*.mkv;*.mov;*.m4v;*.webm;*.avi;*.mts;*.m2ts;*.ts;*.wmv;*.flv;*.3gp")
            fixed (char* allName = "Todos los archivos")
            fixed (char* allSpec = "*.*")
            fixed (char* title = "Abrir un video en el visor de Rodaje")
            {
                COMDLG_FILTERSPEC* filters = stackalloc COMDLG_FILTERSPEC[2];
                filters[0] = new COMDLG_FILTERSPEC { pszName = videoName, pszSpec = videoSpec };
                filters[1] = new COMDLG_FILTERSPEC { pszName = allName, pszSpec = allSpec };
                dialog->SetFileTypes(2, filters);
                dialog->SetTitle(title);
                hr = dialog->Show(owner);
            }

            if (hr == HRESULT_FROM_WIN32(ERROR_CANCELLED))
            {
                return null;
            }

            if (hr.FAILED || dialog->GetResult(&item).FAILED
                || item->GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &path).FAILED)
            {
                return null;
            }

            return new string(path);
        }
        finally
        {
            if (path != null)
            {
                CoTaskMemFree(path);
            }

            if (item != null)
            {
                item->Release();
            }

            if (dialog != null)
            {
                dialog->Release();
            }
        }
    }
}
