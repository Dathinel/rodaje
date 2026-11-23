using System.Security.Cryptography;
using Dathinel.Sala;
using Rodaje.App.Services.Collab;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Collab;

public sealed class OriginalShareTests
{
    /// <summary>El trozo más pequeño que acepta el invitado (las pruebas cortan a mitad sin mover gigas).</summary>
    private const long Chunk = 64L << 10;

    private sealed class FakeSource(Func<string, CancellationToken, Task<OriginalFile?>> find) : IOriginalSource
    {
        public int Calls;

        public Task<OriginalFile?> FindAsync(string mediaHash, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return find(mediaHash, cancellationToken);
        }
    }

    private static (string Path, string Hash) Write(string folder, int length, string name = "clip-a.mp4")
    {
        byte[] data = new byte[length];
        new Random(length).NextBytes(data);
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, data);
        return (path, Convert.ToHexStringLower(SHA256.HashData(data)));
    }

    private static OriginalFile Original(string path) =>
        new(path, new FileInfo(path).Length, Path.GetFileNameWithoutExtension(path));

    private static readonly OriginalGuestOptions Quick = new()
    {
        Retries = 2,
        RetryDelay = TimeSpan.FromMilliseconds(10),
        AnswerTimeout = TimeSpan.FromSeconds(10),
    };

    private static string GuestKey(RoomService guest) => guest.State.Members.First(m => m.IsMe).Key;

    private static Diagnostico Diag(string releSolicitado, bool soloRele, string guestKey, Camino camino) =>
        new("anfitrion", true, "1.0.4",
            new DiagnosticoRed(releSolicitado, null, releSolicitado != "ninguno", soloRele, false),
            [new DiagnosticoPar(guestKey, "Luis", camino, null, null, null, "1.0.4", null, null, null, 0, 0, 0)],
            null, 0, null, null);

    [SalaFact]
    public async Task El_original_llega_por_trozos_comprobado_y_con_el_nombre_del_medio()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        (string path, string hash) = Write(host.RoomFolder, 200_000);
        FakeSource source = new((_, _) => Task.FromResult<OriginalFile?>(Original(path)));
        List<OriginalAsk> asks = [];
        using OriginalShareHost share = new(host, source, host.RoomFolder,
            (ask, _) => { lock (asks) { asks.Add(ask); } return Task.FromResult(OriginalDecision.Send); })
        { ChunkBytes = Chunk };

        string medios = Path.Combine(guest.RoomFolder, RoomOriginals.FolderName);
        List<ShareProgress> seen = [];
        string final = await OriginalShareGuest.RequestAsync(guest, new MediaManifestEntry("m1", "clip-a", 200_000, hash), medios,
            new Sync<ShareProgress>(seen.Add), Quick).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(Path.Combine(Path.GetFullPath(medios), "clip-a.mp4"), final);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(final));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(medios, OriginalShareGuest.PartialFolder)));
        OriginalAsk ask = Assert.Single(asks);
        Assert.Equal("Luis", ask.Alias);
        Assert.Equal("clip-a", ask.MediaName);
        Assert.Equal(200_000, ask.Size);
        Assert.Contains(seen, p => p.Step == 1);
        Assert.Contains(seen, p => p.Step == 2 && p.Fraction is > 0 and <= 1);
        Assert.Contains(seen, p => p.Step == 3);
    }

    [SalaFact]
    public async Task Quien_hospeda_lo_niega_o_no_puede_aprobar_y_el_invitado_lo_oye()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        (string path, string hash) = Write(host.RoomFolder, 100_000);
        FakeSource source = new((_, _) => Task.FromResult<OriginalFile?>(Original(path)));
        string medios = Path.Combine(guest.RoomFolder, RoomOriginals.FolderName);
        MediaManifestEntry entry = new("m1", "clip-a", 100_000, hash);

        using (OriginalShareHost refusing = new(host, source, host.RoomFolder,
            (_, _) => Task.FromResult(OriginalDecision.Refuse)) { ChunkBytes = Chunk })
        {
            CollabException ex = await Assert.ThrowsAsync<CollabException>(() =>
                OriginalShareGuest.RequestAsync(guest, entry, medios, options: Quick).WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Contains("decidió no enviarte", ex.Message, StringComparison.Ordinal);
        }

        // Sin la ventana de la aprobación (o cerrada), el anfitrión lo niega todo.
        using OriginalShareHost deaf = new(host, source, host.RoomFolder, approve: null) { ChunkBytes = Chunk };
        CollabException noApprove = await Assert.ThrowsAsync<CollabException>(() =>
            OriginalShareGuest.RequestAsync(guest, entry, medios, options: Quick).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("no puede aprobar", noApprove.Message, StringComparison.Ordinal);
    }

    [SalaFact]
    public async Task Si_quien_hospeda_no_contesta_en_el_plazo_el_invitado_lo_sabe_sin_colgarse()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        (string path, string hash) = Write(host.RoomFolder, 100_000);
        FakeSource source = new((_, _) => Task.FromResult<OriginalFile?>(Original(path)));
        using OriginalShareHost share = new(host, source, host.RoomFolder,
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return OriginalDecision.Send;
            })
        { ChunkBytes = Chunk, ApprovalTimeout = TimeSpan.FromMilliseconds(600), WaitingSignal = TimeSpan.FromMilliseconds(100) };

        // La espera del invitado es corta de propósito: los avisos de «sigue en pie» la van renovando hasta la negativa.
        CollabException ex = await Assert.ThrowsAsync<CollabException>(() =>
            OriginalShareGuest.RequestAsync(guest, new MediaManifestEntry("m1", "clip-a", 100_000, hash),
                Path.Combine(guest.RoomFolder, RoomOriginals.FolderName),
                options: Quick with { AnswerTimeout = TimeSpan.FromMilliseconds(350) }).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Contains("no respondió", ex.Message, StringComparison.Ordinal);
    }

    [SalaFact]
    public async Task Por_el_rele_publico_de_n0_no_salen_originales()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        (string path, string hash) = Write(host.RoomFolder, 100_000);
        FakeSource source = new((_, _) => Task.FromResult<OriginalFile?>(Original(path)));
        Diagnostico n0 = Diag("n0", soloRele: true, GuestKey(guest), Camino.Rele);
        using OriginalShareHost share = new(host, source, host.RoomFolder,
            (_, _) => Task.FromResult(OriginalDecision.Send), diagnostic: _ => Task.FromResult<Diagnostico?>(n0))
        { ChunkBytes = Chunk };

        CollabException ex = await Assert.ThrowsAsync<CollabException>(() =>
            OriginalShareGuest.RequestAsync(guest, new MediaManifestEntry("m1", "clip-a", 100_000, hash),
                Path.Combine(guest.RoomFolder, RoomOriginals.FolderName), options: Quick).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Contains("relé de pruebas", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, source.Calls);
    }

    [SalaFact]
    public async Task Un_trozo_que_falla_a_medias_no_deja_su_temporal()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        // El original dice pesar 200 000 bytes pero solo tiene 70 000: el segundo trozo se corta a mitad de copia.
        (string path, string hash) = Write(host.RoomFolder, 70_000);
        FakeSource source = new((_, _) => Task.FromResult<OriginalFile?>(new OriginalFile(path, 200_000, "clip-a")));
        using OriginalShareHost share = new(host, source, host.RoomFolder, (_, _) => Task.FromResult(OriginalDecision.Send)) { ChunkBytes = Chunk };

        await Assert.ThrowsAsync<CollabException>(() => OriginalShareGuest.RequestAsync(guest, new MediaManifestEntry("m1", "clip-a", 200_000, hash),
            Path.Combine(guest.RoomFolder, RoomOriginals.FolderName), options: Quick).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(host.RoomFolder, "originales"), "*.tmp"));
    }

    [SalaFact]
    public async Task Solo_quien_baja_el_original_retira_sus_trozos_al_terminar()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        await using RoomService eva = new(CollabHarness.Env(), new RecordingHandler());
        CollabInvitation invite = await host.CreateInviteAsync(TimeSpan.FromMinutes(10));
        Task joining = eva.JoinAsync(invite.Link, "Eva", CollabHarness.TempFolder());
        await CollabHarness.Until(() => host.State.Requests.Count == 1, "la solicitud de Eva");
        await host.ApproveAsync(host.State.Requests[0].Key, Rol.Editar);
        await joining.WaitAsync(TimeSpan.FromSeconds(20));
        await CollabHarness.Until(() => eva.HostKey.Length > 0 && guest.State.Members.Count == 3, "tres en la sala");

        (string path, string hash) = Write(host.RoomFolder, 200_000);
        FakeSource source = new((_, _) => Task.FromResult<OriginalFile?>(Original(path)));
        using OriginalShareHost share = new(host, source, host.RoomFolder, (_, _) => Task.FromResult(OriginalDecision.Send)) { ChunkBytes = Chunk };

        // Luis pide el original y su primer trozo, y se queda a mitad.
        string chunk = Path.Combine(host.RoomFolder, "originales", hash + "-0.trozo");
        await guest.SendDirectAsync(ShareMessages.Make(OriginalMessages.Ask, w => w.WriteString("sha256", hash)), guest.HostKey);
        await Task.Delay(500);
        await guest.SendDirectAsync(ShareMessages.Make(OriginalMessages.ChunkAsk, w => { w.WriteString("sha256", hash); w.WriteNumber("i", 0); }), guest.HostKey);
        await CollabHarness.Until(() => File.Exists(chunk), "el primer trozo preparado");

        // Eva no lo estaba bajando: su «terminado» no toca los trozos de Luis.
        await eva.SendDirectAsync(ShareMessages.Make(OriginalMessages.Done, w => w.WriteString("sha256", hash)), eva.HostKey);
        await Task.Delay(1000);
        Assert.True(File.Exists(chunk), "el trozo de Luis sigue ahí");

        await guest.SendDirectAsync(ShareMessages.Make(OriginalMessages.Done, w => w.WriteString("sha256", hash)), guest.HostKey);
        await CollabHarness.Until(() => !File.Exists(chunk), "los trozos retirados cuando Luis termina");
    }

    [SalaFact]
    public async Task Un_corte_en_un_trozo_se_reintenta_solo()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        (string path, string hash) = Write(host.RoomFolder, 70_000);
        // La segunda búsqueda (la del primer trozo) se cae una vez: la conexión se cortó a mitad.
        int calls = 0;
        FakeSource source = new((_, _) => Interlocked.Increment(ref calls) == 2
            ? Task.FromException<OriginalFile?>(new IOException("se fue la red"))
            : Task.FromResult<OriginalFile?>(Original(path)));
        using OriginalShareHost share = new(host, source, host.RoomFolder,
            (_, _) => Task.FromResult(OriginalDecision.Send))
        { ChunkBytes = Chunk };

        string final = await OriginalShareGuest.RequestAsync(guest, new MediaManifestEntry("m1", "clip-a", 70_000, hash),
            Path.Combine(guest.RoomFolder, RoomOriginals.FolderName), options: Quick).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(final));
        Assert.True(calls >= 3, $"Esperaba al menos 3 búsquedas (pedir, trozo caído, trozo repetido); hubo {calls}.");
    }

    [SalaFact]
    public async Task Lo_que_ya_llego_en_otra_peticion_no_se_baja_dos_veces()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        (string path, string hash) = Write(host.RoomFolder, 200_000);
        FakeSource source = new((_, _) => Task.FromResult<OriginalFile?>(Original(path)));
        using OriginalShareHost share = new(host, source, host.RoomFolder,
            (_, _) => Task.FromResult(OriginalDecision.Send))
        { ChunkBytes = Chunk };

        // De una petición anterior quedó el primer trozo entero: la nueva sigue desde el segundo.
        string medios = Path.Combine(guest.RoomFolder, RoomOriginals.FolderName);
        string partials = Path.Combine(medios, OriginalShareGuest.PartialFolder);
        Directory.CreateDirectory(partials);
        await File.WriteAllBytesAsync(Path.Combine(partials, hash + ".parcial"), File.ReadAllBytes(path).AsMemory(0, (int)Chunk));

        string final = await OriginalShareGuest.RequestAsync(guest, new MediaManifestEntry("m1", "clip-a", 200_000, hash), medios,
            options: Quick).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(final));
        // Una búsqueda al pedir y una por cada trozo que faltaba (3 de los 4).
        Assert.Equal(1 + 3, source.Calls);
    }

    [SalaFact]
    public async Task Lo_que_llega_distinto_se_borra_y_no_se_enlaza()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        (string path, _) = Write(host.RoomFolder, 100_000);
        // Otro contenido del mismo tamaño (Write siembra el azar con el tamaño: con el mismo saldría el mismo archivo).
        string hashOtro = Convert.ToHexStringLower(SHA256.HashData(Enumerable.Repeat((byte)7, 100_000).ToArray()));
        FakeSource source = new((_, _) => Task.FromResult<OriginalFile?>(Original(path)));
        using OriginalShareHost share = new(host, source, host.RoomFolder,
            (_, _) => Task.FromResult(OriginalDecision.Send))
        { ChunkBytes = Chunk };

        // El manifiesto dice otra huella: lo que llega es del tamaño justo pero no es ese archivo.
        string medios = Path.Combine(guest.RoomFolder, RoomOriginals.FolderName);
        CollabException ex = await Assert.ThrowsAsync<CollabException>(() =>
            OriginalShareGuest.RequestAsync(guest, new MediaManifestEntry("m1", "clip-a", 100_000, hashOtro), medios,
                options: Quick).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Contains("no es idéntico", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(medios, OriginalShareGuest.PartialFolder, hashOtro + ".parcial")));
        Assert.False(Directory.EnumerateFiles(medios).Any());
    }

    [SalaFact]
    public async Task Sin_sitio_en_el_disco_se_niega_antes_de_pedirlo()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        (string path, string hash) = Write(host.RoomFolder, 100_000);
        FakeSource source = new((_, _) => Task.FromResult<OriginalFile?>(Original(path)));
        int asks = 0;
        using OriginalShareHost share = new(host, source, host.RoomFolder,
            (_, _) => { Interlocked.Increment(ref asks); return Task.FromResult(OriginalDecision.Send); })
        { ChunkBytes = Chunk };

        CollabException ex = await Assert.ThrowsAsync<CollabException>(() =>
            OriginalShareGuest.RequestAsync(guest, new MediaManifestEntry("m1", "clip-a", 100_000, hash),
                Path.Combine(guest.RoomFolder, RoomOriginals.FolderName),
                options: Quick with { FreeSpace = _ => 50L }).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Contains("No cabe en el disco", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, asks);
        Assert.Equal(0, source.Calls);
    }

    [SalaFact]
    public async Task El_tope_de_tamano_se_aplica_en_el_invitado_y_en_el_anfitrion()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        (string path, string hash) = Write(host.RoomFolder, 100_000);
        FakeSource source = new((_, _) => Task.FromResult<OriginalFile?>(Original(path)));
        int asks = 0;
        using OriginalShareHost share = new(host, source, host.RoomFolder,
            (_, _) => { Interlocked.Increment(ref asks); return Task.FromResult(OriginalDecision.Send); },
            maxBytes: () => 50_000L)
        { ChunkBytes = Chunk };

        MediaManifestEntry entry = new("m1", "clip-a", 100_000, hash);
        // El tope del invitado lo corta antes de llegar a la red.
        RoomOriginals capped = new(guest, guest.RoomFolder, [entry], maxBytes: () => 50_000L, options: Quick);
        CollabException local = await Assert.ThrowsAsync<CollabException>(() =>
            capped.RequestOriginalAsync("m1").WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("tope", local.Message, StringComparison.Ordinal);
        Assert.Equal(0, asks);

        // Y el del anfitrión lo niega aunque el invitado no tenga tope.
        RoomOriginals open = new(guest, guest.RoomFolder, [entry], options: Quick);
        CollabException remote = await Assert.ThrowsAsync<CollabException>(() =>
            open.RequestOriginalAsync("m1").WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("tope", remote.Message, StringComparison.Ordinal);
        Assert.Equal(0, asks);
    }

    [SalaFact]
    public async Task El_nombre_hostil_del_manifiesto_queda_dentro_de_medios()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        (string path, string hash) = Write(host.RoomFolder, 100_000);
        FakeSource source = new((_, _) => Task.FromResult<OriginalFile?>(Original(path)));
        using OriginalShareHost share = new(host, source, host.RoomFolder,
            (_, _) => Task.FromResult(OriginalDecision.Send))
        { ChunkBytes = Chunk };

        string medios = Path.Combine(guest.RoomFolder, RoomOriginals.FolderName);
        string final = await OriginalShareGuest.RequestAsync(guest, new MediaManifestEntry("m1", @"..\..\..\fuera", 100_000, hash),
            medios, options: Quick).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(Path.Combine(Path.GetFullPath(medios), "fuera.mp4"), final);
        Assert.False(File.Exists(Path.Combine(guest.RoomFolder, "fuera.mp4")));
    }

    [SalaFact]
    public async Task Pedir_todos_sigue_con_los_demas_cuando_uno_falla()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        (string path, string hash) = Write(host.RoomFolder, 100_000);
        string missing = new('f', 64);
        FakeSource source = new((asked, _) => Task.FromResult(asked == hash ? Original(path) : null));
        using OriginalShareHost share = new(host, source, host.RoomFolder,
            (_, _) => Task.FromResult(OriginalDecision.SendAll))
        { ChunkBytes = Chunk };

        RoomOriginals originals = new(guest, guest.RoomFolder,
            [new MediaManifestEntry("m1", "clip-a", 100_000, hash), new MediaManifestEntry("m2", "clip-b", 100_000, missing)],
            options: Quick);
        IReadOnlyList<OriginalResult> results = await originals.RequestAllAsync(["m1", "m2"]).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, results.Count);
        Assert.NotNull(results[0].Path);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(results[0].Path!));
        Assert.Null(results[1].Path);
        Assert.Contains("no tiene ese archivo", results[1].Problem ?? "", StringComparison.Ordinal);
    }

    [SalaFact]
    public async Task Huella_o_tamano_que_no_valen_en_el_manifiesto_se_rechazan_sin_salir_a_la_red()
    {
        await using RoomService guest = new(CollabHarness.Env(), new RecordingHandler());
        await Assert.ThrowsAsync<CollabException>(() =>
            OriginalShareGuest.RequestAsync(guest, new MediaManifestEntry("m1", "clip-a", 100, "no-es-una-huella"), CollabHarness.TempFolder()));
        await Assert.ThrowsAsync<CollabException>(() =>
            OriginalShareGuest.RequestAsync(guest, new MediaManifestEntry("m1", "clip-a", 0, new string('a', 64)), CollabHarness.TempFolder()));
    }

    [Fact]
    public void Los_nombres_que_vienen_de_fuera_no_escapan_de_la_carpeta()
    {
        Assert.Equal("evil.mp4", OriginalNames.SafeFileName(@"..\..\evil.mp4", ".mp4"));
        Assert.Equal("_CON.mp4", OriginalNames.SafeFileName("CON", ".mp4"));
        Assert.Equal("medio", OriginalNames.SafeFileName("...", ""));
        Assert.Equal("medio.mp4", OriginalNames.SafeFileName("", ".mp4"));
        Assert.Equal("clip-a.mp4", OriginalNames.SafeFileName("clip-a", ".mp4"));

        string folder = CollabHarness.TempFolder();
        string first = OriginalNames.UniquePath(folder, "clip.mp4");
        File.WriteAllBytes(first, [0]);
        Assert.Equal(Path.Combine(Path.GetFullPath(folder), "clip (2).mp4"), OriginalNames.UniquePath(folder, "clip.mp4"));
    }

    [Fact]
    public void Tras_el_reenlace_el_original_ya_no_es_solo_copia_ligera()
    {
        string folder = CollabHarness.TempFolder();
        string final = Path.Combine(folder, RoomOriginals.FolderName, "clip-a.mp4");
        var project = new Project { Name = "sala" };
        project.Media.Add(new MediaRef { Id = "m1", Path = @"proxies\proxy-" + new string('a', 64) + ".mp4", Name = "clip-a" });
        Assert.True(RoomProxies.IsRoomProxyPath(project.Media[0].ResolvePath(folder)));

        new RelinkMediaCommand([new MediaRelinkTarget("m1", final)]).Apply(project);

        Assert.Equal(final, project.Media[0].Path);
        Assert.False(RoomProxies.IsRoomProxyPath(project.Media[0].ResolvePath(folder)));
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
