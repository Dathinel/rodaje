using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;
using static Rodaje.Visor.Win32;

namespace Rodaje.Visor;

/// <summary>Diálogo de abrir de Windows (IFileOpenDialog) por su vtable, sin COM generado en ejecución.</summary>
internal static unsafe class FileDialog
{
    private const uint FosAllowMultiSelect = 0x200;
    private const uint FosForceFileSystem = 0x40;
    private const uint FosFileMustExist = 0x1000;
    private const uint FosPathMustExist = 0x800;

    /// <summary>Rutas elegidas (vacío si se canceló). Debe llamarse desde un hilo STA.</summary>
    public static IReadOnlyList<string> PickFiles(HWND owner)
    {
        IFileOpenDialog* dialog = null;
        IShellItemArray* items = null;
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
            dialog->SetOptions(options | FosAllowMultiSelect | FosForceFileSystem | FosFileMustExist | FosPathMustExist);
            fixed (char* videoName = "Videos y proyectos de Rodaje")
            fixed (char* videoSpec = "*.mp4;*.mkv;*.mov;*.m4v;*.webm;*.avi;*.mts;*.m2ts;*.ts;*.wmv;*.flv;*.3gp;*.rodaje")
            fixed (char* allName = "Todos los archivos")
            fixed (char* allSpec = "*.*")
            fixed (char* title = "Abrir videos (el primero de fondo, los demás encima) o un proyecto")
            {
                COMDLG_FILTERSPEC* filters = stackalloc COMDLG_FILTERSPEC[2];
                filters[0] = new COMDLG_FILTERSPEC { pszName = videoName, pszSpec = videoSpec };
                filters[1] = new COMDLG_FILTERSPEC { pszName = allName, pszSpec = allSpec };
                dialog->SetFileTypes(2, filters);
                dialog->SetTitle(title);
                hr = dialog->Show(owner);
            }

            if (hr.FAILED || dialog->GetResults(&items).FAILED)
            {
                return [];
            }

            uint count;
            if (items->GetCount(&count).FAILED)
            {
                return [];
            }

            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                IShellItem* item = null;
                char* path = null;
                try
                {
                    if (items->GetItemAt(i, &item).SUCCEEDED && item->GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &path).SUCCEEDED)
                    {
                        paths.Add(new string(path));
                    }
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
                }
            }

            return paths;
        }
        finally
        {
            if (items != null)
            {
                items->Release();
            }

            if (dialog != null)
            {
                dialog->Release();
            }
        }
    }
}
