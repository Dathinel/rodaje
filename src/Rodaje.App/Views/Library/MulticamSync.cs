using Rodaje.Analysis;
using Rodaje.Analysis.Sync;
using Rodaje.Core.Timing;
using Rodaje.Media.Audio;

namespace Rodaje.App.Views.Library;

/// <summary>
/// Sincroniza ángulos de una multicámara por su sonido: decodifica cada audio una vez para la huella (<see cref="SyncFeature"/>),
/// busca el desfase grueso contra el primero y lo afina leyendo solo unos segundos de cada uno a 48 kHz. Va fuera del hilo
/// de la interfaz.
/// </summary>
internal static class MulticamSync
{
    private const int Rate = AudioDecoder.OutputSampleRate;
    private const int Channels = AudioDecoder.OutputChannels;

    /// <summary>El desfase hallado de un ángulo (dónde cae su 0 en el reloj del primero), o <c>null</c> si no se pudo.</summary>
    public sealed record AngleResult(string MediaId, Time? Offset, double Confidence);

    public static IReadOnlyList<AngleResult> Run(IReadOnlyList<(string MediaId, string Path, bool HasAudio)> angles, double maxLagSeconds = 600)
    {
        ArgumentNullException.ThrowIfNull(angles);
        var features = new Dictionary<string, SyncFeature>(StringComparer.Ordinal);
        foreach ((string id, string path, bool hasAudio) in angles)
        {
            if (hasAudio)
            {
                var feature = new SyncFeature(Rate, Channels);
                Decode(path, Time.Zero, null, feature);
                features[id] = feature;
            }
        }

        (string referenceId, string referencePath, _) = angles.FirstOrDefault(a => features.ContainsKey(a.MediaId));
        var results = new List<AngleResult>();
        foreach ((string id, string path, bool _) in angles)
        {
            if (id == referenceId)
            {
                results.Add(new AngleResult(id, Time.Zero, 1));
                continue;
            }

            if (referenceId is null || !features.TryGetValue(id, out SyncFeature? other)
                || WaveformSync.Coarse(features[referenceId], other, maxLagSeconds) is not { } coarse
                || !WaveformSync.RefineWindow(features[referenceId], other, coarse.Lag).HasValue
                || coarse.Confidence < WaveformSync.MinConfidence)
            {
                results.Add(new AngleResult(id, null, 0));
                continue;
            }

            (long start, long length) = WaveformSync.RefineWindow(features[referenceId], other, coarse.Lag)!.Value;
            int radius = WaveformSync.RefineBlocks * other.BlockLength;
            float[] otherSegment = Mono(path, start, length);
            long referenceStart = Math.Max(0, start + coarse.Lag - radius);
            float[] referenceSegment = Mono(referencePath, referenceStart, length + (2L * radius));
            (long lag, _) = WaveformSync.Refine(referenceSegment, referenceStart, otherSegment, start, coarse.Lag, radius);
            results.Add(new AngleResult(id, Time.FromSamples(lag, Rate), coarse.Confidence));
        }

        return results;
    }

    /// <summary>El audio mono de <paramref name="length"/> muestras desde <paramref name="start"/>.</summary>
    private static float[] Mono(string path, long start, long length)
    {
        var collector = new MonoCollector((int)length);
        Decode(path, Time.FromSamples(start, Rate), Time.FromSamples(start + length, Rate), collector);
        return collector.Samples;
    }

    private static void Decode(string path, Time from, Time? to, IAudioAnalyzer analyzer)
    {
        using AudioDecoder decoder = AudioDecoder.Open(path);
        decoder.Seek(from);
        long total = to is { } end ? (end - from).ToSamples(Rate) : long.MaxValue;
        var buffer = new float[8192 * Channels];
        long done = 0;
        while (done < total)
        {
            int wanted = (int)Math.Min(8192, total - done);
            int read = decoder.Read(buffer.AsSpan(0, wanted * Channels));
            if (read == 0)
            {
                break;
            }

            analyzer.Push(buffer.AsSpan(0, read * Channels));
            done += read;
        }
    }

    /// <summary>Junta el audio mezclado a mono de un tramo corto.</summary>
    private sealed class MonoCollector(int length) : IAudioAnalyzer
    {
        private int _filled;

        public float[] Samples { get; } = new float[length];

        public int SampleRate => Rate;

        public int Channels => MulticamSync.Channels;

        public void Push(ReadOnlySpan<float> interleaved)
        {
            for (int i = 0; i + 1 < interleaved.Length && _filled < Samples.Length; i += 2)
            {
                Samples[_filled++] = (interleaved[i] + interleaved[i + 1]) * 0.5f;
            }
        }
    }
}
