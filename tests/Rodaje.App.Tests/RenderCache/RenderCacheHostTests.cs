using System.Collections.Concurrent;
using Rodaje.App.Services;
using Rodaje.App.Services.RenderCache;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.RenderCache;
using Rodaje.Export.Running;
using Rodaje.Media.RenderCache;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Xunit;

namespace Rodaje.App.Tests.RenderCache;

public sealed class RenderCacheHostTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-cache-host-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void La_prevision_marca_rojo_tres_capas_4K_y_el_cuadro_que_abre_un_decodificador()
    {
        Project project = Titles(seconds: 4);
        project.Media.Add(new MediaRef { Id = "uhd", Path = @"C:\no\uhd.mov", HasVideo = true, Width = 3840, Height = 2160, Duration = Time.FromSeconds(10) });
        for (int t = 0; t < 3; t++)
        {
            Track track = new() { Id = $"v{t + 2}", Kind = TrackKind.Video };
            track.Clips.Add(new Clip { Id = $"c{t}", MediaId = "uhd", Start = Time.FromSeconds(1), SourceIn = Time.Zero, SourceOut = Time.FromSeconds(1) });
            project.Sequences[0].VideoTracks.Add(track);
        }

        RenderCostClass[] classes = RenderCacheScan.Classify(project, project.Sequences[0], 120, hasGpu: true);
        Project single = Titles(seconds: 4);
        single.Media.Add(project.Media[0]);
        single.Sequences[0].VideoTracks.Add(project.Sequences[0].VideoTracks[1]);
        RenderCostClass[] one = RenderCacheScan.Classify(single, single.Sequences[0], 120, hasGpu: true);

        Assert.All(classes[..30], c => Assert.Equal(RenderCostClass.Light, c));
        Assert.All(classes[30..60], c => Assert.Equal(RenderCostClass.Heavy, c));
        Assert.All(classes[60..], c => Assert.Equal(RenderCostClass.Light, c));
        Assert.Equal(RenderCostClass.Heavy, one[30]);
        Assert.All(one[31..60], c => Assert.Equal(RenderCostClass.Light, c));
    }

    [Fact]
    public void La_franja_pone_verde_lo_cubierto_rojo_lo_tarde_y_el_trabajo_salta_lo_que_no_tiene_huella()
    {
        RenderCacheKey?[] keys = [.. Enumerable.Range(0, 10).Select(i => i == 7 ? (RenderCacheKey?)null : Key(i))];
        RenderCostClass[] classes = [.. Enumerable.Repeat(RenderCostClass.Light, 4), .. Enumerable.Repeat(RenderCostClass.Heavy, 6)];
        var scan = new RenderCacheScan(1, Rational.Fps30, keys, classes);
        var coverage = new CacheCoverage(1, [new CachedSpan(4, 2, "a.mov", 0)]);

        IReadOnlyList<CacheStripSpan> strip = scan.Strip(coverage, new HashSet<long> { 1 });

        Assert.Equal([new(0, 1, CacheStripState.Light), new(1, 2, CacheStripState.Heavy), new(2, 4, CacheStripState.Light),
            new(4, 6, CacheStripState.Cached), new(6, 10, CacheStripState.Heavy)], strip);
        Assert.Equal([new(1, 2, RenderCostClass.Heavy), new(6, 7, RenderCostClass.Heavy), new(8, 10, RenderCostClass.Heavy)], scan.Work(strip));
    }

    [Fact]
    public void La_cadena_cambia_con_la_version_de_ffmpeg_y_lleva_la_huella_de_los_shaders()
    {
        Assert.NotEqual(RenderCacheJob.Chain("ffmpeg version 9.0.2"), RenderCacheJob.Chain("ffmpeg version 9.0.3"));
        Assert.StartsWith(RenderChain.ShaderFingerprint, RenderCacheJob.Chain("x"), StringComparison.Ordinal);
        Assert.Equal(64, RenderChain.ShaderFingerprint.Length);
    }

    [Fact]
    public async Task Pone_la_cobertura_de_su_generacion_y_un_fragmento_ilegible_sale_y_la_rehace()
    {
        Project project = Titles(seconds: 3);
        var sources = new AppRecipeSources(null, _ => false, RenderCacheJob.Chain(""));
        RenderCacheScan scan = RenderCacheScan.Run(project, "sec", 0, sources.Options(null), true, CancellationToken.None);
        Assert.All(scan.Keys, k => Assert.NotNull(k));
        string movie = Commit(new RenderCacheStore(_folder), scan, first: 0, count: 60);
        var player = new FakePlayer();
        using var host = new RenderCacheHost(player, RenderCacheMode.Manual, _folder, 10, null, null, _ => false, () => null,
            tick: Timeout.InfiniteTimeSpan);

        host.OnPlan(new RenderCachePlan(project, "sec", 7, 0, 0));

        (int generation, CacheCoverage coverage) = await player.NextAsync();
        Assert.Equal(7, generation);
        Assert.Equal([new CachedSpan(0, 60, movie, 0)], coverage.Spans);
        Assert.Contains(host.Strip, s => s is { Start: 0, End: 60, State: CacheStripState.Cached });

        host.OnSourceFailed(movie);

        (_, CacheCoverage after) = await player.NextAsync();
        Assert.Empty(after.Spans);
        Assert.DoesNotContain(host.Strip, s => s.State == CacheStripState.Cached);
    }

    [Fact]
    public async Task Calcular_de_entrada_a_salida_encola_solo_lo_que_no_esta_en_el_cache()
    {
        Project project = Titles(seconds: 4);
        var sources = new AppRecipeSources(null, _ => false, RenderCacheJob.Chain(""));
        RenderCacheScan scan = RenderCacheScan.Run(project, "sec", 0, sources.Options(null), true, CancellationToken.None);
        Commit(new RenderCacheStore(_folder), scan, first: 30, count: 30);
        var player = new FakePlayer();
        using var host = new RenderCacheHost(player, RenderCacheMode.Manual, _folder, 10, null, null, _ => false, () => null,
            tick: Timeout.InfiniteTimeSpan);
        host.OnPlan(new RenderCachePlan(project, "sec", 1, 0, 0));
        await player.NextAsync();

        host.Request(Time.Zero, Time.FromSeconds(3));

        Assert.Equal(2, host.Status.Pending);
        Assert.Equal(RenderCacheState.Waiting, host.Status.State);
    }

    [Fact]
    public async Task Un_trozo_se_calcula_con_el_proyecto_del_que_salieron_sus_huellas_aunque_llegue_otro_plan()
    {
        Project first = Titles(seconds: 3);
        Project second = Titles(seconds: 3);
        second.Sequences[0].VideoTracks[0].Clips[0].Generator!.Text = "Otro";
        var player = new FakePlayer();
        using var host = new RenderCacheHost(player, RenderCacheMode.Manual, _folder, 10, null, null, _ => false, () => null,
            tick: Timeout.InfiniteTimeSpan);
        host.OnPlan(new RenderCachePlan(first, "sec", 1, 4, 0));
        await player.NextAsync();

        host.OnPlan(new RenderCachePlan(second, "sec", 1, 5, 0));

        (RenderCachePlan plan, RenderCacheKey[] keys) = host.JobInput(new RenderCacheWork(0, 30, 4))!.Value;
        Assert.Same(first, plan.Project);
        Assert.Equal(30, keys.Length);
        Assert.Null(host.JobInput(new RenderCacheWork(0, 30, 5)));
    }

    [Fact]
    public void Los_recursos_de_archivo_entran_por_su_contenido_aunque_lleven_file_y_ruta_relativa()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_folder, "proyecto", "luts")).Parent!.FullName;
        File.WriteAllBytes(Path.Combine(folder, "luts", "calida.cube"), [1, 2, 3, 4]);
        var sources = new AppRecipeSources(null, _ => false, "");

        string? absolute = sources.Resource(new ResourceKey("file:" + Path.Combine(folder, "luts", "calida.cube").Replace(Path.DirectorySeparatorChar, '/')), null);
        string? relative = sources.Resource(new ResourceKey("file:luts/calida.cube"), folder);

        Assert.NotNull(absolute);
        Assert.Equal(absolute, relative);
        Assert.Null(sources.Resource(new ResourceKey("file:luts/falta.cube"), folder));
        Assert.Null(sources.Resource(new ResourceKey("file:luts/calida.cube"), null));
        Assert.Null(sources.Resource(new ResourceKey("rodaje-filtros@1.0.0/calida"), null));
        Assert.Equal("0.1,0.2 0.3,0.4", sources.Resource(new ResourceKey("0.1,0.2 0.3,0.4"), folder));
    }

    [Fact]
    public void La_exportacion_lee_el_almacen_vigente_tras_cambiar_de_carpeta()
    {
        var player = new FakePlayer();
        using var host = new RenderCacheHost(player, RenderCacheMode.Manual, _folder, 10, null, null, _ => false, () => null,
            tick: Timeout.InfiniteTimeSpan);
        IRenderCacheStore store = host.WithRenderCache(new ExportRunnerOptions { Ffmpeg = null! }).RenderCache!;
        string other = Path.Combine(_folder, "otra");

        host.Configure(RenderCacheMode.Manual, other, 10);

        Assert.Equal(Path.GetFullPath(other), store.Directory);
    }

    [Fact]
    public void Sin_saber_el_espacio_libre_no_se_frena()
    {
        Assert.True(RenderCacheHost.FreeBytes(@"\\servidor-que-no-existe\compartida\render") >= RenderCacheService.MinimumFreeBytes);
    }

    private static string Commit(RenderCacheStore store, RenderCacheScan scan, long first, int count)
    {
        string temporary = store.CreateTemporaryPath();
        File.WriteAllBytes(temporary, new byte[100]);
        RenderCacheKey[] keys = scan.KeysOf(first, count)!;
        return store.Commit(temporary, new RenderCacheFragmentMetadata(1920, 1080, Rational.Fps30, SequenceColorSpace.Sdr709, "prores_hq", false, keys, 100)).MoviePath;
    }

    private static RenderCacheKey Key(int i)
    {
        byte[] bytes = new byte[16];
        bytes[0] = (byte)i;
        bytes[1] = 1;
        return RenderCacheKey.FromHash(bytes);
    }

    /// <summary>Una secuencia de 1080p30 con un título de texto distinto por segundo (cada cuadro con su huella).</summary>
    private static Project Titles(int seconds)
    {
        var project = new Project { Name = "p" };
        var sequence = new Sequence { Id = "sec", FrameRate = Rational.Fps30, Width = 1920, Height = 1080 };
        var track = new Track { Id = "v1", Kind = TrackKind.Video };
        for (int i = 0; i < seconds; i++)
        {
            track.Clips.Add(new Clip
            {
                Id = $"t{i}", Start = Time.FromSeconds(i), SourceIn = Time.Zero, SourceOut = Time.FromSeconds(1),
                Generator = new ClipGenerator { Kind = GeneratorKinds.Text, Text = $"Título {i}" },
            });
        }

        sequence.VideoTracks.Add(track);
        project.Sequences.Add(sequence);
        return project;
    }

    private sealed class FakePlayer : IRenderCachePlayer
    {
        private readonly BlockingCollection<(int, CacheCoverage)> _applied = [];

        public PlaybackSnapshot Snapshot { get; set; } = new(Time.Zero, Time.FromSeconds(4), false, 0, 120, "");

        public IReadOnlyList<long> LateFrames { get; set; } = [];

        public bool HasGpu => true;

        public void ApplyCoverage(int generation, CacheCoverage coverage) => _applied.Add((generation, coverage));

        public Task<(int, CacheCoverage)> NextAsync() => Task.Run(() =>
            _applied.TryTake(out (int, CacheCoverage) next, TimeSpan.FromSeconds(30)) ? next : throw new TimeoutException("No llegó la cobertura."));
    }
}
