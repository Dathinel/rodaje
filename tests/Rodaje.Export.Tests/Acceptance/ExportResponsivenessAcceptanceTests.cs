using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Engine.Timeline;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using TerraFX.Interop.DirectX;
using Xunit.Abstractions;
using static Rodaje.Export.Tests.Acceptance.AcceptanceSupport;

namespace Rodaje.Export.Tests.Acceptance;

/// <summary>
/// La app usable mientras exporta, medida en el motor: la vista previa de la escena (3 capas 1080p, su propio
/// dispositivo) reproduce a 60 Hz y compone a resolución completa esperando a la GPU, con una edición cada 0,5 s (el
/// color de un PiP: <see cref="TimelineSession.Reload"/> → <c>Tick</c> → composición → GPU terminada), sin exportar y
/// exportando en cada modo. Criterio: ≤1 % de refrescos tarde, edición → pantalla mediana ≤20 ms y p95 ≤40 ms, ningún
/// bloqueo de más de 50 ms.
/// </summary>
/// <remarks>No incluye la espera del vsync ni la interfaz (eso lo mide <c>app.ps1 -Medir</c>); sí la competencia por la GPU, el decodificador y la CPU.</remarks>
[Collection(ExportAcceptanceGroup.Name)]
public sealed class ExportResponsivenessAcceptanceTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Run = TimeSpan.FromSeconds(25);

    [ExportAcceptanceFact]
    public async Task Preview_and_edits_stay_fluid_while_exporting()
    {
        AcceptanceScene preview = Scene(1);
        AcceptanceScene exported = Scene(10);
        var lines = new List<string>();
        var results = new Dictionary<string, Measure>();
        foreach (ExportPerformance? mode in new ExportPerformance?[] { null, ExportPerformance.Fastest, ExportPerformance.Background })
        {
            string name = mode?.ToString() ?? "sin exportar";
            string folder = Folder("fluidez-" + name.Replace(' ', '-'));
            ExportJob? job = null;
            if (mode is { } m)
            {
                var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = Ffmpeg, TemporaryDirectory = folder });
                job = runner.Start(new ExportRequest(exported.Project.Clone(), AcceptanceScene.SequenceId, Path.Combine(folder, "salida.mp4"), new ExportSettings
                {
                    Video = new VideoExportSettings { Codec = ExportCodec.H264, EncoderId = "h264_nvenc", RateControl = RateControl.Vbr(12_000, 18_000) },
                    Audio = new AudioExportSettings { Codec = ExportCodec.Aac },
                    Performance = m,
                }), await ProbedEncoders);
                var wait = Stopwatch.StartNew();
                while (job.Progress.State != ExportJobState.Encoding && wait.Elapsed < TimeSpan.FromSeconds(60) && !job.Completion.IsCompleted)
                {
                    await Task.Delay(20);
                }

                await Task.Delay(1000);
            }

            long framesBefore = job?.Progress.FramesDone ?? 0;
            Measure measure = PlayAndEdit(preview.Project);
            long exportedFrames = (job?.Progress.FramesDone ?? 0) - framesBefore;
            string state = job?.Progress.State.ToString() ?? "-";
            job?.Cancel();
            if (job is not null)
            {
                await job.Completion;
                job.Dispose();
            }

            Delete(folder);
            results[name] = measure;
            string line = string.Create(CultureInfo.InvariantCulture,
                $"{name}: {measure.Refreshes} refrescos, tarde {measure.Late} ({(double)measure.Late / measure.Refreshes:P2}); refresco (Tick + composición + GPU) mediana {measure.FrameMedian:0.0} ms, p99 {measure.FrameP99:0.0}, máx. {measure.FrameMax:0.0}; " +
                $"edición → pantalla ({measure.Edits.Count}) mediana {Percentile(measure.Edits, 0.5):0.0} ms, p95 {Percentile(measure.Edits, 0.95):0.0}, máx. {measure.Edits.Max():0.0}; bloqueo máx. {measure.WorstGap:0} ms; " +
                $"exportación {exportedFrames / Run.TotalSeconds:0} fps ({state}).");
            output.WriteLine(line);
            lines.Add(line);
        }

        foreach ((string name, Measure m) in results)
        {
            Assert.True((double)m.Late / m.Refreshes <= 0.01, $"{name}: {m.Late} refrescos tarde de {m.Refreshes}");
            Assert.True(Percentile(m.Edits, 0.5) <= 20 && Percentile(m.Edits, 0.95) <= 40, $"{name}: edición {Percentile(m.Edits, 0.5):0.0}/{Percentile(m.Edits, 0.95):0.0} ms");
            Assert.True(m.WorstGap <= 50, $"{name}: bloqueo de {m.WorstGap:0} ms");
        }
    }

    private sealed record Measure(long Refreshes, long Late, double FrameMedian, double FrameP99, double FrameMax, List<double> Edits, double WorstGap);

    private static Measure PlayAndEdit(Project original)
    {
        using GpuDevice gpu = GpuDevice.Create();
        Project project = original.Clone();
        using var session = TimelineSession.Open(project, AcceptanceScene.SequenceId, gpu, new TimelineSessionOptions { EnableAudio = false, UseProxies = ProxyUsage.Never });
        using var compositor = new Compositor(gpu, AcceptanceScene.Width, AcceptanceScene.Height);
        using var fence = new GpuFence(gpu);
        Func<LayerFrame, SourceGeometry> geometry = l => new SourceGeometry(l.SampleAspectRatio, l.Rotation);
        void Show()
        {
            using CompositionFrame frame = session.Tick();
            compositor.Render(frame, geometry);
            fence.Wait();
        }

        var settle = Stopwatch.StartNew();
        do
        {
            Show();
            Thread.Sleep(2);
        }
        while (session.IsSeekPending && settle.Elapsed < TimeSpan.FromSeconds(20));

        session.ResetStats();
        session.Play();
        var frames = new List<double>();
        var edits = new List<double>();
        double worstGap = 0;
        var clock = Stopwatch.StartNew();
        long refreshes = 0;
        TimeSpan nextEdit = TimeSpan.FromSeconds(0.5);
        TimeSpan previousEnd = TimeSpan.Zero;
        int edit = 0;
        while (clock.Elapsed < Run)
        {
            TimeSpan due = TimeSpan.FromTicks((long)((refreshes + 1) * (TimeSpan.TicksPerSecond / 60.0)));
            while (clock.Elapsed < due)
            {
                Thread.Sleep(1);
            }

            long start = Stopwatch.GetTimestamp();
            if (clock.Elapsed >= nextEdit)
            {
                // Lo que hace la app al mover un deslizador: una copia editada del proyecto y Reload.
                Project edited = project.Clone();
                Clip pip = edited.Sequences[0].VideoTracks[1].Clips[0];
                pip.Effects.Single(e => e.Type == BuiltInEffects.Color).Parameters[BuiltInEffects.ColorParameters.Saturation] = new Parameter { Value = (edit++ % 2 == 0) ? 0.1 : 0.4 };
                session.Reload(edited);
                project = edited;
                Show();
                edits.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                nextEdit += TimeSpan.FromSeconds(0.5);
            }
            else
            {
                Show();
                frames.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            TimeSpan end = clock.Elapsed;
            if (previousEnd > TimeSpan.Zero)
            {
                worstGap = Math.Max(worstGap, (end - previousEnd).TotalMilliseconds - (1000.0 / 60));
            }

            previousEnd = end;
            refreshes++;
        }

        TimelineStats stats = session.Stats;
        return new Measure(refreshes, stats.LateRefreshes, Percentile(frames, 0.5), Percentile(frames, 0.99), frames.Max(), edits, worstGap);
    }

    /// <summary>Espera a que la GPU termine lo encolado en el contexto (una consulta de evento).</summary>
    private sealed unsafe class GpuFence : IDisposable
    {
        private readonly GpuDevice _gpu;
        private ID3D11Query* _query;

        public GpuFence(GpuDevice gpu)
        {
            _gpu = gpu;
            var desc = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_EVENT };
            ID3D11Query* query;
            Assert.True(((ID3D11Device*)gpu.NativeDevice)->CreateQuery(&desc, &query) >= 0);
            _query = query;
        }

        public void Wait()
        {
            var context = (ID3D11DeviceContext*)_gpu.NativeContext;
            _gpu.LockContext();
            try
            {
                context->End((ID3D11Asynchronous*)_query);
                context->Flush();
            }
            finally
            {
                _gpu.UnlockContext();
            }

            int done = 0;
            while (true)
            {
                _gpu.LockContext();
                try
                {
                    if (context->GetData((ID3D11Asynchronous*)_query, &done, sizeof(int), 0) == 0 && done != 0)
                    {
                        return;
                    }
                }
                finally
                {
                    _gpu.UnlockContext();
                }

                Thread.SpinWait(50);
            }
        }

        public void Dispose()
        {
            if (_query != null)
            {
                _query->Release();
                _query = null;
            }
        }
    }
}
