namespace Rodaje.TestMedia;

/// <summary>
/// Cuadros I420 deterministas: un fondo de degradado con ruido que se desplaza, un recuadro que
/// rebota y el <see cref="FrameCode"/> encima. El movimiento obliga al codificador a producir
/// cuadros P y B con vectores de movimiento reales, como un video de verdad.
/// </summary>
public sealed class SyntheticVideo
{
    private const int Period = 256;
    private const int LumaSpeed = 4;
    private const int BoxSize = 96;

    private readonly byte[] _lumaTile;
    private readonly byte[] _uTile;
    private readonly byte[] _vTile;
    private readonly int _tileWidth;
    private readonly int _chromaTileWidth;

    public SyntheticVideo(int width, int height)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"El tamaño debe ser par y positivo; se recibió {width}×{height}.");
        }

        FrameCode.GetLayout(width, height);
        Width = width;
        Height = height;

        // Fondo periódico en horizontal: cada cuadro es una copia de filas con un desplazamiento.
        _tileWidth = width + Period;
        _lumaTile = new byte[_tileWidth * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < _tileWidth; x++)
            {
                int u = x % Period;
                int ramp = u < Period / 2 ? u : Period - u;
                int noise = (int)(Hash((uint)(u >> 1), (uint)(y >> 1)) & 31);
                _lumaTile[y * _tileWidth + x] = (byte)(40 + ramp + y * 40 / height + noise);
            }
        }

        int cw = width / 2;
        int ch = height / 2;
        _chromaTileWidth = cw + Period / 2;
        _uTile = new byte[_chromaTileWidth * ch];
        _vTile = new byte[_chromaTileWidth * ch];
        for (int y = 0; y < ch; y++)
        {
            for (int x = 0; x < _chromaTileWidth; x++)
            {
                int u = x % (Period / 2);
                int ramp = u < Period / 4 ? u : Period / 2 - u;
                _uTile[y * _chromaTileWidth + x] = (byte)(96 + ramp);
                _vTile[y * _chromaTileWidth + x] = (byte)(160 - ramp / 2 - y * 32 / ch);
            }
        }
    }

    public int Width { get; }
    public int Height { get; }
    public int FrameSize => Width * Height * 3 / 2;

    public void Render(Span<byte> i420, int frameIndex)
    {
        if (i420.Length < FrameSize)
        {
            throw new ArgumentException($"El búfer mide {i420.Length} bytes y el cuadro necesita {FrameSize}.", nameof(i420));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);

        int shift = frameIndex * LumaSpeed % Period;
        Span<byte> luma = i420[..(Width * Height)];
        for (int y = 0; y < Height; y++)
        {
            _lumaTile.AsSpan(y * _tileWidth + shift, Width).CopyTo(luma.Slice(y * Width, Width));
        }

        int cw = Width / 2;
        int ch = Height / 2;
        int chromaShift = shift / 2;
        Span<byte> uPlane = i420.Slice(Width * Height, cw * ch);
        Span<byte> vPlane = i420.Slice(Width * Height + cw * ch, cw * ch);
        for (int y = 0; y < ch; y++)
        {
            _uTile.AsSpan(y * _chromaTileWidth + chromaShift, cw).CopyTo(uPlane.Slice(y * cw, cw));
            _vTile.AsSpan(y * _chromaTileWidth + chromaShift, cw).CopyTo(vPlane.Slice(y * cw, cw));
        }

        DrawBox(luma, uPlane, vPlane, frameIndex);
        FrameCode.DrawI420(i420, Width, Height, frameIndex);
    }

    private void DrawBox(Span<byte> luma, Span<byte> uPlane, Span<byte> vPlane, int frameIndex)
    {
        int size = Math.Min(BoxSize, Math.Min(Width, Height) / 4) & ~1;
        int travelX = Width - size;
        int travelY = Height - size;
        int bx = Bounce(frameIndex * 6, travelX) & ~1;
        int by = Bounce(frameIndex * 3, travelY) & ~1;
        for (int y = by; y < by + size; y++)
        {
            luma.Slice(y * Width + bx, size).Fill(220);
        }

        int cw = Width / 2;
        for (int y = by / 2; y < (by + size) / 2; y++)
        {
            uPlane.Slice(y * cw + bx / 2, size / 2).Fill(60);
            vPlane.Slice(y * cw + bx / 2, size / 2).Fill(200);
        }
    }

    private static int Bounce(int position, int travel)
    {
        if (travel <= 0)
        {
            return 0;
        }

        int p = position % (2 * travel);
        return p <= travel ? p : 2 * travel - p;
    }

    private static uint Hash(uint x, uint y)
    {
        uint h = x * 0x9E3779B1u ^ y * 0x85EBCA77u;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        return h;
    }
}
