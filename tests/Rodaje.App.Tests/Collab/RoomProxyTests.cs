using System.Security.Cryptography;
using Dathinel.Sala;
using Rodaje.App.Services;
using Rodaje.App.Services.Collab;
using Rodaje.App.Services.Operations;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.TestMedia;

namespace Rodaje.App.Tests.Collab;

/// <summary>La decisión de no mandar proxies por el relé público de n0 (solo directo o relé propio).</summary>
public sealed class PublicRelayTests
{
    private static DiagnosticoPar Par(string key, Camino camino) =>
        new(key, "Luis", camino, camino == Camino.Rele ? "https://rele.example" : null, null, null, "1.0.4", null, null, null, 0, 0, 0);

    private static Diagnostico Diag(string releSolicitado, bool soloRele = false, params DiagnosticoPar[] pares) =>
        new("anfitrion", true, "1.0.4", new DiagnosticoRed(releSolicitado, null, releSolicitado != "ninguno", soloRele, false), pares, null, 0, null, null);

    [Fact]
    public void Sin_diagnostico_se_niega_y_con_rele_ninguno_pasa()
    {
        Assert.True(ProxyShareHost.OverPublicRelay(null, "luis"));
        Assert.False(ProxyShareHost.OverPublicRelay(Diag("ninguno"), "luis"));
    }

    [Fact]
    public void Con_n0_un_camino_que_no_consta_como_directo_se_niega()
    {
        Assert.True(ProxyShareHost.OverPublicRelay(Diag("n0", pares: Par("luis", Camino.Desconocido)), "luis"));
        Assert.True(ProxyShareHost.OverPublicRelay(Diag("n0"), "luis"));
    }

    [Fact]
    public void Solo_rele_en_n0_es_siempre_publico()
    {
        Assert.True(ProxyShareHost.OverPublicRelay(Diag("n0", soloRele: true), "luis"));
        Assert.True(ProxyShareHost.OverPublicRelay(Diag("n0", soloRele: true, Par("luis", Camino.Directo)), "luis"));
    }

    [Fact]
    public void Con_n0_el_par_por_rele_se_rechaza_y_el_directo_pasa()
    {
        Diagnostico mixto = Diag("n0", pares: [Par("luis", Camino.Rele), Par("ana", Camino.Directo)]);
        Assert.True(ProxyShareHost.OverPublicRelay(mixto, "luis"));
        Assert.False(ProxyShareHost.OverPublicRelay(mixto, "ana"));
        Assert.True(ProxyShareHost.OverPublicRelay(mixto, "desconocido"));
    }

    [Fact]
    public void Un_rele_propio_no_es_el_rele_de_pruebas()
    {
        Assert.False(ProxyShareHost.OverPublicRelay(Diag("https://rele.mi-casa.es:3340", pares: Par("luis", Camino.Rele)), "luis"));
        Assert.False(ProxyShareHost.OverPublicRelay(Diag("https://rele.mi-casa.es:3340", soloRele: true), "luis"));
    }
}

/// <summary>El servicio del invitado: nombres de archivo, qué medios se pueden pedir y el avance como operación.</summary>
public sealed class RoomProxiesTests
{
    private static readonly string Hash = new('a', 64);

    private static RoomProxies Proxies() =>
        new(null, Path.Combine(Path.GetTempPath(), "rodaje-sala", "prueba"),
            [new MediaManifestEntry("m1", "Toma", 100, Hash), new MediaManifestEntry("m2", "Otra", 200, new string('b', 64))]);

    [Fact]
    public void Solo_se_pueden_pedir_los_medios_del_manifiesto()
    {
        RoomProxies proxies = Proxies();
        Assert.True(proxies.CanRequest("m1"));
        Assert.False(proxies.CanRequest("nada"));
        Assert.Equal($"proxies{Path.DirectorySeparatorChar}proxy-{Hash}.mp4", proxies.RelativePathFor("m1"));
        Assert.Null(proxies.RelativePathFor("nada"));
        Assert.EndsWith($"proxies{Path.DirectorySeparatorChar}proxy-{Hash}.mp4", proxies.AbsolutePathFor("m1"));
    }

    [Fact]
    public async Task Pedir_un_medio_que_no_esta_explica_lo_que_pasa()
    {
        CollabException ex = await Assert.ThrowsAsync<CollabException>(() => Proxies().RequestAsync("nada"));
        Assert.Contains("no se puede pedir así", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Proxies().RequestAsync("m1"));
    }

    [Fact]
    public void La_ruta_de_una_copia_ligera_se_reconoce_y_la_del_usuario_no()
    {
        string hash = new('c', 64);
        Assert.True(RoomProxies.IsRoomProxyPath(Path.Combine("sala", "proxies", $"proxy-{hash}.mp4")));
        Assert.True(RoomProxies.IsRoomProxyPath($"proxies/proxy-{hash}.mp4"));
        Assert.False(RoomProxies.IsRoomProxyPath(Path.Combine("sala", "videos", $"proxy-{hash}.mp4")));
        Assert.False(RoomProxies.IsRoomProxyPath(Path.Combine("sala", "proxies", "toma.mp4")));
        Assert.False(RoomProxies.IsRoomProxyPath(Path.Combine("sala", "proxies", $"proxy-{hash}.mkv")));
        Assert.False(RoomProxies.IsRoomProxyPath(Path.Combine("sala", "proxies", "proxy-corta.mp4")));
        Assert.False(RoomProxies.IsRoomProxyPath(Path.Combine("sala", "proxies", $"{hash}-h540-g15-a128-v1.mp4")));
        Assert.False(RoomProxies.IsRoomProxyPath(Path.Combine("sala", "proxies", $"proxy-{new string('C', 64)}.mp4")));
    }

    [Fact]
    public void La_copia_ligera_usa_ruta_absoluta_en_memoria_y_relativa_solo_al_guardar()
    {
        string projectDir = Path.Combine(Path.GetTempPath(), "sala-prueba");
        var project = new Project { Name = "sala" };
        project.Media.Add(new MediaRef
        {
            Id = "m1",
            Path = "FALTA/toma.mp4",
            Name = "toma",
            Duration = Time.FromSeconds(4),
            HasVideo = true,
            HasAudio = true,
            FrameRate = Rational.Fps25,
        });
        var proxies = new RoomProxies(null, projectDir, [new MediaManifestEntry("m1", "toma", 100, Hash)]);
        string absolute = proxies.AbsolutePathFor("m1")!;

        new RelinkMediaCommand([new MediaRelinkTarget("m1", absolute)]).Apply(project);

        MediaRef media = project.Media[0];
        Assert.Equal(absolute, media.Path);
        Assert.True(Path.IsPathFullyQualified(media.Path));
        Assert.Equal(absolute, media.ResolvePath(Environment.CurrentDirectory));
        Assert.True(media.HasVideo && media.HasAudio);
        Assert.Equal(Rational.Fps25, media.FrameRate);
        Assert.True(RoomProxies.IsRoomProxyPath(media.ResolvePath(projectDir)));

        // El archivo del proyecto puede vivir en una subcarpeta; la base de la ruta guardada es esa, no la sala.
        string savedFolder = Path.Combine(projectDir, "recibido");
        MediaRef saved = ProjectPaths.ForSaving(project, savedFolder).GetMedia("m1");
        Assert.Equal(Path.GetRelativePath(savedFolder, absolute), saved.Path);
        Assert.Equal(absolute, saved.ResolvePath(savedFolder));
        Assert.Equal(absolute, media.Path);
    }

    [Fact]
    public void El_avance_del_reparto_se_ve_como_fases_y_fraccion_de_operacion()
    {
        var operation = new OperationProgress();
        IProgress<ShareProgress> adapted = RoomProxies.Adapt(operation)!;

        adapted.Report(new ShareProgress(1, 2, "Quien hospeda la sala está preparando la copia ligera", 0.4));
        adapted.Report(new ShareProgress(2, 2, "Recibiendo la copia ligera", 0.5));
        OperationSnapshot s = operation.Snapshot();

        Assert.Equal(2, s.Phase);
        Assert.Equal(2, s.PhaseCount);
        Assert.Equal(0.5, s.Fraction);
        Assert.Contains("Recibiendo", s.PhaseText, StringComparison.Ordinal);
    }
}

/// <summary>El <see cref="RoomProxyMaker"/> real: busca el medio por la huella entera y genera el proxy de la sala con FFmpeg.</summary>
public sealed class RoomProxyMakerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rodaje-sala-maker-" + Guid.NewGuid().ToString("N")[..8]);

    private static MediaSpec Spec { get; } = new("sala-720p-25-aac", 1280, 720, Rational.Fps25, TimeSpan.FromSeconds(8))
    {
        Codec = VideoCodec.H264,
        GopLength = 25,
        Audio = AudioCodec.Aac,
        Container = ContainerFormat.Mp4,
    };

    private string Source()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "toma.mp4");
        if (!File.Exists(path))
        {
            Rodaje.TestMedia.GeneratedMedia media = TestMediaGenerator.CreateDefault()!.GetOrCreate(Spec);
            File.Copy(media.Path, path);
        }

        return path;
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    [ProxyFact]
    public async Task Genera_el_proxy_de_sala_por_huella_dentro_de_la_carpeta()
    {
        string source = Source();
        string sha = await Sha256Async(source);
        string folder = Path.Combine(_root, "sala", "proxies");
        using var maker = new RoomProxyMaker(Skip.ProxyExecutable!, () => [new MediaRef { Id = "m1", Path = source }], m => m.Path,
            options: new ProxyGeneratorOptions { EncoderPreference = ["libopenh264"], HardwareDecoding = false });
        var fractions = new List<double>();

        RoomProxy? proxy = await maker.MakeAsync(sha, folder, new InlineProgress<double>(fractions.Add), CancellationToken.None);

        Assert.NotNull(proxy);
        Assert.StartsWith(Path.GetFullPath(folder), Path.GetFullPath(proxy.Path), StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"{sha}-h540-g15-a128-v1.mp4", Path.GetFileName(proxy.Path));
        Assert.Equal(new FileInfo(proxy.Path).Length, proxy.Size);
        Assert.Equal(await Sha256Async(proxy.Path), proxy.Sha256);
        Assert.Contains(fractions, f => f is > 0 and < 1);
        MediaInfo info = MediaProbe.Probe(proxy.Path);
        Assert.Equal("h264", info.Video!.Codec);
        Assert.True(info.HasAudio);
        Assert.Equal("aac", info.Audio!.Codec);
        Assert.Equal(2, info.Audio.Channels);
        Assert.Null(await maker.MakeAsync(new string('f', 64), folder, null, CancellationToken.None));
    }

    [ProxyFact]
    public async Task Dos_peticiones_concurrentes_comparten_un_solo_trabajo()
    {
        string source = Source();
        string sha = await Sha256Async(source);
        string folder = Path.Combine(_root, "sala", "proxies");
        using var maker = new RoomProxyMaker(Skip.ProxyExecutable!, () => [new MediaRef { Id = "m1", Path = source }], m => m.Path,
            options: new ProxyGeneratorOptions { EncoderPreference = ["libopenh264"], HardwareDecoding = false });

        Task<RoomProxy?> first = maker.MakeAsync(sha, folder, null, CancellationToken.None);
        Task<RoomProxy?> second = maker.MakeAsync(sha, folder, null, CancellationToken.None);

        Assert.Equal((await first)!.Path, (await second)!.Path);
        Assert.Single(Directory.EnumerateFiles(folder, "*.mp4"));
    }

    [ProxyFact]
    public async Task Se_cancela_sin_dejar_archivos_a_medias()
    {
        string source = Source();
        string sha = await Sha256Async(source);
        string folder = Path.Combine(_root, "sala", "proxies");
        using var maker = new RoomProxyMaker(Skip.ProxyExecutable!, () => [new MediaRef { Id = "m1", Path = source }], m => m.Path,
            options: new ProxyGeneratorOptions { EncoderPreference = ["libopenh264"], HardwareDecoding = false });
        using var cancel = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            maker.MakeAsync(sha, folder, new InlineProgress<double>(_ => cancel.Cancel()), cancel.Token));
        Assert.Empty(Directory.EnumerateFiles(folder, "*.mp4"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
