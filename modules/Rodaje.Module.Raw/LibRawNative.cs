using System.Reflection;
using System.Runtime.InteropServices;

namespace Rodaje.Module.Raw;

/// <summary>
/// La API en C de LibRaw 0.22 (<c>libraw.dll</c>, LGPL-2.1), cargada como DLL dinámica sin modificar: se puede cambiar por
/// otra compilación compatible, como pide la LGPL. Se busca junto al exe, en <c>RODAJE_ENTORNO\libraw\bin</c> y, al correr
/// desde el repositorio, en el primer <c>.entorno\libraw\bin</c> hacia arriba.
/// </summary>
internal static unsafe partial class LibRawNative
{
    public const string Library = "libraw";

    public const int Success = 0;
    public const int CancelledByCallback = -100010;

    /// <summary>Formato de <see cref="ProcessedImage.Type"/> para un mapa de bits (no un JPEG).</summary>
    public const int ImageBitmap = 2;

    // Desplazamientos de libraw_iparams_t y libraw_imgother_t (libraw_types.h de la 0.22).
    public const int IparamsMake = 4;
    public const int IparamsModel = 68;
    public const int IparamsDngVersion = 332;
    public const int IparamsColors = 340;
    public const int IparamsFilters = 344;
    public const int IparamsCdesc = 420;

    // libraw_data_t empieza por el puntero a image (ushort[4] por píxel) y sigue con libraw_image_sizes_t, cuyo flip
    // va tras ocho ushort, un unsigned y un double alineado a 8.
    public const int DataImage = 0;
    public const int DataSizesPixelAspect = 8 + 24;
    public const int DataSizesFlip = 8 + 32;
    public const int ImgotherIso = 0;
    public const int ImgotherShutter = 4;
    public const int ImgotherAperture = 8;
    public const int ImgotherFocal = 12;

    private static int _resolverSet;

    /// <summary>Instala la búsqueda de la DLL (una vez por proceso).</summary>
    public static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _resolverSet, 1) == 0)
        {
            NativeLibrary.SetDllImportResolver(typeof(LibRawNative).Assembly, Resolve);
        }
    }

    /// <summary>Dónde está <c>libraw.dll</c>, o <c>null</c> si no se encuentra.</summary>
    public static string? Locate()
    {
        foreach (string folder in Folders())
        {
            string candidate = Path.Combine(folder, Library + ".dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> Folders()
    {
        yield return AppContext.BaseDirectory;
        if (Environment.GetEnvironmentVariable("RODAJE_ENTORNO") is { Length: > 0 } entorno)
        {
            yield return Path.Combine(entorno, "libraw", "bin");
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".entorno", "libraw", "bin");
            if (Directory.Exists(candidate))
            {
                yield return candidate;
                yield break;
            }
        }
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library)
        {
            return 0;
        }

        return Locate() is { } path ? NativeLibrary.Load(path) : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessedImage
    {
        public int Type;
        public ushort Height;
        public ushort Width;
        public ushort Colors;
        public ushort Bits;
        public uint DataSize;
        public byte Data;
    }

    [LibraryImport(Library, EntryPoint = "libraw_init")]
    public static partial nint Init(uint flags);

    [LibraryImport(Library, EntryPoint = "libraw_close")]
    public static partial void Close(nint handle);

    [LibraryImport(Library, EntryPoint = "libraw_recycle")]
    public static partial void Recycle(nint handle);

    [LibraryImport(Library, EntryPoint = "libraw_open_buffer")]
    public static partial int OpenBuffer(nint handle, void* buffer, nuint size);

    [LibraryImport(Library, EntryPoint = "libraw_unpack")]
    public static partial int Unpack(nint handle);

    [LibraryImport(Library, EntryPoint = "libraw_adjust_sizes_info_only")]
    public static partial int AdjustSizesInfoOnly(nint handle);

    [LibraryImport(Library, EntryPoint = "libraw_dcraw_process")]
    public static partial int DcrawProcess(nint handle);

    [LibraryImport(Library, EntryPoint = "libraw_dcraw_make_mem_image")]
    public static partial ProcessedImage* MakeMemImage(nint handle, int* error);

    [LibraryImport(Library, EntryPoint = "libraw_dcraw_clear_mem")]
    public static partial void ClearMem(ProcessedImage* image);

    [LibraryImport(Library, EntryPoint = "libraw_strerror")]
    public static partial nint StrError(int error);

    [LibraryImport(Library, EntryPoint = "libraw_version")]
    public static partial nint Version();

    [LibraryImport(Library, EntryPoint = "libraw_set_demosaic")]
    public static partial void SetDemosaic(nint handle, int value);

    [LibraryImport(Library, EntryPoint = "libraw_set_output_color")]
    public static partial void SetOutputColor(nint handle, int value);

    [LibraryImport(Library, EntryPoint = "libraw_set_adjust_maximum_thr")]
    public static partial void SetAdjustMaximumThreshold(nint handle, float value);

    [LibraryImport(Library, EntryPoint = "libraw_set_user_mul")]
    public static partial void SetUserMultiplier(nint handle, int index, float value);

    [LibraryImport(Library, EntryPoint = "libraw_set_output_bps")]
    public static partial void SetOutputBits(nint handle, int value);

    [LibraryImport(Library, EntryPoint = "libraw_set_gamma")]
    public static partial void SetGamma(nint handle, int index, float value);

    [LibraryImport(Library, EntryPoint = "libraw_set_no_auto_bright")]
    public static partial void SetNoAutoBright(nint handle, int value);

    [LibraryImport(Library, EntryPoint = "libraw_set_bright")]
    public static partial void SetBright(nint handle, float value);

    [LibraryImport(Library, EntryPoint = "libraw_set_highlight")]
    public static partial void SetHighlight(nint handle, int value);

    [LibraryImport(Library, EntryPoint = "libraw_get_iwidth")]
    public static partial int GetImageWidth(nint handle);

    [LibraryImport(Library, EntryPoint = "libraw_get_iheight")]
    public static partial int GetImageHeight(nint handle);

    [LibraryImport(Library, EntryPoint = "libraw_get_cam_mul")]
    public static partial float GetCameraMultiplier(nint handle, int index);

    [LibraryImport(Library, EntryPoint = "libraw_get_pre_mul")]
    public static partial float GetDaylightMultiplier(nint handle, int index);

    [LibraryImport(Library, EntryPoint = "libraw_get_rgb_cam")]
    public static partial float GetRgbCam(nint handle, int row, int column);

    [LibraryImport(Library, EntryPoint = "libraw_get_color_maximum")]
    public static partial int GetColorMaximum(nint handle);

    [LibraryImport(Library, EntryPoint = "libraw_raw2image")]
    public static partial int Raw2Image(nint handle);

    [LibraryImport(Library, EntryPoint = "libraw_subtract_black")]
    public static partial void SubtractBlack(nint handle);

    [LibraryImport(Library, EntryPoint = "libraw_get_iparams")]
    public static partial byte* GetImageParams(nint handle);

    [LibraryImport(Library, EntryPoint = "libraw_get_imgother")]
    public static partial byte* GetImageOther(nint handle);

    [LibraryImport(Library, EntryPoint = "libraw_set_progress_handler")]
    public static partial void SetProgressHandler(nint handle, delegate* unmanaged[Cdecl]<void*, int, int, int, int> callback, void* data);

    public static string Message(int error) => Marshal.PtrToStringAnsi(StrError(error)) ?? $"error {error}";
}
