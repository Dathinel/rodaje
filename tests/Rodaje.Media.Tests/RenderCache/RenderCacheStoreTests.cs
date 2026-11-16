using System.Text.Json;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.RenderCache;
using Rodaje.Media.Tests.Support;
using Rodaje.Playback.Composition;

namespace Rodaje.Media.Tests.RenderCache;

public sealed class RenderCacheStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _clock = new(Start);
    private readonly RenderCacheStore _store;

    public RenderCacheStoreTests()
    {
        _store = new RenderCacheStore(_temp.Combine("almacén de render"), _clock);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Carpeta_por_omision_es_local_app_data_render_salvo_variable_de_entorno()
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rodaje", "render");
        Assert.Equal(
            Environment.GetEnvironmentVariable(RenderCacheStore.DirectoryEnvironmentVariable) is { Length: > 0 } configured ? configured : expected,
            RenderCacheStore.DefaultDirectory);
    }

    [Fact]
    public void Ida_y_vuelta_commit_y_almacen_nuevo_encuentra_cada_huella_con_su_indice_de_cuadro()
    {
        var k0 = MakeKey(10);
        var k1 = MakeKey(11);
        var k2 = MakeKey(12);
        var frames = new[] { k0, k1, k2 };

        RenderCacheFragment fragment = CommitFake(_store, frames, bytes: 2048, width: 3840, height: 2160, colorSpace: SequenceColorSpace.HdrPq);

        Assert.Equal(3, _store.Count);
        Assert.True(_store.TryGet(k0, null, out RenderCacheHit hit0));
        Assert.Equal(0, hit0.FrameIndex);
        Assert.Equal(fragment.MoviePath, hit0.Fragment.MoviePath);

        Assert.True(_store.TryGet(k1, null, out RenderCacheHit hit1));
        Assert.Equal(1, hit1.FrameIndex);

        Assert.True(_store.TryGet(k2, null, out RenderCacheHit hit2));
        Assert.Equal(2, hit2.FrameIndex);

        // Almacén nuevo sobre la misma carpeta
        var store2 = new RenderCacheStore(_store.Directory, _clock);
        Assert.Equal(3, store2.Count);

        Assert.True(store2.TryGet(k0, null, out RenderCacheHit hit0Reloaded));
        Assert.Equal(0, hit0Reloaded.FrameIndex);
        Assert.Equal(fragment.MoviePath, hit0Reloaded.Fragment.MoviePath);
        Assert.Equal(3840, hit0Reloaded.Fragment.Metadata.Width);
        Assert.Equal(2160, hit0Reloaded.Fragment.Metadata.Height);
        Assert.Equal(SequenceColorSpace.HdrPq, hit0Reloaded.Fragment.Metadata.ColorSpace);
        Assert.Equal(2048, hit0Reloaded.Fragment.Metadata.MovieBytes);

        Assert.True(store2.TryGet(k1, null, out RenderCacheHit hit1Reloaded));
        Assert.Equal(1, hit1Reloaded.FrameIndex);

        Assert.True(store2.TryGet(k2, null, out RenderCacheHit hit2Reloaded));
        Assert.Equal(2, hit2Reloaded.FrameIndex);

        Assert.False(store2.TryGet(MakeKey(999), null, out _));
    }

    [Fact]
    public void Apagon_simulado_temporal_a_medias_huerfanos_y_mov_truncado_no_aciertan()
    {
        // 1. Temporal a medias nunca acierta
        string temp = _store.CreateTemporaryPath();
        File.WriteAllBytes(temp, new byte[500]);
        Assert.False(_store.TryGet(MakeKey(1), null, out _));
        Assert.Equal(0, _store.Count);

        // 2. .mov sin .json nunca acierta
        string orphanMovie = Path.Combine(_store.Directory, "huerfano.mov");
        File.WriteAllBytes(orphanMovie, new byte[500]);
        var storeAfterMovie = new RenderCacheStore(_store.Directory, _clock);
        Assert.Equal(0, storeAfterMovie.Count);

        // 3. .json sin .mov nunca acierta
        var keyJsonOnly = MakeKey(2);
        string jsonOnly = Path.Combine(_store.Directory, "solo_json.json");
        File.WriteAllText(jsonOnly, JsonSerializer.Serialize(new RenderCacheMetadataDto
        {
            FormatVersion = 1,
            Width = 1920,
            Height = 1080,
            FrameRateNum = 25,
            FrameRateDen = 1,
            ColorSpace = SequenceColorSpace.Sdr709,
            Frames = [keyJsonOnly.ToString()],
            MovieBytes = 500,
        }));
        var storeAfterJson = new RenderCacheStore(_store.Directory, _clock);
        Assert.Equal(0, storeAfterJson.Count);
        Assert.False(storeAfterJson.TryGet(keyJsonOnly, null, out _));

        // 4. .mov truncado no acierta al recargar
        var keyTruncated = MakeKey(3);
        RenderCacheFragment valid = CommitFake(_store, [keyTruncated], bytes: 1000);
        Assert.True(_store.TryGet(keyTruncated, null, out _));

        // Truncar el archivo .mov
        File.WriteAllBytes(valid.MoviePath, new byte[100]);

        var storeReloaded = new RenderCacheStore(_store.Directory, _clock);
        Assert.Equal(0, storeReloaded.Count);
        Assert.False(storeReloaded.TryGet(keyTruncated, null, out _));
    }

    [Fact]
    public void Commit_con_tamano_distinto_de_movie_bytes_lanza_y_no_publica_nada()
    {
        string temp = _store.CreateTemporaryPath();
        File.WriteAllBytes(temp, new byte[300]);

        var metadata = CreateMetadata([MakeKey(1)], movieBytes: 1000);

        Assert.Throws<ArgumentException>(() => _store.Commit(temp, metadata));
        Assert.Equal(0, _store.Count);
        Assert.False(_store.TryGet(MakeKey(1), null, out _));

        // El temporal sigue existiendo tal cual y no se publicó ningún archivo final
        Assert.True(File.Exists(temp));
        Assert.DoesNotContain(Directory.EnumerateFiles(_store.Directory), f => !RenderCacheStore.IsTemporary(f));
    }

    [Fact]
    public void Forget_hace_que_deje_de_acertar()
    {
        var k0 = MakeKey(100);
        var k1 = MakeKey(101);
        RenderCacheFragment fragment = CommitFake(_store, [k0, k1], bytes: 500);

        Assert.Equal(2, _store.Count);
        Assert.True(_store.TryGet(k0, null, out _));
        Assert.True(_store.TryGet(k1, null, out _));

        _store.Forget(fragment);

        Assert.Equal(0, _store.Count);
        Assert.False(_store.TryGet(k0, null, out _));
        Assert.False(_store.TryGet(k1, null, out _));

        // Los archivos en disco siguen intactos
        Assert.True(File.Exists(fragment.MoviePath));
        Assert.True(File.Exists(fragment.MetadataPath));
    }

    [Fact]
    public void Continuidad_con_misma_huella_en_dos_fragmentos_prefiere_mismo_fragmento_siguiente_cuadro()
    {
        var kA = MakeKey(1);
        var kB = MakeKey(2);
        var kShared = MakeKey(3);

        // Fragmento 1: [kA, kShared]
        RenderCacheFragment f1 = CommitFake(_store, [kA, kShared], bytes: 1000);

        // Fragmento 2: [kB, kShared]
        RenderCacheFragment f2 = CommitFake(_store, [kB, kShared], bytes: 1000);

        Assert.Equal(3, _store.Count);

        // Leemos kA de f1 (FrameIndex == 0)
        Assert.True(_store.TryGet(kA, null, out RenderCacheHit hitA));
        Assert.Equal(f1.MoviePath, hitA.Fragment.MoviePath);
        Assert.Equal(0, hitA.FrameIndex);

        // Al pedir kShared pasando previous=hitA, debe elegir f1 (FrameIndex == 1, que es hitA.FrameIndex + 1)
        Assert.True(_store.TryGet(kShared, hitA, out RenderCacheHit hitSharedFrom1));
        Assert.Equal(f1.MoviePath, hitSharedFrom1.Fragment.MoviePath);
        Assert.Equal(1, hitSharedFrom1.FrameIndex);

        // Ahora leemos kB de f2 (FrameIndex == 0)
        Assert.True(_store.TryGet(kB, null, out RenderCacheHit hitB));
        Assert.Equal(f2.MoviePath, hitB.Fragment.MoviePath);
        Assert.Equal(0, hitB.FrameIndex);

        // Al pedir kShared pasando previous=hitB, debe elegir f2 (FrameIndex == 1, que es hitB.FrameIndex + 1)
        Assert.True(_store.TryGet(kShared, hitB, out RenderCacheHit hitSharedFrom2));
        Assert.Equal(f2.MoviePath, hitSharedFrom2.Fragment.MoviePath);
        Assert.Equal(1, hitSharedFrom2.FrameIndex);

        // Sin previous, devuelve el primero
        Assert.True(_store.TryGet(kShared, null, out RenderCacheHit hitDefault));
        Assert.Equal(f1.MoviePath, hitDefault.Fragment.MoviePath);
    }

    [Fact]
    public void Tope_borra_los_mas_viejos_y_respeta_protegidos()
    {
        var k1 = MakeKey(10);
        var k2 = MakeKey(20);
        var k3 = MakeKey(30);

        _clock.Now = Start;
        RenderCacheFragment f1 = CommitFake(_store, [k1], bytes: 1000);

        _clock.Now = Start.AddMinutes(1);
        RenderCacheFragment f2 = CommitFake(_store, [k2], bytes: 1000);

        _clock.Now = Start.AddMinutes(2);
        RenderCacheFragment f3 = CommitFake(_store, [k3], bytes: 1000);

        // Accedemos a f1 en t=10 min para que sea el más recientemente usado
        _clock.Now = Start.AddMinutes(10);
        Assert.True(_store.TryGet(k1, null, out _));

        // Orden de antigüedad de uso: f2 (t=1, más viejo), f3 (t=2), f1 (t=10, más reciente)
        // Tope que solo admite un fragmento (~1300 bytes entre .mov y .json)
        long oneFragmentBytes = new FileInfo(f1.MoviePath).Length + new FileInfo(f1.MetadataPath).Length;
        RenderCacheCleanupReport report = _store.Cleanup(new RenderCacheCleanupOptions
        {
            MaxTotalBytes = oneFragmentBytes + 50,
        });

        Assert.Equal(2, report.Evicted);
        Assert.Equal(0, report.Protected);
        Assert.True(File.Exists(f1.MoviePath));
        Assert.False(File.Exists(f2.MoviePath));
        Assert.False(File.Exists(f3.MoviePath));
        Assert.True(_store.TryGet(k1, null, out _));
        Assert.False(_store.TryGet(k2, null, out _));
        Assert.False(_store.TryGet(k3, null, out _));
        Assert.Equal(1, _store.Count);

        // Segunda parte: con todos protegidos no se borra ninguno aunque el tope no se cumpla y Protected lo cuenta
        var storeProtegido = new RenderCacheStore(_temp.Combine("almacén protegido"), _clock);
        var kp1 = MakeKey(101);
        var kp2 = MakeKey(102);
        var kp3 = MakeKey(103);

        RenderCacheFragment fp1 = CommitFake(storeProtegido, [kp1], bytes: 1000);
        RenderCacheFragment fp2 = CommitFake(storeProtegido, [kp2], bytes: 1000);
        RenderCacheFragment fp3 = CommitFake(storeProtegido, [kp3], bytes: 1000);

        long protOneFragmentBytes = new FileInfo(fp1.MoviePath).Length + new FileInfo(fp1.MetadataPath).Length;
        RenderCacheCleanupReport reportProt = storeProtegido.Cleanup(new RenderCacheCleanupOptions
        {
            MaxTotalBytes = protOneFragmentBytes + 50,
            ProtectedKeys = new HashSet<RenderCacheKey> { kp1, kp2, kp3 },
        });

        Assert.Equal(0, reportProt.Evicted);
        Assert.Equal(3, reportProt.Protected);
        Assert.True(File.Exists(fp1.MoviePath));
        Assert.True(File.Exists(fp2.MoviePath));
        Assert.True(File.Exists(fp3.MoviePath));
        Assert.Equal(3, storeProtegido.Count);
        Assert.True(reportProt.RemainingBytes > 1200);
    }

    [Fact]
    public void Limpieza_de_temporales_viejos_y_huerfanos_viejos_sin_tocar_los_recientes()
    {
        _clock.Now = Start;

        // 1. Temporales
        string oldTemp = _store.CreateTemporaryPath();
        File.WriteAllBytes(oldTemp, new byte[100]);
        File.SetLastWriteTimeUtc(oldTemp, Start.UtcDateTime.AddHours(-2));

        string freshTemp = _store.CreateTemporaryPath();
        File.WriteAllBytes(freshTemp, new byte[100]);
        File.SetLastWriteTimeUtc(freshTemp, Start.UtcDateTime);

        // 2. Huérfanos .mov
        string oldOrphanMov = Path.Combine(_store.Directory, "viejo_huerfano_mov.mov");
        File.WriteAllBytes(oldOrphanMov, new byte[200]);
        File.SetLastWriteTimeUtc(oldOrphanMov, Start.UtcDateTime.AddHours(-2));

        string freshOrphanMov = Path.Combine(_store.Directory, "fresco_huerfano_mov.mov");
        File.WriteAllBytes(freshOrphanMov, new byte[200]);
        File.SetLastWriteTimeUtc(freshOrphanMov, Start.UtcDateTime);

        // 3. Huérfanos .json
        string oldOrphanJson = Path.Combine(_store.Directory, "viejo_huerfano_json.json");
        File.WriteAllText(oldOrphanJson, "{}");
        File.SetLastWriteTimeUtc(oldOrphanJson, Start.UtcDateTime.AddHours(-2));

        string freshOrphanJson = Path.Combine(_store.Directory, "fresco_huerfano_json.json");
        File.WriteAllText(freshOrphanJson, "{}");
        File.SetLastWriteTimeUtc(freshOrphanJson, Start.UtcDateTime);

        RenderCacheCleanupReport report = _store.Cleanup(new RenderCacheCleanupOptions
        {
            TemporaryMaxAge = TimeSpan.FromHours(1),
        });

        Assert.Equal(1, report.TemporaryFiles);
        Assert.Equal(2, report.Orphans);

        Assert.False(File.Exists(oldTemp));
        Assert.True(File.Exists(freshTemp));

        Assert.False(File.Exists(oldOrphanMov));
        Assert.True(File.Exists(freshOrphanMov));

        Assert.False(File.Exists(oldOrphanJson));
        Assert.True(File.Exists(freshOrphanJson));
    }

    [Fact]
    public void Json_con_otra_version_o_corrupto_no_se_carga()
    {
        Directory.CreateDirectory(_store.Directory);
        var kVersion = MakeKey(1);
        var kCorrupt = MakeKey(2);

        // Archivo con versión 99 (distinta de la actual)
        string mov1 = Path.Combine(_store.Directory, "otra_version.mov");
        string json1 = Path.Combine(_store.Directory, "otra_version.json");
        File.WriteAllBytes(mov1, new byte[100]);
        File.WriteAllText(json1, JsonSerializer.Serialize(new RenderCacheMetadataDto
        {
            FormatVersion = 99,
            Width = 1920,
            Height = 1080,
            FrameRateNum = 25,
            FrameRateDen = 1,
            ColorSpace = SequenceColorSpace.Sdr709,
            Frames = [kVersion.ToString()],
            MovieBytes = 100,
        }));

        // Archivo JSON corrupto
        string mov2 = Path.Combine(_store.Directory, "corrupto.mov");
        string json2 = Path.Combine(_store.Directory, "corrupto.json");
        File.WriteAllBytes(mov2, new byte[100]);
        File.WriteAllText(json2, "{ esto no es json valido");

        var storeReloaded = new RenderCacheStore(_store.Directory, _clock);
        Assert.Equal(0, storeReloaded.Count);
        Assert.False(storeReloaded.TryGet(kVersion, null, out _));
        Assert.False(storeReloaded.TryGet(kCorrupt, null, out _));

        // Cleanup debe eliminar los JSON corruptos / no válidos
        RenderCacheCleanupReport report = storeReloaded.Cleanup();
        Assert.True(report.Orphans >= 2);
        Assert.False(File.Exists(json1));
        Assert.False(File.Exists(json2));
    }

    [Fact]
    public void Total_bytes_suma_mov_y_json_y_devuelve_cero_si_no_existe()
    {
        var storeNoExiste = new RenderCacheStore(_temp.Combine("no_existe"), _clock);
        Assert.Equal(0, storeNoExiste.TotalBytes());

        var k = MakeKey(1);
        RenderCacheFragment fragment = CommitFake(_store, [k], bytes: 1000);

        long movSize = new FileInfo(fragment.MoviePath).Length;
        long jsonSize = new FileInfo(fragment.MetadataPath).Length;

        Assert.Equal(movSize + jsonSize, _store.TotalBytes());
    }

    [Fact]
    [Trait("Categoria", "SoloWindows")]
    public void Fragmento_abierto_se_salta_sin_excepcion_durante_desalojo()
    {
        var k = MakeKey(1);
        RenderCacheFragment fragment = CommitFake(_store, [k], bytes: 1000);

        using (new FileStream(fragment.MoviePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Intentar desalojar con tope de 0 bytes
            RenderCacheCleanupReport report = _store.Cleanup(new RenderCacheCleanupOptions
            {
                MaxTotalBytes = 0,
            });

            Assert.Equal(0, report.Evicted);
            Assert.True(File.Exists(fragment.MoviePath));
            Assert.True(File.Exists(fragment.MetadataPath));
            Assert.True(_store.TryGet(k, null, out _));
        }
    }

    [Fact]
    public void Tras_reabrir_el_almacen_el_tope_sigue_el_ultimo_uso()
    {
        var k1 = MakeKey(41);
        var k2 = MakeKey(42);
        var k3 = MakeKey(43);
        _clock.Now = Start;
        RenderCacheFragment f1 = CommitFake(_store, [k1], bytes: 1000);
        _clock.Now = Start.AddMinutes(1);
        RenderCacheFragment f2 = CommitFake(_store, [k2], bytes: 1000);
        _clock.Now = Start.AddMinutes(2);
        RenderCacheFragment f3 = CommitFake(_store, [k3], bytes: 1000);
        _clock.Now = Start.AddMinutes(10);
        Assert.True(_store.TryGet(k1, null, out _));

        var reopened = new RenderCacheStore(_store.Directory, _clock);
        long oneFragmentBytes = new FileInfo(f1.MoviePath).Length + new FileInfo(f1.MetadataPath).Length;
        reopened.Cleanup(new RenderCacheCleanupOptions { MaxTotalBytes = oneFragmentBytes + 50 });

        Assert.True(File.Exists(f1.MoviePath));
        Assert.False(File.Exists(f2.MoviePath));
        Assert.False(File.Exists(f3.MoviePath));
    }

    [Fact]
    public void Leer_cuadro_a_cuadro_no_escribe_la_fecha_del_archivo_en_cada_lectura()
    {
        var keys = new[] { MakeKey(51), MakeKey(52), MakeKey(53) };
        _clock.Now = Start;
        RenderCacheFragment fragment = CommitFake(_store, keys);
        _clock.Now = Start.AddMinutes(10);
        Assert.True(_store.TryGet(keys[0], null, out RenderCacheHit hit));

        _clock.Now = Start.AddMinutes(10).AddSeconds(30);
        Assert.True(_store.TryGet(keys[1], hit, out hit));
        Assert.True(_store.TryGet(keys[2], hit, out _));
        Assert.Equal(Start.AddMinutes(10).UtcDateTime, File.GetLastAccessTimeUtc(fragment.MoviePath));

        _clock.Now = Start.AddMinutes(12);
        Assert.True(_store.TryGet(keys[0], null, out _));
        Assert.Equal(Start.AddMinutes(12).UtcDateTime, File.GetLastAccessTimeUtc(fragment.MoviePath));
    }

    [Fact]
    public void Sin_anterior_un_fragmento_viejo_de_pocos_cuadros_no_tapa_al_continuo()
    {
        RenderCacheKey[] keys = [.. Enumerable.Range(1, 60).Select(i => MakeKey((ulong)i))];
        RenderCacheFragment sparse = CommitFake(_store, keys[..8]);
        RenderCacheFragment continuous = CommitFake(_store, keys);

        Assert.True(_store.TryGet(keys[0], null, out RenderCacheHit hit));

        Assert.Equal(continuous.MoviePath, hit.Fragment.MoviePath);
        Assert.NotEqual(sparse.MoviePath, hit.Fragment.MoviePath);
    }

    [Fact]
    public void Un_cuadro_no_consecutivo_del_mismo_fragmento_no_gana_a_otro_continuo()
    {
        RenderCacheKey x = MakeKey(100);
        RenderCacheKey[] run = [.. Enumerable.Range(1, 60).Select(i => MakeKey((ulong)i))];
        RenderCacheFragment first = CommitFake(_store, [x, run[0], run[1]]);
        RenderCacheFragment continuous = CommitFake(_store, run);
        var end = new RenderCacheHit(first, 2);

        Assert.True(_store.TryGet(run[0], end, out RenderCacheHit next));

        Assert.Equal(continuous.MoviePath, next.Fragment.MoviePath);
        Assert.Equal(0, next.FrameIndex);
    }

    [Fact]
    public void Un_commit_durante_la_limpieza_no_se_olvida_del_indice()
    {
        RenderCacheFragment old = CommitFake(_store, [MakeKey(1)]);
        RenderCacheFragment? during = null;
        _store.AfterEnumerate = () => during = CommitFake(_store, [MakeKey(2)]);

        _store.Cleanup();

        Assert.True(_store.TryGet(MakeKey(1), null, out RenderCacheHit kept));
        Assert.Equal(old.MoviePath, kept.Fragment.MoviePath);
        Assert.True(_store.TryGet(MakeKey(2), null, out RenderCacheHit published));
        Assert.Equal(during!.MoviePath, published.Fragment.MoviePath);
    }

    private static RenderCacheKey MakeKey(ulong value) => new(0x1234_5678_9ABC_DEF0, value);

    private static RenderCacheFragmentMetadata CreateMetadata(
        IReadOnlyList<RenderCacheKey> frames,
        long movieBytes = 1000,
        int width = 1920,
        int height = 1080,
        Rational? frameRate = null,
        SequenceColorSpace colorSpace = SequenceColorSpace.Sdr709,
        string codec = "prores_hq",
        bool playbackOnly = false) =>
        new(width, height, frameRate ?? Rational.Fps25, colorSpace, codec, playbackOnly, frames, movieBytes);

    private static RenderCacheFragment CommitFake(
        RenderCacheStore store,
        IReadOnlyList<RenderCacheKey> frames,
        long bytes = 1000,
        int width = 1920,
        int height = 1080,
        SequenceColorSpace colorSpace = SequenceColorSpace.Sdr709,
        string codec = "prores_hq",
        bool playbackOnly = false)
    {
        string temporary = store.CreateTemporaryPath();
        File.WriteAllBytes(temporary, new byte[bytes]);
        var metadata = CreateMetadata(frames, bytes, width, height, colorSpace: colorSpace, codec: codec, playbackOnly: playbackOnly);
        RenderCacheFragment fragment = store.Commit(temporary, metadata);
        Assert.False(File.Exists(temporary));
        return fragment;
    }
}
