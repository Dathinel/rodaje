using System.Buffers.Binary;

namespace Rodaje.Module.Raw;

/// <summary>
/// Las etiquetas de CinemaDNG que LibRaw no expone, leídas del primer IFD del TIFF: la cadencia (<c>FrameRate</c>,
/// 0xC764, SRATIONAL). Un archivo raro o cortado da <c>null</c>, nunca una excepción.
/// </summary>
internal static class DngTags
{
    public const ushort FrameRateTag = 0xC764;
    private const ushort SignedRational = 10;
    private const ushort Rational = 5;

    /// <summary>Cuadros por segundo como fracción, o <c>null</c> si el archivo no la trae o no es válida.</summary>
    public static (int Numerator, int Denominator)? FrameRate(ReadOnlySpan<byte> file)
    {
        if (file.Length < 8)
        {
            return null;
        }

        bool little = file[0] == (byte)'I' && file[1] == (byte)'I';
        if (!little && !(file[0] == (byte)'M' && file[1] == (byte)'M'))
        {
            return null;
        }

        if (U16(file, 2, little) != 42)
        {
            return null;
        }

        long ifd = U32(file, 4, little);
        if (ifd < 8 || ifd + 2 > file.Length)
        {
            return null;
        }

        int count = U16(file, (int)ifd, little);
        for (int i = 0; i < count; i++)
        {
            long entry = ifd + 2 + (i * 12L);
            if (entry + 12 > file.Length)
            {
                return null;
            }

            int e = (int)entry;
            if (U16(file, e, little) != FrameRateTag)
            {
                continue;
            }

            ushort type = U16(file, e + 2, little);
            long offset = U32(file, e + 8, little);
            if ((type != SignedRational && type != Rational) || U32(file, e + 4, little) < 1 || offset + 8 > file.Length)
            {
                return null;
            }

            long num = type == SignedRational ? (int)U32(file, (int)offset, little) : U32(file, (int)offset, little);
            long den = type == SignedRational ? (int)U32(file, (int)offset + 4, little) : U32(file, (int)offset + 4, little);
            return num is > 0 and <= 1_000_000 && den is > 0 and <= 1_000_000 && num / (double)den is >= 1 and <= 240 ? ((int)num, (int)den) : null;
        }

        return null;
    }

    private static ushort U16(ReadOnlySpan<byte> s, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(s[at..]) : BinaryPrimitives.ReadUInt16BigEndian(s[at..]);

    private static uint U32(ReadOnlySpan<byte> s, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(s[at..]) : BinaryPrimitives.ReadUInt32BigEndian(s[at..]);
}
