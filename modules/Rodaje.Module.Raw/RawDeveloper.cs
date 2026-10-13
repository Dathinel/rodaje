using System.Runtime.InteropServices;
using System.Text;
using Rodaje.Modules.Protocol;

namespace Rodaje.Module.Raw;

/// <summary>Lo que se sabe de un cuadro RAW sin revelarlo.</summary>
/// <param name="Width">Ancho del cuadro revelado (ya girado si la cámara lo pide).</param>
/// <param name="CameraMultipliers">Balance de la toma (R, G, B), normalizado a G = 1; vacío si el archivo no lo trae.</param>
internal sealed record RawInfo(int Width, int Height, string Make, string Model, float Iso, float Shutter, float Aperture, float FocalLength,
    bool IsDng, IReadOnlyList<float> CameraMultipliers);

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
                *(uint*)(iparams + LibRawNative.IparamsDngVersion) != 0, CameraMultipliers(raw.Handle));
        }
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
