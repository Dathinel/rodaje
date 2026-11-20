using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Rodaje.Render;

/// <summary>
/// Con <c>RODAJE_HUELLAS_PIXELES</c> apuntando a un archivo, cada lectura de píxeles de la GPU añade allí su SHA-256: dos corridas de
/// las pruebas, antes y después de un cambio interno (el paso a <c>IGpu</c>), se comparan como conjuntos. Sin la variable no hace nada.
/// </summary>
internal static class PixelFingerprint
{
    public const string Variable = "RODAJE_HUELLAS_PIXELES";
    private static readonly string? s_path = Environment.GetEnvironmentVariable(Variable);
    private static readonly Lock s_gate = new();

    public static void Record(string what, ReadOnlySpan<byte> data)
    {
        if (s_path is null)
        {
            return;
        }

        string line = string.Create(CultureInfo.InvariantCulture, $"{what}\t{data.Length}\t{Convert.ToHexStringLower(SHA256.HashData(data))}\n");
        lock (s_gate)
        {
            File.AppendAllText(s_path, line);
        }
    }

    public static void Record<T>(string what, ReadOnlySpan<T> data)
        where T : unmanaged => Record(what, MemoryMarshal.AsBytes(data));
}
