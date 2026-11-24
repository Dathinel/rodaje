using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.RenderCache;
using Rodaje.Export.Running;
using Rodaje.Export.Tests.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.Media.RenderCache;
using Rodaje.Playback.Composition;
using Rodaje.Render.Export;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Export.Tests.RenderCache;

[Collection(ExportGpuGroup.Name)]
// Los fragmentos de la caché de render se componen con el dispositivo D3D11 compartido.
[Trait("Categoria", "SoloWindows")]
public sealed class RenderCacheJobTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _folder = CreateFolder();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [FfmpegTheory]
    [InlineData(24, 1, SequenceColorSpace.Sdr709, 48)]
    [InlineData(30000, 1001, SequenceColorSpace.HdrPq, 8)]
    [InlineData(24000, 1001, SequenceColorSpace.HdrHlg, 8)]
    public async Task El_fragmento_tiene_los_mismos_pixeles_que_exportar_el_tramo_y_publica_sus_huellas(
        long num, long den, SequenceColorSpace colorSpace, int count)
    {
        // 640×360: en la CI, 48 cuadros de 1080p en WARP iban a 0,77 cuadros/s y vencía la espera.
        const int width = 640;
        const int height = 360;
        Project project = Scene(new Rational(num, den), colorSpace, width, height);
        const long first = 7;
        RenderCacheKey[] keys = Keys(project, first, count);
        var store = Store();
        bool checkedCurrent = false;
        using var devices = new ExportDevicePool(GpuDevice.CreateWarp);
        var job = new RenderCacheJob(project.Clone(), "sec", first, count, keys, store, Options() with { DevicePool = devices }, current =>
        {
            checkedCurrent = true;
            Assert.Equal(keys, current);
            Assert.Equal(0, store.Count);
            return true;
        });
        RenderCacheFragment fragment = Assert.IsType<RenderCacheFragment>(await job.RunAsync().WaitAsync(TimeSpan.FromMinutes(5)));
        Assert.True(checkedCurrent);
        Assert.Equal(keys, fragment.Metadata.Frames);
        Assert.Equal((width, height, project.Sequences[0].FrameRate, colorSpace, "prores_hq", false),
            (fragment.Metadata.Width, fragment.Metadata.Height, fragment.Metadata.FrameRate, fragment.Metadata.ColorSpace,
                fragment.Metadata.Codec, fragment.Metadata.PlaybackOnly));
        Assert.Equal(new FileInfo(fragment.MoviePath).Length, fragment.Metadata.MovieBytes);
        RenderCacheHit? previous = null;
        var reopened = new RenderCacheStore(store.Directory);
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.True(reopened.TryGet(keys[i], previous, out RenderCacheHit hit));
            Assert.Equal(fragment.MoviePath, hit.Fragment.MoviePath);
            Assert.Equal(i, hit.FrameIndex);
            previous = hit;
        }

        Assert.Empty(Directory.GetFiles(store.Directory, "*.tmp*"));
        Assert.Equal(ExportGpuPriority.Lowest, job.Export!.Profile!.GpuPriority);
        Assert.Equal(ProcessPriorityClass.BelowNormal, job.Export.Profile.EncoderPriority);
        Assert.False(job.Export.StreamCopy!.Copies);
        Assert.Equal("HQ", job.Export.Report!.Video!.Profile);
        Assert.Null(job.Export.Report.Audio);
        string reference = Path.Combine(_folder, "referencia.mov");
        var settings = ExportPresets.ProResMaster.Settings with { Audio = null, SmartRender = false };
        using ExportJob export = new ExportRunner(Options()).Start(new ExportRequest(project.Clone(), "sec", reference, settings,
            new ExportRange(Time.FromFrames(first, project.Sequences[0].FrameRate), Time.FromFrames(first + count, project.Sequences[0].FrameRate))),
            [ExportEncoderCatalog.Find("prores_ks")!]);
        await export.Completion.WaitAsync(TimeSpan.FromMinutes(5));
        Assert.True(export.Progress.State == ExportJobState.Completed, export.Failure?.ToString());
        await CompareFramesAsync(fragment.MoviePath, reference, count, width, height);
        output.WriteLine($"{count} cuadros decodificados y comparados a 10 bits 4:2:2: idénticos (PSNR infinito).");
    }

    [FfmpegFact]
    public async Task Si_las_huellas_cambiaron_no_publica_y_borra_el_temporal()
    {
        Project project = Scene();
        var store = Store();
        bool called = false;
        var job = new RenderCacheJob(project, "sec", 5, 6, Keys(project, 5, 6), store, Options() with { Verify = false }, current =>
        {
            called = true;
            Assert.Equal(6, current.Count);
            Assert.Single(Directory.GetFiles(store.Directory, "*.tmp.mov"));
            return false;
        });

        Assert.Null(await job.RunAsync().WaitAsync(TimeSpan.FromMinutes(2)));

        Assert.True(called);
        Assert.NotNull(job.Export!.Report);
        Assert.Equal(0, store.Count);
        Assert.Empty(Directory.GetFileSystemEntries(store.Directory));
    }

    [FfmpegTheory]
    [Trait("Categoria", "TiempoReal")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task La_pausa_detiene_la_entrega_en_cien_ms_y_reanudar_completa_el_fragmento(bool warp)
    {
        Project project = Scene();
        var store = Store();
        var job = new RenderCacheJob(project, "sec", 0, 48, Keys(project, 0, 48), store, Options(warp), _ => true);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Task<RenderCacheFragment?> running = job.RunAsync(cancel.Token);
        try
        {
            await WaitUntilAsync(() => job.Export is { FramesWritten: >= 2 }, running);
            long requested = Stopwatch.GetTimestamp();
            job.Pause.Pause();
            await WaitUntilAsync(() => job.Pause.IsWaiting, running);
            TimeSpan latency = Stopwatch.GetElapsedTime(requested, job.Pause.WaitingTimestamp);
            long stopped = job.Export!.FramesWritten;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Pausa 1920×1080 {(warp ? "WARP" : "GPU")}, con decodificación: {latency.TotalMilliseconds:0.000} ms; detenida tras {stopped} cuadros."));
            Assert.True(latency <= TimeSpan.FromMilliseconds(100), $"Pausa: {latency.TotalMilliseconds:0.000} ms.");
            await Task.Delay(250);
            Assert.Equal(stopped, job.Export.FramesWritten);
            Assert.False(running.IsCompleted);
            Assert.Equal(0, store.Count);
            job.Pause.Resume();
            Assert.NotNull(await running);
            Assert.Equal(48, job.Export.FramesWritten);
            Assert.Equal(ExportJobState.Completed, job.Export.Progress.State);
            Assert.Empty(Directory.GetFiles(store.Directory, "*.tmp*"));
        }
        finally
        {
            await StopAsync(cancel, running);
        }
    }

    [FfmpegTheory]
    [Trait("Categoria", "TiempoReal")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Una_pausa_larga_o_cancelada_borra_el_fragmento_incompleto(bool cancelWhilePaused)
    {
        Project project = Scene();
        var store = Store();
        var pause = new ExportPauseToken(cancelWhilePaused ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(200));
        var job = new RenderCacheJob(project, "sec", 0, 48, Keys(project, 0, 48), store, Options(), _ => true, pause);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Task<RenderCacheFragment?> running = job.RunAsync(cancel.Token);
        try
        {
            await WaitUntilAsync(() => job.Export is { FramesWritten: >= 2 }, running);
            pause.Pause();

            // Con el tope de 200 ms la pausa puede vencer antes de verla en espera en un runner lento: solo se espera a
            // verla cuando se cancela a mano.
            if (cancelWhilePaused)
            {
                await WaitUntilAsync(() => pause.IsWaiting, running);
                await cancel.CancelAsync();
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(ExportJobState.Canceled, job.Export!.Progress.State);
            Assert.Equal(0, store.Count);
            Assert.Empty(Directory.GetFileSystemEntries(store.Directory));
        }
        finally
        {
            await StopAsync(cancel, running);
        }
    }

    [FfmpegFact]
    public async Task Un_error_al_comprobar_vigencia_borra_el_temporal_y_no_se_repite_el_trabajo()
    {
        Project project = Scene();
        var store = Store();
        var job = new RenderCacheJob(project, "sec", 0, 3, Keys(project, 0, 3), store, Options(),
            _ => throw new InvalidOperationException("Revisión fallida."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunAsync());
        Assert.Empty(Directory.GetFileSystemEntries(store.Directory));
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunAsync());
    }

    [FfmpegFact]
    public void Rechaza_tramos_sin_todas_las_huellas_o_mayores_de_dos_segundos()
    {
        Project project = Scene(new Rational(30000, 1001));
        var store = Store();
        Assert.Throws<ArgumentException>(() => new RenderCacheJob(project, "sec", 0, 2, Keys(project, 0, 1), store, Options(), _ => true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderCacheJob(project, "sec", 0, 61, Keys(project, 0, 61), store, Options(), _ => true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderCacheJob(project, "sec", 119, 2, Keys(project, 0, 2), store, Options(), _ => true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExportPauseToken(TimeSpan.Zero));
        Assert.False(Directory.Exists(store.Directory));
    }

    [FfmpegTheory]
    [Trait("Categoria", "TiempoReal")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mide_tres_fragmentos_de_60_cuadros_1080p30_por_fase(bool warp)
    {
        Project project = Scene(Rational.Fps30);
        RenderCacheKey[] keys = Keys(project, 0, 60);
        using var devices = new ExportDevicePool(() =>
        {
            GpuDevice device = warp ? GpuDevice.CreateWarp() : GpuDevice.Create();
            Assert.Equal(warp, device.IsSoftware);
            output.WriteLine($"Dispositivo: {device.AdapterName}.");
            return device;
        });
        for (int trial = 0; trial < 3; trial++)
        {
            var phases = new Dictionary<string, double>(StringComparer.Ordinal);
            var options = Options(warp) with
            {
                DevicePool = devices,
                Measure = (phase, elapsed) => phases[phase] = phases.GetValueOrDefault(phase) + elapsed.TotalMilliseconds,
            };
            var job = new RenderCacheJob(project.Clone(), "sec", 0, 60, keys, Store(), options, _ => true);
            var clock = Stopwatch.StartNew();
            Assert.NotNull(await job.RunAsync().WaitAsync(TimeSpan.FromMinutes(2)));
            clock.Stop();
            phases["Total"] = clock.Elapsed.TotalMilliseconds;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"1080p30 {(warp ? "WARP" : "GPU")} intento {trial + 1}: {60 / clock.Elapsed.TotalSeconds:0.00} cuadros/s"));
            foreach ((string phase, double ms) in phases)
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{phase}: {ms:0.000} ms"));
            }
        }
    }

    [FfmpegTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dos_trabajos_reutilizan_el_dispositivo_incluso_tras_cancelar(bool cancelFirst)
    {
        Project project = Scene();
        int created = 0;
        GpuDevice? shared = null;
        using (var devices = new ExportDevicePool(() =>
        {
            created++;
            return shared = GpuDevice.CreateWarp();
        }))
        {
            ExportRunnerOptions options = Options() with { DevicePool = devices };
            var first = new RenderCacheJob(project.Clone(), "sec", 0, 6, Keys(project, 0, 6), Store(), options, _ => true);
            RenderCacheFragment? firstFragment = null;
            if (cancelFirst)
            {
                first.Pause.Pause();
                using var cancel = new CancellationTokenSource();
                Task<RenderCacheFragment?> running = first.RunAsync(cancel.Token);
                try
                {
                    await WaitUntilAsync(() => first.Pause.IsWaiting, running);
                    await cancel.CancelAsync();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
                }
                finally
                {
                    await StopAsync(cancel, running);
                }
            }
            else
            {
                firstFragment = Assert.IsType<RenderCacheFragment>(await first.RunAsync());
            }

            Assert.Equal(1, created);
            Assert.False(shared!.IsDisposed);
            var second = new RenderCacheJob(project.Clone(), "sec", 0, 6, Keys(project, 0, 6), Store(), options, _ => true);
            RenderCacheFragment secondFragment = Assert.IsType<RenderCacheFragment>(await second.RunAsync());
            Assert.Equal(1, created);
            Assert.False(shared.IsDisposed);
            if (firstFragment is not null)
            {
                await CompareFramesAsync(firstFragment.MoviePath, secondFragment.MoviePath, 6, 1920, 1080);
            }
        }

        Assert.True(shared!.IsDisposed);
    }

    [Fact]
    public void Cerrar_el_propietario_espera_al_ultimo_prestamo_y_rechaza_trabajos_nuevos()
    {
        using var devices = new ExportDevicePool(GpuDevice.CreateWarp);
        using ExportDevicePool.Lease first = devices.Acquire();
        using ExportDevicePool.Lease second = devices.Acquire();
        Assert.Same(first.Device, second.Device);
        devices.Dispose();
        Assert.False(first.Device.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => devices.Acquire());
        first.Dispose();
        Assert.False(second.Device.IsDisposed);
        second.Dispose();
        Assert.True(second.Device.IsDisposed);
        second.Dispose();
    }

    [FfmpegTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task La_verificacion_rechaza_un_MOV_con_datos_o_una_caja_final_truncados(bool trailingBox)
    {
        Project project = Scene();
        var job = new RenderCacheJob(project, "sec", 0, 3, Keys(project, 0, 3), Store(), Options(), _ => true);
        RenderCacheFragment fragment = Assert.IsType<RenderCacheFragment>(await job.RunAsync());
        string truncated = Path.Combine(_folder, "truncado.mov");
        File.Copy(fragment.MoviePath, truncated);
        using (var stream = new FileStream(truncated, FileMode.Open, FileAccess.Write))
        {
            if (trailingBox)
            {
                stream.Position = stream.Length;
                stream.Write([0, 0, 0, 16, (byte)'f', (byte)'r', (byte)'e', (byte)'e', 0, 0, 0, 0, 0, 0, 0, 0]);
            }

            stream.SetLength(stream.Length - 1);
        }

        var verifier = ExportVerifier.For(Options().Ffmpeg);
        if (trailingBox)
        {
            Assert.Empty(ExportVerifier.Check(verifier.Inspect(truncated), new ExportExpectation { FrameCount = 3, Width = 1920, Height = 1080 }));
        }

        ExportException error = Assert.Throws<ExportException>(() => verifier.InspectMovPackets(truncated));
        Assert.Equal(ExportErrorCode.EncoderFailed, error.Code);
    }

    [FfmpegFact]
    public async Task La_verificacion_de_paquetes_conserva_el_informe_y_detecta_menos_cuadros_u_otro_tamano()
    {
        Project project = Scene();
        var job = new RenderCacheJob(project, "sec", 0, 3, Keys(project, 0, 3), Store(), Options(), _ => true);
        RenderCacheFragment fragment = Assert.IsType<RenderCacheFragment>(await job.RunAsync());
        ExportReport report = ExportVerifier.For(Options().Ffmpeg).InspectMovPackets(fragment.MoviePath);
        ExportReport reference = ExportVerifier.For(Options().Ffmpeg).Inspect(fragment.MoviePath);
        Assert.Equal(reference.Video, report.Video);
        Assert.Equal(reference.MoovAtStart, report.MoovAtStart);
        Assert.Equal(job.Export!.Report!.Video, report.Video);
        Assert.Empty(ExportVerifier.Check(report, new ExportExpectation { FrameCount = 3, Width = 1920, Height = 1080 }));
        Assert.NotEmpty(ExportVerifier.Check(report, new ExportExpectation { FrameCount = 4 }));
        Assert.NotEmpty(ExportVerifier.Check(report, new ExportExpectation { Width = 1280 }));
        Assert.NotEmpty(ExportVerifier.Check(report, new ExportExpectation { Height = 720 }));
    }

    private ExportRunnerOptions Options(bool warp = true) => new()
    {
        Ffmpeg = FfmpegExecutable.TryLocate(out _)!,
        CreateDevice = warp ? GpuDevice.CreateWarp : GpuDevice.Create,
        TemporaryDirectory = _folder,
    };

    private RenderCacheStore Store() => new(Path.Combine(_folder, "almacen"));

    private static Project Scene(Rational? rate = null, SequenceColorSpace colorSpace = SequenceColorSpace.Sdr709,
        int width = 1920, int height = 1080)
    {
        Rational fps = rate ?? Rational.Fps24;
        GeneratedMedia generated = TestMediaGenerator.CreateDefault()!.GetOrCreate(new MediaSpec("cache-f3a", width, height, fps, TimeSpan.FromSeconds(4)));
        var media = new MediaRef { Id = "m", Path = generated.Path };
        MediaProbe.Probe(media.Path).ApplyTo(media);
        var clip = new Clip { Id = "c", MediaId = media.Id, SourceOut = media.Duration };
        var color = new EffectInstance { Id = "color", Type = BuiltInEffects.Color };
        color.Parameters[BuiltInEffects.ColorParameters.Saturation] = new Parameter { Value = 0.6 };
        clip.Effects.Add(color);
        var sequence = new Sequence
        {
            Id = "sec", Width = width, Height = height, FrameRate = fps, ColorSpace = colorSpace,
            VideoTracks = [new Track { Id = "v", Kind = TrackKind.Video, Clips = [clip] }],
        };
        return new Project { Id = "p", Sequences = [sequence], Media = [media] };
    }

    private static RenderCacheKey[] Keys(Project project, long first, int count)
    {
        RecipeBuilder recipes = RecipeBuilder.Create(project, "sec", new RecipeBuilderOptions
        {
            Media = path => new RecipeMedia(new RecipeSource(ContentFingerprint.Of(path)!, "yuv420p"), new Rational(1, 1), 0),
            Resources = _ => null,
            Fonts = _ => null,
            Chain = "prores_ks/hq",
        });
        return [.. Enumerable.Range(0, count).Select(i => recipes.KeyAt(first + i)!.Value)];
    }

    private async Task CompareFramesAsync(string actualPath, string expectedPath, int count, int width, int height)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        using Process actual = StartDecoder(actualPath);
        try
        {
            using Process expected = StartDecoder(expectedPath);
            try
            {
                Task<string> actualErrors = actual.StandardError.ReadToEndAsync(token);
                Task<string> expectedErrors = expected.StandardError.ReadToEndAsync(token);
                byte[] a = new byte[width * height * 4];
                byte[] b = new byte[a.Length];
                for (int frame = 0; frame < count; frame++)
                {
                    await actual.StandardOutput.BaseStream.ReadExactlyAsync(a, token);
                    await expected.StandardOutput.BaseStream.ReadExactlyAsync(b, token);
                    Assert.True(a.AsSpan().SequenceEqual(b), $"Píxeles distintos en el cuadro {frame}.");
                }

                byte[] extra = new byte[1];
                Assert.Equal(0, await actual.StandardOutput.BaseStream.ReadAsync(extra, token));
                Assert.Equal(0, await expected.StandardOutput.BaseStream.ReadAsync(extra, token));
                await actual.WaitForExitAsync(token);
                await expected.WaitForExitAsync(token);
                Assert.True(actual.ExitCode == 0, await actualErrors);
                Assert.True(expected.ExitCode == 0, await expectedErrors);
            }
            finally
            {
                await StopDecoderAsync(expected);
            }
        }
        finally
        {
            await StopDecoderAsync(actual);
        }
    }

    private Process StartDecoder(string path)
    {
        var info = new ProcessStartInfo(Options().Ffmpeg.Path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-v", "error", "-threads", "1", "-i", path, "-map", "0:v:0", "-threads", "1", "-pix_fmt", "yuv422p10le", "-f", "rawvideo", "pipe:1" })
        {
            info.ArgumentList.Add(argument);
        }

        Process process = Process.Start(info)!;
        process.PriorityClass = ProcessPriorityClass.BelowNormal;
        return process;
    }

    private static async Task StopDecoderAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Task running)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.False(running.IsCompleted, "El fragmento terminó antes del punto que se quería medir.");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "No se alcanzó el punto de medición.");
            await Task.Delay(1);
        }
    }

    private static async Task StopAsync(CancellationTokenSource cancel, Task running)
    {
        await cancel.CancelAsync();
        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string CreateFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rodaje-cache-f3a", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
