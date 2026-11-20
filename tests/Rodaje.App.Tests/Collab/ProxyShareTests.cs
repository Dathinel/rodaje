using System.Security.Cryptography;
using Dathinel.Sala;
using Rodaje.App.Services.Collab;

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
