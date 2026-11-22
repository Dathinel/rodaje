using System.Security.Cryptography;
using Dathinel.Sala;
using Rodaje.App.Services;
using Rodaje.App.Services.Collab;
using Rodaje.App.Services.Media;
using Rodaje.App.Tests.Media;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Library;
using Rodaje.Core.Collab;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;

namespace Rodaje.App.Tests.Collab;

public sealed class ProxyShareTests
{
    private sealed class FakeMaker(Func<string, string, RoomProxy?> make, int steps = 0) : IRoomProxyMaker
    {
        public int Calls;

        public async Task<RoomProxy?> MakeAsync(string mediaHash, string folder, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            for (int i = 1; i <= steps; i++)
            {
                await Task.Delay(120, cancellationToken);
                progress?.Report((double)i / steps);
            }

            return make(mediaHash, folder);
        }
    }

    private static RoomProxy Write(string folder, int length)
    {
        byte[] data = new byte[length];
        new Random(length).NextBytes(data);
        string path = Path.Combine(folder, "p.mp4");
        File.WriteAllBytes(path, data);
        return new RoomProxy(path, Convert.ToHexStringLower(SHA256.HashData(data)), length);
    }

    private static readonly string MediaHash = new('a', 64);

    [SalaFact]
    public void La_copia_recibida_reemplaza_el_origen_ausente_sin_reabrir_el_proyecto() => UiThread.Run(async () =>
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        FakeMaker maker = new((_, folder) => Write(folder, 300_000));
        using ProxyShareHost share = new(host, maker, host.RoomFolder);

        string missing = Path.Combine(guest.RoomFolder, "original-ausente.mp4");
        var playback = new FakePlayback();
        var session = new EditorSession(playback, new FakeProber().Video(missing), new FakeDialogs(), new SequentialIdGenerator());
        string id = Assert.Single((await session.ImportMediaAsync([missing])).MediaIds);
        Assert.True(session.AppendToTimeline(id));
        MediaRef original = session.Project.GetMedia(id).Clone();
        string sharedBefore = ProjectWire.ToJson(session.Project);
        var proxies = new RoomProxies(guest, guest.RoomFolder, [new MediaManifestEntry(id, "clip-a", 100, MediaHash)]);
        using var resolver = new MediaSourceResolver(new ProxyStore(Path.Combine(guest.RoomFolder, "preview-cache")), generator: null);
        MediaSourceResolution unavailable = resolver.Resolve(original, Environment.CurrentDirectory, MediaPurpose.Preview);
        Assert.False(File.Exists(unavailable.VideoPath));
        Assert.False(File.Exists(unavailable.AudioPath));

        // Las mismas suscripciones que usa la ventana: renovar los visuales y volver a revisar la biblioteca tras editar.
        var sources = new FakeSources();
        using var sourceGate = sources.Gate;
        using var sourceStuck = sources.Stuck;
        sources.Broken.Add(missing);
        using var visuals = new ClipVisuals(mediaId => session.Project.FindMedia(mediaId)?.Path, sources,
            new ClipVisualsOptions { ThumbnailWorkers = 1 }, SynchronizationContext.Current);
        IReadOnlyList<string> offline = MediaDuplicates.Offline(session.Project.Media.Select(m => new MediaFile(m.Id, m.Path)));
        int edits = 0;
        session.ProjectEdited += (_, _) =>
        {
            visuals.ForgetRemovedMedia();
            offline = MediaDuplicates.Offline(session.Project.Media.Select(m => new MediaFile(m.Id, m.Path)));
            edits++;
        };
        Assert.Contains(id, offline);
        Assert.Null(visuals.GetThumbnail(id, Time.Zero, 48));
        await CollabHarness.Until(() => visuals.ThumbnailProblem(missing) is not null, "el fallo del original", seconds: 60);
        playback.Loads.Clear();
        playback.Reloads.Clear();

        // RequestAsync devolvía proxies/proxy-… relativo a la sala, que los consumidores buscaban desde el proceso.
        string path = await proxies.RequestAsync(id).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(proxies.Has(id));
        Assert.True(session.Execute(new RelinkMediaCommand([new MediaRelinkTarget(id, path)])));

        Assert.Empty(offline);
        Assert.Equal(1, edits);
        Assert.Empty(playback.Loads);
        Project reloaded = Assert.Single(playback.Reloads);
        MediaRef media = reloaded.GetMedia(id);
        string expected = Path.Combine(guest.RoomFolder, "proxies", RoomProxies.FileName(MediaHash));
        Assert.Equal(expected, path);
        Assert.True(Path.IsPathFullyQualified(media.Path));
        Assert.Equal(expected, media.ResolvePath(Environment.CurrentDirectory));
        // Tampoco depende de que el proyecto esté guardado en la carpeta de la sala.
        string projectFolder = Path.Combine(guest.RoomFolder, "recibido");
        Assert.Equal(expected, media.ResolvePath(projectFolder));
        Assert.True(File.Exists(media.Path));
        Assert.True(RoomProxies.IsRoomProxyPath(media.Path));
        Assert.True(media.HasVideo && media.HasAudio);
        Assert.Equal(original.Duration, media.Duration);
        Assert.Equal(original.FrameRate, media.FrameRate);
        Assert.Equal(original.Width, media.Width);
        Assert.Equal(original.Height, media.Height);
        Assert.Equal(original.SampleRate, media.SampleRate);
        Assert.Equal(original.AudioChannels, media.AudioChannels);
        Assert.Equal(sharedBefore, ProjectWire.ToJson(session.Project));

        // El resolvedor de la vista previa no necesita un proxy registrado del original ausente: abre la copia con audio.
        MediaSourceResolution available = resolver.Resolve(media, Environment.CurrentDirectory, MediaPurpose.Preview);
        Assert.Equal(expected, available.VideoPath);
        Assert.Equal(expected, available.AudioPath);
        Assert.Null(available.Proxy);

        Assert.Null(visuals.ThumbnailProblem(missing));
        await CollabHarness.Until(() => visuals.GetThumbnail(id, Time.Zero, 48) is not null, "la miniatura de la copia ligera", seconds: 60);
        Assert.Contains(sources.Decoded, frame => frame.Path == expected);

        Project saved = ProjectPaths.ForSaving(session.Project, projectFolder);
        Assert.False(Path.IsPathFullyQualified(saved.GetMedia(id).Path));
        Assert.Equal(expected, saved.GetMedia(id).ResolvePath(projectFolder));
        Assert.Equal(expected, session.Project.GetMedia(id).Path);
    });

    [SalaFact]
    public async Task El_invitado_recibe_la_copia_ligera_con_su_avance_y_se_hace_una_sola_vez()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        FakeMaker maker = new((_, folder) => Write(folder, 300_000), steps: 3);
        using ProxyShareHost share = new(host, maker, host.RoomFolder);
        List<ShareProgress> seen = [];
        string dest = Path.Combine(guest.RoomFolder, "proxies", "a.mp4");

        string path = await ProxyShareGuest.RequestAsync(guest, MediaHash, dest, new Sync<ShareProgress>(seen.Add)).WaitAsync(TimeSpan.FromSeconds(30));
        string again = await ProxyShareGuest.RequestAsync(guest, MediaHash, Path.Combine(guest.RoomFolder, "proxies", "b.mp4")).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(dest, path);
        Assert.Equal(300_000, new FileInfo(path).Length);
        Assert.Equal(new FileInfo(path).Length, new FileInfo(again).Length);
        Assert.Equal(1, maker.Calls);
        Assert.Contains(seen, p => p.Step == 1 && p.Fraction is > 0 and <= 1);
        Assert.Contains(seen, p => p.Step == 2 && p.Fraction == 1);
    }

    [SalaFact]
    public async Task Sin_el_original_el_anfitrion_lo_dice_y_un_proxy_fuera_de_la_carpeta_se_rechaza()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        string outside = Path.Combine(CollabHarness.TempFolder(), "fuera.mp4");
        File.WriteAllBytes(outside, new byte[100]);
        FakeMaker maker = new((hash, _) => hash[0] == 'a' ? null : new RoomProxy(outside, new string('b', 64), 100));
        using ProxyShareHost share = new(host, maker, host.RoomFolder);

        CollabException missing = await Assert.ThrowsAsync<CollabException>(() =>
            ProxyShareGuest.RequestAsync(guest, MediaHash, Path.Combine(guest.RoomFolder, "x.mp4")).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("no tiene ese archivo", missing.Message, StringComparison.Ordinal);

        CollabException hostile = await Assert.ThrowsAsync<CollabException>(() =>
            ProxyShareGuest.RequestAsync(guest, new string('c', 64), Path.Combine(guest.RoomFolder, "y.mp4")).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("No se pudo preparar", hostile.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<CollabException>(() => ProxyShareGuest.RequestAsync(guest, "no-es-una-huella", Path.Combine(guest.RoomFolder, "z.mp4")));
    }

    private static Diagnostico Diag(string releSolicitado, bool soloRele, string guestKey, Camino camino) =>
        new("anfitrion", true, "1.0.4",
            new DiagnosticoRed(releSolicitado, null, releSolicitado != "ninguno", soloRele, false),
            [new DiagnosticoPar(guestKey, "Luis", camino, null, null, null, "1.0.4", null, null, null, 0, 0, 0)],
            null, 0, null, null);

    private static string GuestKey(RoomService guest) => guest.State.Members.First(m => m.IsMe).Key;

    [SalaFact]
    public async Task Por_el_rele_publico_de_n0_el_anfitrion_se_niega_sin_generar()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        FakeMaker maker = new((_, folder) => Write(folder, 300_000));
        Diagnostico n0 = Diag("n0", soloRele: true, GuestKey(guest), Camino.Rele);
        using ProxyShareHost share = new(host, maker, host.RoomFolder, _ => Task.FromResult<Diagnostico?>(n0));

        CollabException ex = await Assert.ThrowsAsync<CollabException>(() =>
            ProxyShareGuest.RequestAsync(guest, MediaHash, Path.Combine(guest.RoomFolder, "x.mp4")).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Contains("relé de pruebas", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, maker.Calls);
    }

    [SalaFact]
    public async Task Por_camino_directo_o_rele_propio_si_se_mandan_proxies()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        string key = GuestKey(guest);
        FakeMaker maker = new((_, folder) => Write(folder, 300_000));
        var diagnostics = new Queue<Diagnostico>([
            // El relé de n0 está configurado pero este par habla por un camino directo: se permite.
            Diag("n0", soloRele: false, key, Camino.Directo),
            // Todo va por un relé propio, no por el de pruebas: se permite.
            Diag("https://rele.mi-casa.es:3340", soloRele: true, key, Camino.Rele),
        ]);
        using ProxyShareHost share = new(host, maker, host.RoomFolder, _ => Task.FromResult<Diagnostico?>(diagnostics.Dequeue()));

        string first = await ProxyShareGuest.RequestAsync(guest, MediaHash, Path.Combine(guest.RoomFolder, "a.mp4")).WaitAsync(TimeSpan.FromSeconds(30));
        string second = await ProxyShareGuest.RequestAsync(guest, MediaHash, Path.Combine(guest.RoomFolder, "b.mp4")).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(File.Exists(first) && File.Exists(second));
        Assert.Equal(1, maker.Calls);
    }

    [SalaFact]
    public async Task Dos_peticiones_a_la_vez_generan_una_sola_vez()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        FakeMaker maker = new((_, folder) => Write(folder, 300_000), steps: 4);
        using ProxyShareHost share = new(host, maker, host.RoomFolder);

        Task<string> a = ProxyShareGuest.RequestAsync(guest, MediaHash, Path.Combine(guest.RoomFolder, "a.mp4"));
        Task<string> b = ProxyShareGuest.RequestAsync(guest, MediaHash, Path.Combine(guest.RoomFolder, "b.mp4"));
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(File.Exists(await a) && File.Exists(await b));
        Assert.Equal(1, maker.Calls);
    }

    [SalaFact]
    public async Task El_texto_del_fallo_del_maker_llega_al_invitado()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        FakeMaker maker = new((_, _) => throw new CollabException("Este equipo no puede hacer copias ligeras: le falta el componente de video."));
        using ProxyShareHost share = new(host, maker, host.RoomFolder);

        CollabException ex = await Assert.ThrowsAsync<CollabException>(() =>
            ProxyShareGuest.RequestAsync(guest, MediaHash, Path.Combine(guest.RoomFolder, "x.mp4")).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Contains("componente de video", ex.Message, StringComparison.Ordinal);
    }

    private sealed class Sync<T>(Action<T> add) : IProgress<T>
    {
        public void Report(T value)
        {
            lock (this)
            {
                add(value);
            }
        }
    }
}
