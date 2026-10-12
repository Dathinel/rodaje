using Rodaje.Analysis;
using Rodaje.App.Services;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Audio;

namespace Rodaje.App.Views.Analysis;

/// <summary>Decodifica el audio que usa un clip (estéreo, 48 kHz) y lo pasa a los analizadores, fuera del hilo de la interfaz.</summary>
internal static class ClipAudioSource
{
    public const int SampleRate = AudioDecoder.OutputSampleRate;
    public const int Channels = AudioDecoder.OutputChannels;

    /// <summary>El clip elegido para analizar: el primero seleccionado que tiene sonido.</summary>
    public static Clip? SelectedAudioClip(EditorSession session)
    {
        foreach (string id in session.SelectedClipIds)
        {
            if (session.Sequence.FindClip(id) is { } found && HasAudio(session, found.Clip))
            {
                return found.Clip;
            }
        }

        return null;
    }

    public static bool HasAudio(EditorSession session, Clip clip) =>
        clip.Generator is null && clip.Enabled && session.Project.FindMedia(clip.MediaId) is { HasAudio: true };

    public static Task AnalyzeAsync(
        EditorSession session,
        Clip clip,
        IAudioAnalyzer analyzer,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        MediaRef media = session.Project.FindMedia(clip.MediaId)
            ?? throw new InvalidOperationException("El clip usa un medio que ya no está en el proyecto.");
        string path = media.Path;
        (Time sourceIn, Time sourceOut) = ClipAudioMap.SourceWindow(clip);
        return Task.Run(() => Decode(path, sourceIn, sourceOut, analyzer, progress, cancellationToken), cancellationToken);
    }

    private static void Decode(string path, Time sourceIn, Time sourceOut, IAudioAnalyzer analyzer, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using AudioDecoder decoder = AudioDecoder.Open(path);
        decoder.Seek(sourceIn);
        long total = (sourceOut - sourceIn).ToSamples(SampleRate);
        var buffer = new float[8192 * Channels];
        long done = 0;
        int reported = -1;
        while (done < total)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int wanted = (int)Math.Min(8192, total - done);
            int read = decoder.Read(buffer.AsSpan(0, wanted * Channels));
            if (read == 0)
            {
                break;
            }

            analyzer.Push(buffer.AsSpan(0, read * Channels));
            done += read;
            int percent = (int)(done * 100 / total);
            if (percent != reported)
            {
                reported = percent;
                progress?.Report(done / (double)total);
            }
        }
    }
}
