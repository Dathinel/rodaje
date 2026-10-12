using System.Numerics;

namespace Rodaje.Module.AudioAi.DeepFilter;

/// <summary>
/// La parte de señal de DeepFilterNet3 (equivalente a <c>libDF</c>, MIT/Apache-2.0): STFT de 960 puntos con salto de 480
/// y ventana de Vorbis, bandas ERB, rasgos normalizados que entran a la red y la síntesis con solapamiento. Uno por canal:
/// guarda la memoria de la ventana y las medias de normalización.
/// </summary>
internal sealed class DfState
{
    public const int SampleRate = 48_000;
    public const int FftSize = 960;
    public const int Hop = 480;
    public const int Bins = (FftSize / 2) + 1;
    public const int ErbBands = 32;
    public const int DfBins = 96;
    public const int DfOrder = 5;
    public const int DfLookahead = 2;
    public const int ConvLookahead = 2;
    private const int MinBinsPerBand = 2;

    /// <summary>exp(−salto/τ) con τ = 1 s, redondeado a tres decimales como en libDF.</summary>
    private const float Alpha = 0.99f;

    private static readonly float[] Window = BuildWindow();
    private static readonly float WindowNorm = 1f / (FftSize * FftSize / (2f * Hop));

    private readonly MixedRadixFft _fft = new(FftSize);
    private readonly Complex[] _time = new Complex[FftSize];
    private readonly Complex[] _freq = new Complex[FftSize];
    private readonly float[] _analysisMemory = new float[FftSize - Hop];
    private readonly float[] _synthesisMemory = new float[FftSize - Hop];
    private readonly float[] _meanNorm = Linspace(-60, -90, ErbBands);
    private readonly float[] _unitNorm = Linspace(0.001f, 0.0001f, DfBins);

    /// <summary>Ancho en bins de cada banda ERB (suman <see cref="Bins"/>).</summary>
    public static int[] ErbWidths { get; } = BuildErbWidths();

    /// <summary>
    /// Un salto de entrada a espectro (<paramref name="spectrum"/>: re e im intercalados, <see cref="Bins"/> pares) y rasgos:
    /// <paramref name="erbFeature"/> (<see cref="ErbBands"/>, dB con media móvil restada, /40) y <paramref name="dfFeature"/>
    /// (los primeros <see cref="DfBins"/> bins con norma unitaria móvil: re y luego im).
    /// </summary>
    public void Analyze(ReadOnlySpan<float> hop, Span<float> spectrum, Span<float> erbFeature, Span<float> dfFeature)
    {
        int memory = _analysisMemory.Length;
        for (int i = 0; i < memory; i++)
        {
            _time[i] = new Complex(_analysisMemory[i] * Window[i], 0);
        }

        for (int i = 0; i < Hop; i++)
        {
            _time[memory + i] = new Complex(hop[i] * Window[memory + i], 0);
        }

        hop.CopyTo(_analysisMemory);
        _fft.Forward(_time, _freq);
        for (int k = 0; k < Bins; k++)
        {
            spectrum[2 * k] = (float)(_freq[k].Real * WindowNorm);
            spectrum[(2 * k) + 1] = (float)(_freq[k].Imaginary * WindowNorm);
        }

        int bin = 0;
        for (int b = 0; b < ErbBands; b++)
        {
            int width = ErbWidths[b];
            float energy = 0;
            for (int j = 0; j < width; j++, bin++)
            {
                float re = spectrum[2 * bin], im = spectrum[(2 * bin) + 1];
                energy += ((re * re) + (im * im)) / width;
            }

            float db = 10f * MathF.Log10(energy + 1e-10f);
            _meanNorm[b] = (db * (1 - Alpha)) + (_meanNorm[b] * Alpha);
            erbFeature[b] = (db - _meanNorm[b]) / 40f;
        }

        for (int k = 0; k < DfBins; k++)
        {
            float re = spectrum[2 * k], im = spectrum[(2 * k) + 1];
            float norm = MathF.Sqrt((re * re) + (im * im));
            _unitNorm[k] = (norm * (1 - Alpha)) + (_unitNorm[k] * Alpha);
            float scale = 1f / MathF.Sqrt(_unitNorm[k]);
            dfFeature[k] = re * scale;
            dfFeature[DfBins + k] = im * scale;
        }
    }

    /// <summary>Espectro (re e im intercalados) a un salto de salida; la salida va <see cref="FftSize"/> − <see cref="Hop"/> muestras detrás de la entrada.</summary>
    public void Synthesize(ReadOnlySpan<float> spectrum, Span<float> hop)
    {
        // Inversa real con simetría hermítica; la parte imaginaria de DC y Nyquist se ignora, como en realfft.
        _time[0] = new Complex(spectrum[0], 0);
        for (int k = 1; k < Bins - 1; k++)
        {
            var x = new Complex(spectrum[2 * k], spectrum[(2 * k) + 1]);
            _time[k] = Complex.Conjugate(x);
            _time[FftSize - k] = x;
        }

        _time[Bins - 1] = new Complex(spectrum[2 * (Bins - 1)], 0);
        _fft.Forward(_time, _freq);

        // ifft(X) = conj(fft(conj(X))); la parte real no cambia con la conjugación final.
        int memory = _synthesisMemory.Length;
        for (int i = 0; i < Hop; i++)
        {
            hop[i] = ((float)_freq[i].Real * Window[i]) + _synthesisMemory[i];
        }

        for (int i = 0; i < memory; i++)
        {
            _synthesisMemory[i] = (float)_freq[Hop + i].Real * Window[Hop + i];
        }
    }

    private static float[] BuildWindow()
    {
        var window = new float[FftSize];
        int half = FftSize / 2;
        for (int n = 0; n < FftSize; n++)
        {
            double s = Math.Sin(0.5 * Math.PI * (n + 0.5) / half);
            window[n] = (float)Math.Sin(0.5 * Math.PI * s * s);
        }

        return window;
    }

    private static int[] BuildErbWidths()
    {
        static double FreqToErb(double hz) => 9.265 * Math.Log(1 + (hz / (24.7 * 9.265)));
        static double ErbToFreq(double erb) => 24.7 * 9.265 * (Math.Exp(erb / 9.265) - 1);

        double freqWidth = (double)SampleRate / FftSize;
        double low = FreqToErb(0);
        double high = FreqToErb(SampleRate / 2);
        double step = (high - low) / ErbBands;
        var widths = new int[ErbBands];
        int previous = 0, over = 0;
        for (int i = 1; i <= ErbBands; i++)
        {
            // libDF trabaja en f32: se redondea igual para no mover ningún borde de banda.
            float f = (float)ErbToFreq((float)(low + (i * (float)step)));
            int fb = (int)MathF.Round(f / (float)freqWidth, MidpointRounding.AwayFromZero);
            int count = fb - previous - over;
            if (count < MinBinsPerBand)
            {
                over = MinBinsPerBand - count;
                count = MinBinsPerBand;
            }
            else
            {
                over = 0;
            }

            widths[i - 1] = count;
            previous = fb;
        }

        widths[^1] += 1;
        int excess = widths.Sum() - Bins;
        if (excess > 0)
        {
            widths[^1] -= excess;
        }

        return widths.Sum() == Bins ? widths : throw new InvalidOperationException("Las bandas ERB no cubren el espectro.");
    }

    private static float[] Linspace(float from, float to, int count)
    {
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = from + ((to - from) * i / (count - 1));
        }

        return values;
    }
}
