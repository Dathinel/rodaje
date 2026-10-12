using System.Buffers.Binary;
using Rodaje.Core.Timing;

namespace Rodaje.Analysis.Video;

/// <summary>
/// El análisis de movimiento de un tramo de un medio, tal como se guarda en la caché: un movimiento por cuadro desde
/// <see cref="FirstFrame"/>, más el tamaño de la imagen y la tasa. De esto sale la corrección para cualquier intensidad
/// sin volver a decodificar.
/// </summary>
public sealed class MotionTrack
{
    private const uint Magic = 0x544F4D52; // "RMOT"
    private const int Version = 1;
    private const int HeaderBytes = 4 + 4 + 4 + 4 + 8 + 8 + 8 + 4;
    private const int FrameBytes = (4 * 8) + 4 + 4;

    /// <summary>Tope de cuadros que se aceptan al leer (unas 10 h a 60 fps).</summary>
    public const int MaxFrames = 2_200_000;

    public MotionTrack(int width, int height, Rational frameRate, long firstFrame, IReadOnlyList<FrameMotion> motions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate.Num);
        ArgumentOutOfRangeException.ThrowIfNegative(firstFrame);
        ArgumentNullException.ThrowIfNull(motions);
        Width = width;
        Height = height;
        FrameRate = frameRate;
        FirstFrame = firstFrame;
        Motions = [.. motions];
    }

    /// <summary>Tamaño de la imagen mostrada (para la proporción).</summary>
    public int Width { get; }

    public int Height { get; }

    public Rational FrameRate { get; }

    /// <summary>Índice en el medio del primer cuadro analizado.</summary>
    public long FirstFrame { get; }

    public IReadOnlyList<FrameMotion> Motions { get; }

    public double Aspect => (double)Width / Height;

    /// <summary>La corrección de cada cuadro analizado con estos ajustes.</summary>
    public StabilizationResult Stabilize(StabilizeOptions options) =>
        CameraPath.Stabilize([.. Motions.Select(m => m.Motion)], FrameRate.ToDouble(), Aspect, options);

    public byte[] ToBytes()
    {
        byte[] bytes = new byte[HeaderBytes + (Motions.Count * FrameBytes)];
        Span<byte> s = bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(s, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], Width);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], Height);
        BinaryPrimitives.WriteInt64LittleEndian(s[16..], FrameRate.Num);
        BinaryPrimitives.WriteInt64LittleEndian(s[24..], FrameRate.Den);
        BinaryPrimitives.WriteInt64LittleEndian(s[32..], FirstFrame);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], Motions.Count);
        int at = HeaderBytes;
        foreach (FrameMotion m in Motions)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(s[at..], m.Motion.A);
            BinaryPrimitives.WriteDoubleLittleEndian(s[(at + 8)..], m.Motion.B);
            BinaryPrimitives.WriteDoubleLittleEndian(s[(at + 16)..], m.Motion.X);
            BinaryPrimitives.WriteDoubleLittleEndian(s[(at + 24)..], m.Motion.Y);
            BinaryPrimitives.WriteInt32LittleEndian(s[(at + 32)..], m.Features);
            BinaryPrimitives.WriteInt32LittleEndian(s[(at + 36)..], m.Inliers);
            at += FrameBytes;
        }

        return bytes;
    }

    /// <exception cref="InvalidDataException">No es un análisis de movimiento de esta versión, o está cortado.</exception>
    public static MotionTrack FromBytes(ReadOnlySpan<byte> s)
    {
        if (s.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(s) != Magic || BinaryPrimitives.ReadInt32LittleEndian(s[4..]) != Version)
        {
            throw new InvalidDataException("No es un análisis de movimiento de Rodaje.");
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(s[8..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(s[12..]);
        long num = BinaryPrimitives.ReadInt64LittleEndian(s[16..]);
        long den = BinaryPrimitives.ReadInt64LittleEndian(s[24..]);
        long first = BinaryPrimitives.ReadInt64LittleEndian(s[32..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(s[40..]);
        if (width <= 0 || height <= 0 || num <= 0 || den <= 0 || first < 0 || count < 0 || count > MaxFrames
            || s.Length != HeaderBytes + ((long)count * FrameBytes))
        {
            throw new InvalidDataException("El análisis de movimiento está dañado o cortado.");
        }

        var motions = new FrameMotion[count];
        int at = HeaderBytes;
        for (int i = 0; i < count; i++)
        {
            var m = new Similarity(
                BinaryPrimitives.ReadDoubleLittleEndian(s[at..]),
                BinaryPrimitives.ReadDoubleLittleEndian(s[(at + 8)..]),
                BinaryPrimitives.ReadDoubleLittleEndian(s[(at + 16)..]),
                BinaryPrimitives.ReadDoubleLittleEndian(s[(at + 24)..]));
            motions[i] = new FrameMotion(m.IsFinite ? m : Similarity.Identity, BinaryPrimitives.ReadInt32LittleEndian(s[(at + 32)..]),
                BinaryPrimitives.ReadInt32LittleEndian(s[(at + 36)..]));
            at += FrameBytes;
        }

        return new MotionTrack(width, height, new Rational(num, den), first, motions);
    }
}
