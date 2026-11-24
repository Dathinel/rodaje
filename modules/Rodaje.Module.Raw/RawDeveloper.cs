using System.Runtime.InteropServices;
using System.Text;
using Rodaje.Modules.Protocol;

namespace Rodaje.Module.Raw;

/// <summary>Lo que se sabe de un cuadro RAW sin revelarlo.</summary>
/// <param name="Width">Ancho del cuadro revelado (ya girado si la cámara lo pide).</param>
/// <param name="CameraMultipliers">Balance de la toma (R, G, B), normalizado a G = 1; vacío si el archivo no lo trae.</param>
/// <param name="BayerPattern">El patrón de Bayer (<c>RGGB</c>…) si <see cref="RawDeveloper.Unpack"/> puede dar el mosaico; <c>null</c> si no (X-Trans, Foveon, DNG lineal, cuadros girados).</param>
internal sealed record RawInfo(int Width, int Height, string Make, string Model, float Iso, float Shutter, float Aperture, float FocalLength,
    bool IsDng, IReadOnlyList<float> CameraMultipliers, string? BayerPattern);

/// <summary>
/// Lo que acompaña a un mosaico de Bayer sin revelar (<see cref="RawDeveloper.Unpack"/>): el patrón del bloque de 2×2
/// (<c>RGGB</c>…), el blanco con el negro ya restado, los multiplicadores del balance pedido (R, G, B) y la matriz 3×3 por
/// filas de RGB de la cámara a RGB lineal Rec.709, tal como la usa LibRaw.
/// </summary>
internal sealed record MosaicInfo(int Width, int Height, string Pattern, int White, IReadOnlyList<float> Multipliers, IReadOnlyList<float> Matrix);

/// <summary>Un cuadro que LibRaw no pudo abrir o revelar, con su motivo.</summary>
internal sealed class RawException(string message) : Exception(message);

/// <summary>
/// Abre un archivo RAW desde memoria (el editor lo lee y lo deja en la región: el módulo no abre archivos del usuario) y
/// lo revela con LibRaw en luz lineal de 16 bits; la exposición y la curva van después, con una tabla. Cada llamada usa su
/// propia instancia de LibRaw, así que se pueden revelar varios cuadros a la vez en hilos distintos.
/// </summary>
internal static unsafe class RawDeveloper
{
    private const uint InitFlags = 1 << 1; // LIBRAW_OPTIONS_NO_DATAERR_CALLBACK: sin mensajes en la consola del módulo.
    private const int DemosaicLinear = 0;
    private const int DemosaicAhd = 3;

    /// <summary>Versión de la DLL cargada, o <c>null</c> si no está.</summary>
    public static string? Version
    {
        get
        {
            LibRawNative.EnsureResolver();
            try
            {
                return Marshal.PtrToStringAnsi(LibRawNative.Version());
            }
            catch (DllNotFoundException)
            {
                return null;
            }
        }
    }

    public static RawInfo Inspect(ReadOnlySpan<byte> file)
    {
        using var raw = new Instance();
        fixed (byte* data = file)
        {
            Check(LibRawNative.OpenBuffer(raw.Handle, data, (nuint)file.Length), "abrir");
            Check(LibRawNative.AdjustSizesInfoOnly(raw.Handle), "medir");
            byte* iparams = LibRawNative.GetImageParams(raw.Handle);
            byte* other = LibRawNative.GetImageOther(raw.Handle);
            return new RawInfo(
                LibRawNative.GetImageWidth(raw.Handle), LibRawNative.GetImageHeight(raw.Handle),
                Ascii(iparams + LibRawNative.IparamsMake, 64), Ascii(iparams + LibRawNative.IparamsModel, 64),
                *(float*)(other + LibRawNative.ImgotherIso), *(float*)(other + LibRawNative.ImgotherShutter),
                *(float*)(other + LibRawNative.ImgotherAperture), *(float*)(other + LibRawNative.ImgotherFocal),
                *(uint*)(iparams + LibRawNative.IparamsDngVersion) != 0, CameraMultipliers(raw.Handle),
                IsUpright(raw.Handle) ? BayerPattern(raw.Handle) : null);
        }
    }

    /// <summary>Sin giro ni píxeles anchos: el mosaico mide lo mismo que el cuadro revelado.</summary>
    private static bool IsUpright(nint handle)
    {
        double aspect = *(double*)((byte*)handle + LibRawNative.DataSizesPixelAspect);
        // Los umbrales de adjust_sizes_info_only: fuera de ellos LibRaw reescala el cuadro revelado.
        return *(int*)((byte*)handle + LibRawNative.DataSizesFlip) == 0 && (aspect == 0 || aspect is >= 0.995 and <= 1.005);
    }

    /// <summary>
    /// Lee el mosaico de Bayer de <paramref name="file"/> sin interpolar, con el negro restado, en <paramref name="output"/>
    /// (ancho × alto códigos de 16 bits, por filas), y devuelve lo que hace falta para revelarlo fuera (en la GPU del editor):
    /// el patrón, el blanco, el balance que pide <paramref name="settings"/> y la matriz de la cámara a Rec.709.
    /// </summary>
    /// <exception cref="RawException">LibRaw no pudo abrirlo o leerlo, no es un mosaico de Bayer RGB o el tamaño no coincide.</exception>
    /// <exception cref="OperationCanceledException">Cancelado a mitad.</exception>
    public static MosaicInfo Unpack(ReadOnlySpan<byte> file, DevelopSettings settings, Span<byte> output, int width, int height,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Primaries != OutputPrimaries.Rec709)
        {
            throw new RawException("El mosaico solo se revela fuera con primarios Rec.709.");
        }

        if (output.Length < (long)width * height * 2)
        {
            throw new ArgumentException("La salida es más chica que el mosaico.");
        }

        using var raw = new Instance();
        var cancel = new CancelFlag(cancellationToken);
        GCHandle pin = GCHandle.Alloc(cancel);
        try
        {
            string pattern;
            fixed (byte* data = file)
            {
                Check(LibRawNative.OpenBuffer(raw.Handle, data, (nuint)file.Length), "abrir", cancellationToken);
                pattern = BayerPattern(raw.Handle) ?? throw new RawException("El cuadro no es un mosaico de Bayer RGB: se revela con LibRaw.");
                if (!IsUpright(raw.Handle))
                {
                    throw new RawException("El cuadro viene girado o con píxeles anchos: se revela con LibRaw.");
                }

                LibRawNative.SetProgressHandler(raw.Handle, &OnProgress, (void*)GCHandle.ToIntPtr(pin));
                Check(LibRawNative.Unpack(raw.Handle), "leer", cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Check(LibRawNative.Raw2Image(raw.Handle), "copiar", cancellationToken);
            LibRawNative.SubtractBlack(raw.Handle);
            int actualWidth = LibRawNative.GetImageWidth(raw.Handle), actualHeight = LibRawNative.GetImageHeight(raw.Handle);
            if (actualWidth != width || actualHeight != height)
            {
                throw new RawException($"El mosaico mide {actualWidth}×{actualHeight} y la salida {width}×{height}.");
            }

            int white = LibRawNative.GetColorMaximum(raw.Handle);
            if (white is < 1 or > 65_535)
            {
                throw new RawException($"LibRaw no dio un blanco válido ({white}).");
            }

            float[] multipliers = settings.WhiteBalance switch
            {
                WhiteBalanceMode.Custom => [.. settings.Multipliers],
                WhiteBalanceMode.Daylight => DaylightMultipliers(raw.Handle),
                _ => CameraMultipliers(raw.Handle) is { Length: 3 } camera ? camera : DaylightMultipliers(raw.Handle),
            };
            var matrix = new float[9];
            bool any = false;
            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 3; column++)
                {
                    matrix[(row * 3) + column] = LibRawNative.GetRgbCam(raw.Handle, row, column);
                    any |= matrix[(row * 3) + column] != 0;
                }
            }

            if (!any)
            {
                // Sin matriz de color (raw_color), LibRaw deja el RGB de la cámara tal cual.
                matrix[0] = matrix[4] = matrix[8] = 1;
            }

            cancellationToken.ThrowIfCancellationRequested();
            uint filters = *(uint*)((byte*)LibRawNative.GetImageParams(raw.Handle) + LibRawNative.IparamsFilters);
            ushort* image = *(ushort**)((byte*)raw.Handle + LibRawNative.DataImage);
            if (image is null)
            {
                throw new RawException("LibRaw no dejó el mosaico en memoria.");
            }

            Span<ushort> target = MemoryMarshal.Cast<byte, ushort>(output[..(width * height * 2)]);
            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++)
                {
                    int i = (row * width) + column;
                    target[i] = image[(i * 4) + ColorIndex(filters, row, column)];
                }
            }

            return new MosaicInfo(width, height, pattern, white, multipliers, matrix);
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>El canal de LibRaw (índice en <c>cdesc</c>) del píxel (fila, columna): la macro <c>FC</c> de dcraw.</summary>
    private static int ColorIndex(uint filters, int row, int column) =>
        (int)((filters >> ((((row << 1) & 14) + (column & 1)) << 1)) & 3);

    /// <summary><c>RGGB</c>, <c>BGGR</c>… si el cuadro es un mosaico de Bayer de tres colores; <c>null</c> si no.</summary>
    private static string? BayerPattern(nint handle)
    {
        byte* iparams = LibRawNative.GetImageParams(handle);
        uint filters = *(uint*)(iparams + LibRawNative.IparamsFilters);
        int colors = *(int*)(iparams + LibRawNative.IparamsColors);
        return BayerPattern(filters, colors, new ReadOnlySpan<byte>(iparams + LibRawNative.IparamsCdesc, 4));
    }

    /// <summary>Valida el patrón CFA sin abrir LibRaw; los índices se traducen con <paramref name="colorDescription"/>.</summary>
    internal static string? BayerPattern(uint filters, int colors, ReadOnlySpan<byte> colorDescription)
    {
        // 0 es sin mosaico (DNG lineal, Foveon), 1 Leaf y 9 X-Trans: solo los patrones de 2×2 valen.
        if (filters <= 1000 || colors != 3 || colorDescription.Length < 4)
        {
            return null;
        }

        Span<char> letters = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            char letter = (char)colorDescription[ColorIndex(filters, i / 2, i % 2)];
            if (letter is not ('R' or 'G' or 'B'))
            {
                return null;
            }

            letters[i] = letter;
        }

        // FC codifica ocho filas: todas deben repetir el mismo bloque, también si usa dos índices para el verde.
        for (int row = 2; row < 8; row++)
        {
            for (int column = 0; column < 2; column++)
            {
                if (colorDescription[ColorIndex(filters, row, column)] != letters[((row & 1) * 2) + column])
                {
                    return null;
                }
            }
        }

        string pattern = new(letters);
        int greens = pattern.Count(c => c == 'G');
        bool diagonal = pattern[0] == 'G' ? pattern[3] == 'G' : pattern[1] == 'G' && pattern[2] == 'G';
        return greens == 2 && pattern.Contains('R', StringComparison.Ordinal) && pattern.Contains('B', StringComparison.Ordinal) && diagonal ? pattern : null;
    }

    private static float[] DaylightMultipliers(nint handle)
    {
        float r = LibRawNative.GetDaylightMultiplier(handle, 0);
        float g = LibRawNative.GetDaylightMultiplier(handle, 1);
        float b = LibRawNative.GetDaylightMultiplier(handle, 2);
        return r > 0 && g > 0 && b > 0 && float.IsFinite(r + g + b) ? [r / g, 1f, b / g] : [1f, 1f, 1f];
    }

    /// <summary>
    /// Revela <paramref name="file"/> en <paramref name="output"/> (<paramref name="layout"/> con el ancho y alto de
    /// <see cref="Inspect"/>). <paramref name="table"/> lleva la exposición, la curva y el formato de píxel.
    /// </summary>
    /// <exception cref="RawException">LibRaw no pudo abrirlo o revelarlo, o el tamaño no coincide.</exception>
    /// <exception cref="OperationCanceledException">Cancelado a mitad (LibRaw lo consulta en cada etapa).</exception>
    public static void Develop(ReadOnlySpan<byte> file, DevelopSettings settings, ToneTable table, Span<byte> output, VideoLayout layout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(layout);
        using var raw = new Instance();
        var cancel = new CancelFlag(cancellationToken);
        GCHandle pin = GCHandle.Alloc(cancel);
        LibRawNative.ProcessedImage* image = null;
        try
        {
            fixed (byte* data = file)
            {
                Check(LibRawNative.OpenBuffer(raw.Handle, data, (nuint)file.Length), "abrir", cancellationToken);
                Configure(raw.Handle, settings);
                LibRawNative.SetProgressHandler(raw.Handle, &OnProgress, (void*)GCHandle.ToIntPtr(pin));
                Check(LibRawNative.Unpack(raw.Handle), "leer", cancellationToken);
                Check(LibRawNative.DcrawProcess(raw.Handle), "revelar", cancellationToken);
            }

            // LibRaw solo mira la cancelación al pasar de etapa, y lo que sigue (copiar la imagen y escribir la salida) no
            // la mira: con la CPU llena, un cuadro grande tardaba en confirmarla todo eso más (tarea 221).
            cancellationToken.ThrowIfCancellationRequested();
            int error = 0;
            image = LibRawNative.MakeMemImage(raw.Handle, &error);
            Check(error, "copiar", cancellationToken);
            if (image is null || image->Type != LibRawNative.ImageBitmap || image->Colors != 3 || image->Bits != 16)
            {
                throw new RawException("LibRaw no devolvió una imagen RGB de 16 bits.");
            }

            if (image->Width != layout.Width || image->Height != layout.Height)
            {
                throw new RawException($"El cuadro revelado mide {image->Width}×{image->Height} y la salida {layout.Width}×{layout.Height}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var pixels = new ReadOnlySpan<ushort>(&image->Data, image->Width * image->Height * 3);
            table.Write(pixels, image->Width, image->Height, output, layout.Stride);
        }
        finally
        {
            if (image is not null)
            {
                LibRawNative.ClearMem(image);
            }

            pin.Free();
        }
    }

    private static void Configure(nint handle, DevelopSettings settings)
    {
        LibRawNative.SetOutputColor(handle, (int)settings.Primaries);
        LibRawNative.SetOutputBits(handle, 16);
        LibRawNative.SetGamma(handle, 0, 1f);
        LibRawNative.SetGamma(handle, 1, 1f);
        LibRawNative.SetNoAutoBright(handle, 1);
        LibRawNative.SetBright(handle, 1f);
        LibRawNative.SetHighlight(handle, 0);
        // Sin esto LibRaw toma como blanco el máximo que encuentra si es >75 % del nominal y una toma oscura cambia de brillo.
        LibRawNative.SetAdjustMaximumThreshold(handle, 0f);
        LibRawNative.SetDemosaic(handle, settings.HighQuality ? DemosaicAhd : DemosaicLinear);
        IReadOnlyList<float> multipliers = settings.WhiteBalance switch
        {
            WhiteBalanceMode.Camera => CameraMultipliers(handle),
            WhiteBalanceMode.Custom => settings.Multipliers,
            _ => [],
        };
        if (multipliers.Count == 3)
        {
            LibRawNative.SetUserMultiplier(handle, 0, multipliers[0]);
            LibRawNative.SetUserMultiplier(handle, 1, multipliers[1]);
            LibRawNative.SetUserMultiplier(handle, 2, multipliers[2]);
            LibRawNative.SetUserMultiplier(handle, 3, multipliers[1]);
        }
    }

    private static float[] CameraMultipliers(nint handle)
    {
        float r = LibRawNative.GetCameraMultiplier(handle, 0);
        float g = LibRawNative.GetCameraMultiplier(handle, 1);
        float b = LibRawNative.GetCameraMultiplier(handle, 2);
        return r > 0 && g > 0 && b > 0 && float.IsFinite(r + g + b) ? [r / g, 1f, b / g] : [];
    }

    private static void Check(int error, string what, CancellationToken cancellationToken = default)
    {
        if (error == LibRawNative.CancelledByCallback)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }

        if (error != LibRawNative.Success)
        {
            throw new RawException($"LibRaw no pudo {what} el cuadro: {LibRawNative.Message(error)}.");
        }
    }

    private static string Ascii(byte* text, int max)
    {
        int length = 0;
        while (length < max && text[length] != 0)
        {
            length++;
        }

        return Encoding.ASCII.GetString(text, length).Trim();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnProgress(void* data, int stage, int iteration, int expected) =>
        GCHandle.FromIntPtr((nint)data).Target is CancelFlag { Token.IsCancellationRequested: true } ? 1 : 0;

    private sealed class CancelFlag(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
    }

    private sealed class Instance : IDisposable
    {
        public Instance()
        {
            LibRawNative.EnsureResolver();
            try
            {
                Handle = LibRawNative.Init(InitFlags);
            }
            catch (DllNotFoundException)
            {
                throw new RawException("Falta libraw.dll (o el runtime de Visual C++ que necesita). Reinstala el módulo.");
            }

            if (Handle == 0)
            {
                throw new RawException("LibRaw no pudo reservar memoria.");
            }
        }

        public nint Handle { get; }

        public void Dispose() => LibRawNative.Close(Handle);
    }
}
