using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text;
using Rodaje.App.Services.Packages;
using Rodaje.App.Tests.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.Packages;
using Rodaje.Packages.Download;
using Rodaje.TestServer;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Packages;

/// <summary>
/// Regresión de la v1.0.0: «Bajar modelos» de un módulo (547 MB) a la vez que se instalaba otro dejaba la ventana «No
/// responde». Cada bloque de 64 KB mandaba un aviso de progreso a la cola de la interfaz y la descarga del paquete corría
/// sus continuaciones (leer, hash, escribir) en el hilo de la interfaz. Aquí dos instalaciones simultáneas desde un
/// servidor local, con un hilo de interfaz de prueba que mide cuánto queda ocupado seguido y cuántos avisos recibe.
/// </summary>
/// <remarks>Corren solas (<see cref="ModuleProcessGroup"/>): miden tiempos del hilo de interfaz.</remarks>
[Collection(ModuleProcessGroup.Name)]
public sealed class ConcurrentDownloadTests(ITestOutputHelper output) : IAsyncLifetime, IDisposable
{
    private static readonly string[] EchoFiles =
        ["Rodaje.Module.Echo.exe", "Rodaje.Module.Echo.dll", "Rodaje.Module.Echo.runtimeconfig.json", "Rodaje.Module.Echo.deps.json", "Rodaje.Modules.Protocol.dll"];

    /// <summary>Lo que cuesta en la interfaz real pintar un avance (barra, texto y su diseño).</summary>
    private static readonly TimeSpan UiCostPerUpdate = TimeSpan.FromMilliseconds(2);

    private readonly TempDirectory _folder = new();
    private readonly TestHttpServer _server = TestHttpServer.Start();
    private readonly HttpClient _http = new();
    private readonly TestIndexSigner _signer = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Assert.Empty(_server.Faults);
    }

    public void Dispose()
    {
        _http.Dispose();
        _signer.Dispose();
        for (int i = 0; ; i++)
        {
            try
            {
                _folder.Dispose();
                return;
            }
            catch (Exception ex) when (i < 30 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private PackageLocations Locations => PackageLocations.Under(Path.Combine(_folder.Path, "complementos"));

    /// <summary>
    /// Lo que se mide sin reloj: en el hilo de la interfaz solo corren los avisos de progreso (limitados por
    /// <see cref="ThrottledProgress{T}"/>) y unas pocas continuaciones de la prueba. Con el defecto corrían ahí también las
    /// continuaciones de cada bloque de 64 KB (leer, hash, escribir): cientos de mensajes.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void Two_installs_at_once_never_hold_the_interface_thread(int chunkDelayMs) => TwoInstalls(chunkDelayMs, strictClock: false);

    /// <summary>
    /// La racha y la espera del hilo de la interfaz en 100 ms de reloj. En una vuelta lenta de la CI de Windows la racha
    /// llegó a 2,3 s con solo los avisos en la cola (el hilo no recibía CPU): es TiempoReal.
    /// </summary>
    [Theory]
    [Trait("Categoria", "TiempoReal")]
    [InlineData(0)]
    [InlineData(10)]
    public void Two_installs_at_once_keep_the_interface_thread_free_within_100_ms(int chunkDelayMs) => TwoInstalls(chunkDelayMs, strictClock: true);

    private void TwoInstalls(int chunkDelayMs, bool strictClock)
    {
        var delay = TimeSpan.FromMilliseconds(chunkDelayMs);
        byte[] model = TestPayloads.Deterministic(24 << 20, seed: 11);
        PackageService service = ServiceWith(model, packagePadding: 6 << 20, delay);
        var subtitles = new List<InstallProgress>();
        var audio = new List<InstallProgress>();
        var watch = new Stopwatch();

        MeasuredUiThread ui = MeasuredUiThread.Run(async () =>
        {
            await service.RefreshAsync();
            Assert.NotEmpty(service.MissingModelsOf("modulo-subtitulos"));
            watch.Start();
            Task models = service.EnsureModelsAsync("modulo-subtitulos", Ui(subtitles), CancellationToken.None);
            Task install = service.InstallFromCatalogAsync(service.Catalog.Find("modulo-audio-ia")!, Ui(audio), CancellationToken.None);
            await Task.WhenAll(models, install);
            watch.Stop();
        });

        double seconds = watch.Elapsed.TotalSeconds;
        output.WriteLine($"{chunkDelayMs} ms por bloque: {seconds:0.00} s; avisos subtítulos {subtitles.Count}, audio {audio.Count}; " +
            $"hilo de interfaz: {ui.Callbacks} mensajes, {ui.Busy.TotalMilliseconds:0} ms ocupado, racha más larga {ui.LongestStretch.TotalMilliseconds:0} ms, " +
            $"espera máxima de un mensaje {ui.LongestWait.TotalMilliseconds:0} ms");

        Assert.Empty(service.MissingModelsOf("modulo-subtitulos"));
        Assert.True(service.Installed.Find("modulo-audio-ia") is not null);
        int bound = (int)Math.Ceiling(seconds * 10) + 3;
        Assert.InRange(subtitles.Count, 1, bound);
        Assert.InRange(audio.Count, 1, bound);

        // Cada aviso es un mensaje; lo demás son las continuaciones de la prueba (8 en Linux).
        Assert.InRange(ui.Callbacks, 0, subtitles.Count + audio.Count + 20);
        if (strictClock)
        {
            Assert.InRange(ui.LongestStretch, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
            Assert.InRange(ui.LongestWait, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        }
    }

    [Fact]
    public void Cancelling_a_model_download_returns_at_once()
    {
        byte[] model = TestPayloads.Deterministic(24 << 20, seed: 12);
        PackageService service = ServiceWith(model, packagePadding: 0, TimeSpan.FromMilliseconds(20));
        TimeSpan cancelled = TimeSpan.Zero;

        MeasuredUiThread.Run(async () =>
        {
            await service.RefreshAsync();
            using var cancel = new CancellationTokenSource();
            Task models = service.EnsureModelsAsync("modulo-subtitulos", Ui([]), cancel.Token);
            await Task.Delay(400);
            var watch = Stopwatch.StartNew();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => models);
            cancelled = watch.Elapsed;
        });

        output.WriteLine($"Cancelar tardó {cancelled.TotalMilliseconds:0} ms.");
        Assert.InRange(cancelled, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    /// <summary>Como la página: un avance que se entrega en el hilo de la interfaz y cuesta <see cref="UiCostPerUpdate"/> pintarlo.</summary>
    private static ThrottledProgress<InstallProgress> Ui(List<InstallProgress> seen) => new(p =>
    {
        seen.Add(p);
        var spin = Stopwatch.StartNew();
        while (spin.Elapsed < UiCostPerUpdate)
        {
            Thread.SpinWait(50);
        }
    });

    /// <summary>
    /// «modulo-subtitulos» instalado y sin su modelo (<paramref name="model"/>), y «modulo-audio-ia» publicado en un índice
    /// firmado con un relleno de <paramref name="packagePadding"/> bytes; todo servido en bloques de 64 KB cada
    /// <paramref name="chunkDelay"/>.
    /// </summary>
    private PackageService ServiceWith(byte[] model, int packagePadding, TimeSpan chunkDelay)
    {
        _server.Map("modelos/subtitulos.bin", TestResponse.File(model) with { ChunkDelay = chunkDelay });
        string subtitles = WriteModule("modulo-subtitulos",
            $$"""{ "id": "whisper", "file": "modelos/whisper.bin", "size": {{model.Length}}, "sha256": "{{TestPayloads.Sha256Hex(model)}}", "license": "MIT", "url": "{{_server.UrlFor("modelos/subtitulos.bin")}}" }""",
            0);
        new PackageInstaller(Locations, PackageService.AppId).Install(subtitles);

        byte[] audio = File.ReadAllBytes(WriteModule("modulo-audio-ia", "", packagePadding));
        string sha = TestPayloads.Sha256Hex(audio);
        _server.Map("Dathinel/modulo-audio-ia/releases/download/v1.0.0/modulo-audio-ia.paquete", TestResponse.File(audio) with { ChunkDelay = chunkDelay });
        byte[] index = Encoding.UTF8.GetBytes($$"""
            { "formatVersion": 1, "generated": "2026-10-12T12:00:00Z", "entries": [
              { "id": "modulo-audio-ia", "version": "1.0.0", "kind": "module", "url": "{{_server.UrlFor("Dathinel/modulo-audio-ia/releases/download/v1.0.0/modulo-audio-ia.paquete")}}", "sha256": "{{sha}}", "size": {{audio.Length}} } ] }
            """);
        _server.Map("indice.json", TestResponse.File(index));
        _server.Map("indice.json.sig", TestResponse.File(_signer.SignFile(index)));

        var downloader = new GitHubReleaseDownloader(new GitHubReleaseOptions
        {
            IndexUrl = _server.UrlFor("indice.json"),
            PublicKey = _signer.PublicKeyBase64,
            DownloadFolder = Path.Combine(_folder.Path, "descargas"),
            AppVersion = PackageVersion.Parse("1.0.0"),
            Policy = DownloadUrlPolicy.WithLoopback("Dathinel"),
        }, _http);
        return new PackageService(Locations, PackageService.LoadBundledCatalog(), downloader,
            models: new ModelStore(Path.Combine(_folder.Path, "complementos", "modelos"), new FileDownloader(_http), DownloadUrlPolicy.WithLoopback("Dathinel")));
    }

    /// <summary>Un .paquete con el módulo eco bajo <paramref name="id"/>, sus modelos y un relleno sin comprimir.</summary>
    private string WriteModule(string id, string models, int padding)
    {
        string archive = Path.Combine(_folder.Path, $"{id}.paquete");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (string file in EchoFiles)
            {
                zip.CreateEntryFromFile(Path.Combine(AppContext.BaseDirectory, file), file);
            }

            if (padding > 0)
            {
                using Stream stream = zip.CreateEntry("relleno.bin", CompressionLevel.NoCompression).Open();
                stream.Write(TestPayloads.Deterministic(padding, seed: 13));
            }

            using var writer = new StreamWriter(zip.CreateEntry(PackageManifest.FileName).Open());
            writer.Write($$"""
                { "formatVersion": 1, "id": "{{id}}", "version": "1.0.0", "kind": "module", "name": "Eco como {{id}}", "author": "Pruebas",
                  "license": "GPL-3.0-only", "apps": ["rodaje"],
                  "module": { "entry": "Rodaje.Module.Echo.exe", "protocol": 1, "functions": ["eco"], "vramMb": 0, "models": [ {{models}} ] } }
                """);
        }

        return archive;
    }

    /// <summary>
    /// Un hilo con un contexto de un solo hilo, como el de la interfaz, que mide la racha más larga en que no queda libre
    /// (mensajes seguidos sin vaciar la cola) y lo más que espera un mensaje antes de atenderse (lo que esperaría un clic).
    /// </summary>
    private sealed class MeasuredUiThread : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State, long Posted)> _queue = [];
        private readonly Lock _gate = new();
        private bool _closed;

        public int Callbacks { get; private set; }

        public TimeSpan Busy { get; private set; }

        public TimeSpan LongestStretch { get; private set; }

        public TimeSpan LongestWait { get; private set; }

        public static MeasuredUiThread Run(Func<Task> body)
        {
            var context = new MeasuredUiThread();
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(context);
                Task task;
                try
                {
                    task = body();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                    return;
                }

                task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
                context.Loop();
                if (task.Exception is { } error)
                {
                    failure = ExceptionDispatchInfo.Capture(error.InnerExceptions[0]);
                }
            })
            { IsBackground = true, Name = "Hilo de interfaz medido" };
            thread.Start();
            Assert.True(thread.Join(UiThread.DefaultTimeout), "Las descargas no terminaron a tiempo. " + UiThread.PoolState());
            failure?.Throw();
            return context;
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_gate)
            {
                if (!_closed)
                {
                    _queue.Add((d, state, Stopwatch.GetTimestamp()));
                    return;
                }
            }

            ThreadPool.QueueUserWorkItem(static s => s.d(s.state), (d, state), preferLocal: false);
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        private void Complete()
        {
            lock (_gate)
            {
                _closed = true;
                _queue.CompleteAdding();
            }
        }

        private void Loop()
        {
            long? stretch = null;
            foreach ((SendOrPostCallback callback, object? state, long posted) in _queue.GetConsumingEnumerable())
            {
                long start = Stopwatch.GetTimestamp();
                stretch ??= start;
                LongestWait = Max(LongestWait, Stopwatch.GetElapsedTime(posted, start));
                callback(state);
                long end = Stopwatch.GetTimestamp();
                Callbacks++;
                Busy += Stopwatch.GetElapsedTime(start, end);
                if (_queue.Count == 0)
                {
                    LongestStretch = Max(LongestStretch, Stopwatch.GetElapsedTime(stretch.Value, end));
                    stretch = null;
                }
            }

            _queue.Dispose();
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }
}
