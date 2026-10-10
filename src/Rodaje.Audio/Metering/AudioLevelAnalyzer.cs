namespace Rodaje.Audio.Metering;

/// <summary>Mide la mezcla estéreo en bloques fijos y los publica en un <see cref="AudioLevelRing"/> con su posición.</summary>
/// <remarks>
/// Lo usa un solo hilo, el que produce la mezcla, y no reserva memoria después de construirse. Los bloques de 128
/// cuadros (2,7 ms a 48 kHz) fijan la resolución en el tiempo del medidor; un salto de posición entre dos llamadas
/// cierra el bloque a medias y empieza otro, sin arrastrar la historia del pico real.
/// </remarks>
public sealed class AudioLevelAnalyzer
{
    public const int DefaultBlockFrames = 128;

    private const int Channels = 2;

    private readonly TruePeakDetector _trueLeft = new();
    private readonly TruePeakDetector _trueRight = new();
    private readonly int _blockFrames;

    private long _blockStart;
    private int _frames;
    private float _peakL, _peakR, _trueL, _trueR;
    private double _squaresL, _squaresR;
    private bool _clipped;

    public AudioLevelAnalyzer(AudioLevelRing ring, int blockFrames = DefaultBlockFrames)
    {
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockFrames, 16);
        Ring = ring;
        _blockFrames = blockFrames;
    }

    public AudioLevelRing Ring { get; }

    public int BlockFrames => _blockFrames;

    /// <summary>Muestra de la línea de tiempo que se espera en la próxima llamada a <see cref="Process"/>.</summary>
    public long Next => _blockStart + _frames;

    /// <summary>Mide la mezcla de un tramo y publica los bloques que se completan.</summary>
    /// <param name="mix">Lo que suena, estéreo intercalado (L, R, L, R…).</param>
    /// <param name="position">Muestra de la línea de tiempo del primer cuadro de <paramref name="mix"/>.</param>
    /// <param name="beforeLimiter">La misma mezcla antes del limitador, para saber si saturó; vacío si no hay limitador.</param>
    public void Process(ReadOnlySpan<float> mix, long position, ReadOnlySpan<float> beforeLimiter = default)
    {
        int total = mix.Length / Channels;
        if (!beforeLimiter.IsEmpty && beforeLimiter.Length < total * Channels)
        {
            throw new ArgumentException("La mezcla sin limitar es más corta que la mezcla.", nameof(beforeLimiter));
        }

        if (position != Next)
        {
            Restart(position);
        }

        int done = 0;
        while (done < total)
        {
            int n = Math.Min(total - done, _blockFrames - _frames);
            ReadOnlySpan<float> piece = mix.Slice(done * Channels, n * Channels);
            for (int f = 0; f < n; f++)
            {
                float l = piece[f * Channels];
                float r = piece[(f * Channels) + 1];
                _peakL = MathF.Max(_peakL, MathF.Abs(l));
                _peakR = MathF.Max(_peakR, MathF.Abs(r));
                _squaresL += l * l;
                _squaresR += r * r;
            }

            _trueL = MathF.Max(_trueL, _trueLeft.Process(piece, 0, Channels, n));
            _trueR = MathF.Max(_trueR, _trueRight.Process(piece, 1, Channels, n));
            if (!_clipped)
            {
                ReadOnlySpan<float> raw = beforeLimiter.IsEmpty ? piece : beforeLimiter.Slice(done * Channels, n * Channels);
                float limit = beforeLimiter.IsEmpty ? 1f : 1f + 1e-6f;
                foreach (float sample in raw)
                {
                    if (MathF.Abs(sample) >= limit)
                    {
                        _clipped = true;
                        break;
                    }
                }
            }

            _frames += n;
            done += n;
            if (_frames == _blockFrames)
            {
                Publish();
            }
        }
    }

    /// <summary>Publica el bloque a medias y empieza de nuevo en <paramref name="position"/> (un seek de la mezcla).</summary>
    public void Restart(long position)
    {
        Publish();
        _trueLeft.Reset();
        _trueRight.Reset();
        _blockStart = position;
    }

    private void Publish()
    {
        if (_frames > 0)
        {
            Ring.Write(new LevelBlock(_blockStart, _frames, _peakL, _peakR, _trueL, _trueR, _squaresL, _squaresR, _clipped));
        }

        _blockStart += _frames;
        _frames = 0;
        _peakL = _peakR = _trueL = _trueR = 0;
        _squaresL = _squaresR = 0;
        _clipped = false;
    }
}
