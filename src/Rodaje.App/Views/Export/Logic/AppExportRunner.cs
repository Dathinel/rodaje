using Rodaje.Engine.Timeline;
using Rodaje.Export;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
using Rodaje.Render.Export;

namespace Rodaje.App.Views.Export;

/// <summary>
/// La exportación de verdad para la interfaz: <see cref="ExportRunner"/> con el <c>ffmpeg.exe</c> LGPL verificado, la
/// sonda de codificadores, el registro de temporales y la vista previa que cede según el modo de rendimiento.
/// </summary>
/// <remarks>
/// El <c>ffmpeg.exe</c> se busca y se verifica la primera vez que hace falta (la sonda al abrir el diálogo), fuera del
/// hilo de la interfaz. Sin él, la sonda no da codificadores y cada exportación falla con
/// <see cref="ExportErrorCode.EncoderMissing"/> y el motivo.
/// </remarks>
public sealed partial class AppExportRunner : IExportRunner
{
    private readonly Lazy<(ExportRunner? Runner, string? Problem)> _setup;

    public AppExportRunner(IEffectContent? content, IExportPreview? preview, ExportJournal? journal = null)
    {
        _setup = new(() =>
        {
            FfmpegExecutable? ffmpeg = FfmpegExecutable.TryLocate(out string? reason);
            if (ffmpeg is null)
            {
                return (null, reason ?? "No se encontró el ffmpeg.exe LGPL.");
            }

            return (new ExportRunner(new ExportRunnerOptions
            {
                Ffmpeg = ffmpeg,
                Content = content,
                Preview = preview,
                Journal = journal ?? ExportJournal.Default,
                Encoders = new ProbedEncoderSource(ffmpeg, ExportEncoderCandidates.All),
            }), null);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsSimulation => false;

    public async Task<IReadOnlyList<EncoderSpec>> GetEncodersAsync(CancellationToken cancellationToken = default)
    {
        (ExportRunner? runner, _) = await Task.Run(() => _setup.Value, cancellationToken).ConfigureAwait(false);
        return runner?.Options.Encoders is { } source ? await source.GetAsync(cancellationToken).ConfigureAwait(false) : [];
    }

    public IExportJob Start(ExportRequest request, bool continueWithoutMissing = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with { ContinueWithoutMissing = request.ContinueWithoutMissing || continueWithoutMissing };
        (ExportRunner? runner, string? problem) = _setup.Value;
        return runner is null
            ? new FailedJob(request, new ExportException(ExportErrorCode.EncoderMissing, problem!))
            : new JobView(runner.Start(request));
    }

    /// <summary>Un <see cref="ExportJob"/> visto como <see cref="IExportJob"/>: la miniatura pasa a <see cref="BgraImage"/>.</summary>
    private sealed partial class JobView : IExportJob
    {
        private readonly ExportJob _job;
        private ExportThumbnail? _source;
        private BgraImage? _thumbnail;

        public JobView(ExportJob job)
        {
            _job = job;
            job.Changed += (_, e) => Changed?.Invoke(this, e);
        }

        public event EventHandler? Changed;

        public ExportRequest Request => _job.Request;

        public ExportProgress Progress => _job.Progress;

        public ExportException? Failure => _job.Failure;

        public BgraImage? Thumbnail
        {
            get
            {
                ExportThumbnail? latest = _job.Thumbnail;
                if (latest is not null && !ReferenceEquals(latest, _source))
                {
                    _thumbnail = new BgraImage(latest.Width, latest.Height, latest.Bgra);
                    _source = latest;
                }

                return _thumbnail;
            }
        }

        public Task Completion => _job.Completion;

        public void Cancel() => _job.Cancel();

        public void Dispose() => _job.Dispose();
    }

    /// <summary>Una exportación que no pudo ni empezar.</summary>
    private sealed partial class FailedJob(ExportRequest request, ExportException failure) : IExportJob
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public ExportRequest Request { get; } = request;

        public ExportProgress Progress { get; } = new(ExportJobState.Failed, 0, 0, 0, TimeSpan.Zero, null, 0);

        public ExportException? Failure { get; } = failure;

        public BgraImage? Thumbnail => null;

        public Task Completion => Task.CompletedTask;

        public void Cancel()
        {
        }

        public void Dispose()
        {
        }
    }
}
