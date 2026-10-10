using System.Diagnostics;

namespace Rodaje.App.Services.Media;

public sealed partial class ClipVisuals
{
    private void EnqueueWaveform(string path)
    {
        lock (_gate)
        {
            if (_disposed || !_waveformQueued.Add(path))
            {
                return;
            }

            _waveformQueue.AddFirst(path);
            Monitor.PulseAll(_gate);
        }
    }

    private void WaveformLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            string path;
            lock (_gate)
            {
                while (_waveformQueue.Count == 0 && !_disposed)
                {
                    Monitor.Wait(_gate);
                }

                if (_disposed)
                {
                    return;
                }

                path = _waveformQueue.First!.Value;
                _waveformQueue.RemoveFirst();
            }

            try
            {
                ComputeWaveform(path);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Como con las miniaturas: el fallo se queda en ese archivo.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _waveforms.TryRemove(path, out _);
                _noWaveform[path] = ex.Message;
            }
            finally
            {
                lock (_gate)
                {
                    _waveformQueued.Remove(path);
                }

                MarkChanged(path, ClipVisualKind.Waveform);
            }
        }
    }

    private void ComputeWaveform(string path)
    {
        string fingerprint = FingerprintOf(path);
        string? cached = _waveformDirectory is null ? null : Path.Combine(_waveformDirectory, fingerprint + WaveformExtension);
        if (cached is not null && File.Exists(cached) && WaveformPeaks.Deserialize(File.ReadAllBytes(cached)) is { } stored)
        {
            Publish(path, stored);
            return;
        }

        using IAudioSampleReader? reader = _sources.OpenAudio(path);
        if (reader is null)
        {
            _noWaveform[path] = "sin audio";
            return;
        }

        var peaks = new WaveformPeaks(reader.SampleRate, reader.ExpectedSamples);
        Publish(path, peaks);
        float[] buffer = new float[16_384 * reader.Channels];
        long lastNotice = Stopwatch.GetTimestamp();
        int read;
        while ((read = reader.Read(buffer)) > 0)
        {
            _stop.Token.ThrowIfCancellationRequested();
            peaks.Append(buffer.AsSpan(0, read * reader.Channels), reader.Channels);
            if (Stopwatch.GetElapsedTime(lastNotice) > TimeSpan.FromMilliseconds(300))
            {
                lastNotice = Stopwatch.GetTimestamp();
                MarkChanged(path, ClipVisualKind.Waveform);
            }
        }

        peaks.Complete();
        if (cached is not null)
        {
            try
            {
                Directory.CreateDirectory(_waveformDirectory!);
                string temp = cached + ".tmp";
                File.WriteAllBytes(temp, peaks.Serialize());
                File.Move(temp, cached, overwrite: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void Publish(string path, WaveformPeaks peaks)
    {
        _waveforms[path] = peaks;
        _waveformOrder.Enqueue(path);
        while (_waveforms.Count > Math.Max(1, _options.MaxWaveformsInMemory) && _waveformOrder.TryDequeue(out string? old))
        {
            if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase))
            {
                _waveforms.TryRemove(old, out _);
            }
        }
    }
}
