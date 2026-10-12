using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Export;
using Rodaje.Media.Export;
using Rodaje.Media.Video;

namespace Rodaje.App.Views.Export;

/// <summary>
/// Doble de <see cref="IExportRunner"/> para la demo y las pruebas: recorre los estados de un trabajo real a un ritmo
/// fijo, dibuja una miniatura de muestra y no escribe nada. La app usa la orquestación real.
/// </summary>
public sealed partial class SimulatedExportRunner(IReadOnlyList<EncoderSpec>? encoders = null) : IExportRunner
{
    public bool IsSimulation => true;

    /// <summary>Cuadros que «codifica» por segundo de reloj.</summary>
    public double FramesPerSecond { get; init; } = 240;

    /// <summary>Cada cuánto avanza y avisa.</summary>
    public TimeSpan Step { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Si se da, falla con este error al llegar a la mitad.</summary>
    public ExportException? FailWith { get; init; }

    public Task<IReadOnlyList<EncoderSpec>> GetEncodersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(encoders ?? SimulatedEncoders.All);

    /// <summary>Cuántas veces se llamó a <see cref="Start"/> y con qué, para las pruebas.</summary>
    public List<(ExportRequest Request, bool ContinueWithoutMissing)> Started { get; } = [];

    public IExportJob Start(ExportRequest request, bool continueWithoutMissing = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        Started.Add((request, continueWithoutMissing));
        var job = new SimulatedJob(request, FramesPerSecond, Step, continueWithoutMissing ? null : FailWith);
        job.Run();
        return job;
    }

    private sealed partial class SimulatedJob(ExportRequest request, double fps, TimeSpan step, ExportException? failWith) : IExportJob
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly Lock _gate = new();
        private ExportProgress _progress = new(ExportJobState.Queued, 0, FrameCountOf(request), 0, TimeSpan.Zero, null, 0);
        private BgraImage? _thumbnail;
        private ExportException? _error;
        private Task _completion = Task.CompletedTask;

        public ExportRequest Request { get; } = request;

        public ExportProgress Progress
        {
            get
            {
                lock (_gate)
                {
                    return _progress;
                }
            }
        }

        public ExportException? Failure => Volatile.Read(ref _error);

        public BgraImage? Thumbnail => Volatile.Read(ref _thumbnail);

        public Task Completion => _completion;

        public event EventHandler? Changed;

        public void Cancel() => _cancel.Cancel();

        public void Dispose() => _cancel.Dispose();

        public void Run() => _completion = Task.Run(RunAsync);

        private async Task RunAsync()
        {
            var clock = Stopwatch.StartNew();
            long total = Progress.FrameCount;
            try
            {
                Publish(ExportJobState.Preparing, 0, 0, clock.Elapsed);
                await Task.Delay(step, _cancel.Token).ConfigureAwait(false);
                if (Request.Settings.Audio?.Loudness is not null)
                {
                    Publish(ExportJobState.MeasuringLoudness, 0, 0, clock.Elapsed);
                    await Task.Delay(step, _cancel.Token).ConfigureAwait(false);
                }

                if (Request.Settings.Audio is not null)
                {
                    Publish(ExportJobState.MixingAudio, 0, 0, clock.Elapsed);
                    await Task.Delay(step, _cancel.Token).ConfigureAwait(false);
                }

                long perStep = Math.Max(1, (long)(fps * step.TotalSeconds));
                for (long done = 0; done < total;)
                {
                    await Task.Delay(step, _cancel.Token).ConfigureAwait(false);
                    done = Math.Min(total, done + perStep);
                    if (failWith is not null && done * 2 >= total)
                    {
                        throw failWith;
                    }

                    if (Request.Settings.Video is not null)
                    {
                        Volatile.Write(ref _thumbnail, Sample(done, total));
                    }

                    Publish(ExportJobState.Encoding, done, fps, clock.Elapsed);
                }

                Publish(ExportJobState.Finalizing, total, fps, clock.Elapsed);
                await Task.Delay(step, _cancel.Token).ConfigureAwait(false);
                Publish(ExportJobState.Completed, total, fps, clock.Elapsed);
            }
            catch (OperationCanceledException)
            {
                Publish(ExportJobState.Canceled, Progress.FramesDone, 0, clock.Elapsed);
            }
            catch (ExportException ex)
            {
                Volatile.Write(ref _error, ex);
                Publish(ExportJobState.Failed, Progress.FramesDone, 0, clock.Elapsed);
            }
        }

        private void Publish(ExportJobState state, long done, double rate, TimeSpan elapsed)
        {
            lock (_gate)
            {
                long total = _progress.FrameCount;
                TimeSpan? remaining = rate > 0 && state == ExportJobState.Encoding ? TimeSpan.FromSeconds((total - done) / rate) : null;
                _progress = new(state, done, total, rate, elapsed, remaining, done * 40_000);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static long FrameCountOf(ExportRequest request)
        {
            Rodaje.Core.Model.Sequence sequence = request.Project.GetSequence(request.SequenceId);
            Rational rate = request.Settings.Video?.FrameRate ?? sequence.FrameRate;
            Time duration = request.Range?.Duration ?? sequence.Duration;
            return Math.Max(1, duration.ToFrames(rate));
        }

        /// <summary>Un degradado que se desplaza con el avance: basta para ver que la miniatura se refresca.</summary>
        private static BgraImage Sample(long done, long total)
        {
            const int Width = 160;
            const int Height = 90;
            var pixels = new byte[Width * Height * 4];
            int shift = (int)(done * 255 / Math.Max(1, total));
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int i = ((y * Width) + x) * 4;
                    pixels[i] = (byte)(x + shift);
                    pixels[i + 1] = (byte)(y * 2);
                    pixels[i + 2] = (byte)(255 - shift);
                    pixels[i + 3] = 255;
                }
            }

            return new BgraImage(Width, Height, pixels);
        }
    }
}
