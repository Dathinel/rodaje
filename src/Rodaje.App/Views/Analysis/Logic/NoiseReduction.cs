using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using Rodaje.App.Services;
using Rodaje.App.Services.Modules;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Audio;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Views.Analysis;

/// <summary>El audio limpio de un clip: un WAV estéreo f32 de 48 kHz que cubre <c>[Offset, Offset + Duration)</c> del medio original.</summary>
internal sealed record CleanAudio(string Path, string SourcePath, Time Offset, Time Duration, long Frames);

/// <summary>
/// «Reducir ruido» sin interfaz: elige el clip de audio, pide al módulo (<c>ruido</c> de modulo-audio-ia) la versión limpia
/// del tramo que usa el clip más un margen, la guarda como WAV en la carpeta de medios generados del proyecto y arma el paso
/// del historial que pasa el clip a ese medio. La intensidad mezcla el limpio con el original (como el límite de atenuación
/// de DeepFilterNet), así que moverla no vuelve a llamar al módulo.
/// </summary>
internal static class NoiseReduction
{
    public const string Function = "ruido";
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    /// <summary>Margen de origen a cada lado para poder recortar el clip después.</summary>
    public static readonly Time Handle = Time.FromSeconds(2);

    /// <summary>Lo más largo que se limpia de una vez (el WAV f32 estéreo de 1 h ocupa 1,4 GB).</summary>
    public static readonly Time MaxSpan = Time.FromSeconds(3600);

    /// <summary>Intensidad máxima (sin límite de atenuación).</summary>
    public const double MaxReductionDb = 48;

    /// <summary>El clip de audio que se limpia: el seleccionado o, si es un video, su audio enlazado en una pista de audio.</summary>
    public static Clip? TargetClip(Sequence sequence, IReadOnlyList<string> selection, Func<Clip, bool> hasAudio)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(selection);
        foreach (string id in selection)
        {
            if (sequence.FindClip(id) is not { } found || found.Clip.Generator is not null || !hasAudio(found.Clip))
            {
                continue;
            }

            if (found.Track.Kind == TrackKind.Audio)
            {
                return found.Clip;
            }

            if (found.Clip.LinkId is { } link && sequence.AudioTracks.SelectMany(t => t.Clips)
                .FirstOrDefault(c => c.LinkId == link && c.MediaId == found.Clip.MediaId) is { } linked)
            {
                return linked;
            }
        }

        return null;
    }

    /// <summary>Tramo del medio a limpiar: el del clip más <see cref="Handle"/> a cada lado, dentro del medio.</summary>
    public static (Time Start, Time End) Window(Clip clip, Time mediaDuration)
    {
        ArgumentNullException.ThrowIfNull(clip);
        Time start = Time.Max(Time.Zero, clip.SourceIn - Handle);
        Time end = mediaDuration > Time.Zero ? Time.Min(mediaDuration, clip.SourceOut + Handle) : clip.SourceOut + Handle;
        return (start, end);
    }

    /// <summary>
    /// Pide al módulo el audio limpio de <paramref name="window"/> y lo escribe en <paramref name="folder"/>. Es un trabajo de
    /// Trabajos (avance, cancelar); el archivo aparece entero o no aparece.
    /// </summary>
    public static Task<CleanAudio> RunAsync(ModuleManager modules, ModuleDescriptor module, string sourcePath, (Time Start, Time End) window, string folder,
        string title, CancellationToken cancellationToken, Action<ModuleJob>? started = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(module);
        return modules.RunAsync(module.Id, title, async call =>
        {
            started?.Invoke(call.Job);
            SharedBufferRef input = await ModuleMedia.WriteAudioAsync(call, sourcePath, window.Start, window.End, SampleRate, Channels, 0, 0.1).ConfigureAwait(false);
            SharedMemoryRegion outputRegion = call.CreateRegion(input.Length);
            SharedBufferRef output = outputRegion.Describe(0, input.Length, audio: input.Audio);
            using JsonDocument parameters = JsonDocument.Parse("""{"dispositivo":"cpu"}""");
            ModuleResult result = await call.InvokeAsync(Function, parameters.RootElement, [input, output], 0.1, 0.97).ConfigureAwait(false);
            if (result.Buffers.Count != 1 || result.Buffers[0].Length != input.Length)
            {
                throw new ModuleProtocolException("«ruido» devolvió un búfer inesperado.");
            }

            call.Report(0.97, "Guardando el audio limpio");
            string path = System.IO.Path.Combine(folder, $"ruido-{Guid.NewGuid():N}.wav");
            long frames = input.Audio!.Frames;
            await Task.Run(() => Wav.Write(path, Channels, SampleRate, frames, (offset, count) =>
                MemoryMarshal.Cast<byte, float>(call.Map(result.Buffers[0]).Slice((int)(offset * sizeof(float)), count * sizeof(float)))), call.CancellationToken).ConfigureAwait(false);
            return new CleanAudio(path, sourcePath, window.Start, window.End - window.Start, frames);
        }, cancellationToken);
    }

    /// <summary>
    /// El archivo que va al proyecto: el limpio tal cual a intensidad máxima o, si no, uno nuevo con
    /// <c>lim·original + (1 − lim)·limpio</c>, lim = 10^(−dB/20).
    /// </summary>
    public static string Final(CleanAudio clean, double reductionDb, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clean);
        if (reductionDb >= MaxReductionDb)
        {
            return clean.Path;
        }

        float lim = (float)Math.Pow(10, -reductionDb / 20);
        string path = System.IO.Path.ChangeExtension(clean.Path, null) + $"-{reductionDb:0}db.wav";
        using AudioDecoder decoder = AudioDecoder.Open(clean.SourcePath);
        decoder.Seek(clean.Offset);
        using FileStream cleanFile = File.OpenRead(clean.Path);
        cleanFile.Position = Wav.HeaderBytes;
        var original = new float[8192 * Channels];
        var processed = new float[8192 * Channels];
        var mixed = new float[8192 * Channels];
        Wav.Write(path, Channels, SampleRate, clean.Frames, (offset, count) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            int frames = count / Channels;
            int got = frames == 0 ? 0 : decoder.Read(original.AsSpan(0, count));
            original.AsSpan(got * Channels, count - (got * Channels)).Clear();
            cleanFile.ReadExactly(MemoryMarshal.AsBytes(processed.AsSpan(0, count)));
            for (int i = 0; i < count; i++)
            {
                mixed[i] = (lim * original[i]) + ((1 - lim) * processed[i]);
            }

            return mixed.AsSpan(0, count);
        }, blockSamples: original.Length);
        return path;
    }

    /// <summary>
    /// Un paso del historial: suma el medio limpio y pasa el clip a él (los puntos de entrada y salida y los keyframes de sus
    /// efectos, que van en tiempo de origen, se corren <paramref name="offset"/>). Un Ctrl+Z vuelve al medio original.
    /// </summary>
    public static ICommand Command(string sequenceId, string clipId, MediaRef media, Time offset)
    {
        ArgumentNullException.ThrowIfNull(media);
        return new CompositeCommand("Reducir ruido",
        [
            new AddMediaCommand(media),
            new SequenceEditCommand("Reducir ruido", sequenceId, ctx => Retarget(ctx, clipId, media, offset)),
        ]);
    }

    public static MediaRef Media(string id, CleanAudio clean, string name) => new()
    {
        Id = id,
        Path = clean.Path,
        Name = name,
        // El tramo entero aunque no caiga en una muestra (un MP3 de 12,042449 s): con la duración de las muestras, un clip
        // que llega al final del medio quedaba una fracción de muestra fuera del audio limpio y se rechazaba.
        Duration = clean.Frames >= clean.Duration.ToSamples(SampleRate) ? clean.Duration : Time.FromSamples(clean.Frames, SampleRate),
        HasAudio = true,
        SampleRate = SampleRate,
        AudioChannels = Channels,
    };

    private static void Retarget(EditContext ctx, string clipId, MediaRef media, Time offset)
    {
        (Track track, Clip clip) = ctx.Sequence.FindClip(clipId) ?? throw new EditException("El clip ya no está en la secuencia.");
        EditContext.EnsureUnlocked(track);
        if (clip.SourceIn < offset || clip.SourceOut - offset > media.Duration)
        {
            throw new EditException("El clip cambió y ya no cabe en el audio limpio. Vuelve a reducir el ruido.");
        }

        clip.MediaId = media.Id;
        clip.SourceIn -= offset;
        clip.SourceOut -= offset;
        foreach (Parameter parameter in clip.Effects.SelectMany(e => e.Parameters.Values))
        {
            for (int i = 0; i < parameter.Keyframes.Count; i++)
            {
                parameter.Keyframes[i] = parameter.Keyframes[i] with { Time = parameter.Keyframes[i].Time - offset };
            }
        }
    }

    /// <summary>WAV IEEE float de 32 bits (formato 3), cabecera de 44 bytes; se escribe a un temporal y se renombra.</summary>
    internal static class Wav
    {
        public const int HeaderBytes = 44;

        /// <param name="read">Devuelve <c>count</c> muestras intercaladas desde la muestra <c>offset</c>.</param>
        public static void Write(string path, int channels, int sampleRate, long frames, Func<long, int, ReadOnlySpan<float>> read, int blockSamples = 1 << 18)
        {
            long dataBytes = frames * channels * sizeof(float);
            if (dataBytes + HeaderBytes - 8 > uint.MaxValue)
            {
                throw new InvalidOperationException("El audio es demasiado largo para un WAV.");
            }

            string temp = path + ".part";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    Span<byte> header = stackalloc byte[HeaderBytes];
                    "RIFF"u8.CopyTo(header);
                    BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(dataBytes + HeaderBytes - 8));
                    "WAVEfmt "u8.CopyTo(header[8..]);
                    BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
                    BinaryPrimitives.WriteInt16LittleEndian(header[20..], 3);
                    BinaryPrimitives.WriteInt16LittleEndian(header[22..], (short)channels);
                    BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
                    BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * channels * sizeof(float));
                    BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)(channels * sizeof(float)));
                    BinaryPrimitives.WriteInt16LittleEndian(header[34..], 32);
                    "data"u8.CopyTo(header[36..]);
                    BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)dataBytes);
                    stream.Write(header);
                    long total = frames * channels;
                    blockSamples -= blockSamples % channels;
                    for (long at = 0; at < total; at += blockSamples)
                    {
                        int count = (int)Math.Min(blockSamples, total - at);
                        stream.Write(MemoryMarshal.AsBytes(read(at, count)));
                    }

                    stream.Flush(flushToDisk: true);
                }

                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                File.Delete(temp);
                throw;
            }
        }

        /// <summary>Muestras intercaladas de <paramref name="frames"/> cuadros desde el cuadro <paramref name="firstFrame"/>.</summary>
        public static float[] Read(string path, int channels, long firstFrame, int frames)
        {
            using FileStream stream = File.OpenRead(path);
            long available = Math.Max(0, ((stream.Length - HeaderBytes) / (channels * sizeof(float))) - firstFrame);
            var samples = new float[(int)Math.Min(frames, available) * channels];
            stream.Position = HeaderBytes + (firstFrame * channels * sizeof(float));
            stream.ReadExactly(MemoryMarshal.AsBytes(samples.AsSpan()));
            return samples;
        }
    }
}
