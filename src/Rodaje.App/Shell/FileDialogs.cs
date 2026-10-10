using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.App.Shell;

/// <summary>
/// Diálogos de abrir y guardar de Windows (IFileOpenDialog e IFileSaveDialog) por su tabla virtual: funcionan sin
/// paquete, con Native AOT y aunque el proceso corra como administrador, cosa que los selectores de WinRT no aceptan.
/// </summary>
internal static unsafe class FileDialogs
{
    private const uint FosOverwritePrompt = 0x2;
    private const uint FosNoChangeDir = 0x8;
    private const uint FosForceFileSystem = 0x40;
    private const uint FosAllowMultiSelect = 0x200;
    private const uint FosPathMustExist = 0x800;
    private const uint FosFileMustExist = 0x1000;

    public static readonly (string Name, string Spec) ProjectFilter = ("Proyectos de Rodaje", "*.rodaje");

    public static readonly (string Name, string Spec) MediaFilter =
        ("Videos y audios", "*.mp4;*.mkv;*.mov;*.m4v;*.webm;*.avi;*.mts;*.m2ts;*.ts;*.wmv;*.flv;*.3gp;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus");

    public static readonly (string Name, string Spec) AllFilter = ("Todos los archivos", "*.*");

    /// <summary>Rutas elegidas, o vacío si se canceló. Desde el hilo de la interfaz (STA).</summary>
    public static IReadOnlyList<string> Open(nint owner, string title, bool multiple, string? folder, params (string Name, string Spec)[] filters)
    {
        IFileOpenDialog* dialog = null;
        IShellItemArray* items = null;
        try
        {
            ThrowIfFailed(CoCreateInstance(__uuidof<FileOpenDialog>(), null, (uint)CLSCTX.CLSCTX_INPROC_SERVER,
                __uuidof<IFileOpenDialog>(), (void**)&dialog), "crear el diálogo de abrir");
            uint options;
            dialog->GetOptions(&options);
            options |= FosForceFileSystem | FosFileMustExist | FosPathMustExist | FosNoChangeDir;
            dialog->SetOptions(multiple ? options | FosAllowMultiSelect : options);
            Configure((IFileDialog*)dialog, title, folder, filters);
            if (dialog->Show((HWND)owner).FAILED || dialog->GetResults(&items).FAILED)
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
                try
                {
                    if (items->GetItemAt(i, &item).SUCCEEDED && PathOf(item) is { } path)
                    {
                        paths.Add(path);
                    }
                }
                finally
                {
                    Release(item);
                }
            }

            return paths;
        }
        finally
        {
            Release(items);
            Release(dialog);
        }
    }

    /// <summary>Ruta donde guardar, o <c>null</c> si se canceló.</summary>
    public static string? Save(nint owner, string title, string suggestedName, string defaultExtension, string? folder, params (string Name, string Spec)[] filters)
    {
        IFileSaveDialog* dialog = null;
        IShellItem* result = null;
        try
        {
            ThrowIfFailed(CoCreateInstance(__uuidof<FileSaveDialog>(), null, (uint)CLSCTX.CLSCTX_INPROC_SERVER,
                __uuidof<IFileSaveDialog>(), (void**)&dialog), "crear el diálogo de guardar");
            uint options;
            dialog->GetOptions(&options);
            dialog->SetOptions(options | FosForceFileSystem | FosOverwritePrompt | FosPathMustExist | FosNoChangeDir);
            Configure((IFileDialog*)dialog, title, folder, filters);
            fixed (char* name = suggestedName)
            fixed (char* extension = defaultExtension)
            {
                dialog->SetFileName(name);
                dialog->SetDefaultExtension(extension);
            }

            if (dialog->Show((HWND)owner).FAILED || dialog->GetResult(&result).FAILED)
            {
                return null;
            }

            return PathOf(result);
        }
        finally
        {
            Release(result);
            Release(dialog);
        }
    }

    private static void Configure(IFileDialog* dialog, string title, string? folder, (string Name, string Spec)[] filters)
    {
        fixed (char* titleText = title)
        {
            dialog->SetTitle(titleText);
        }

        if (filters.Length > 0)
        {
            var pins = new List<System.Runtime.InteropServices.GCHandle>();
            try
            {
                COMDLG_FILTERSPEC* specs = stackalloc COMDLG_FILTERSPEC[filters.Length];
                for (int i = 0; i < filters.Length; i++)
                {
                    var name = System.Runtime.InteropServices.GCHandle.Alloc(filters[i].Name, System.Runtime.InteropServices.GCHandleType.Pinned);
                    var spec = System.Runtime.InteropServices.GCHandle.Alloc(filters[i].Spec, System.Runtime.InteropServices.GCHandleType.Pinned);
                    pins.Add(name);
                    pins.Add(spec);
                    specs[i] = new COMDLG_FILTERSPEC { pszName = (char*)name.AddrOfPinnedObject(), pszSpec = (char*)spec.AddrOfPinnedObject() };
                }

                dialog->SetFileTypes((uint)filters.Length, specs);
            }
            finally
            {
                foreach (System.Runtime.InteropServices.GCHandle pin in pins)
                {
                    pin.Free();
                }
            }
        }

        if (folder is not null && Directory.Exists(folder))
        {
            IShellItem* item = null;
            fixed (char* path = folder)
            {
                if (SHCreateItemFromParsingName(path, null, __uuidof<IShellItem>(), (void**)&item).SUCCEEDED)
                {
                    dialog->SetFolder(item);
                    Release(item);
                }
            }
        }
    }

    private static string? PathOf(IShellItem* item)
    {
        char* path = null;
        try
        {
            return item->GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &path).SUCCEEDED ? new string(path) : null;
        }
        finally
        {
            if (path != null)
            {
                CoTaskMemFree(path);
            }
        }
    }

    private static void Release<T>(T* pointer)
        where T : unmanaged, IUnknown.Interface
    {
        if (pointer != null)
        {
            pointer->Release();
        }
    }

    private static void ThrowIfFailed(HRESULT hr, string what)
    {
        if (hr.FAILED)
        {
            throw new InvalidOperationException($"No se pudo {what} (HRESULT 0x{hr.Value:X8}).");
        }
    }
}
