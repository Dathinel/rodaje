using System.Security.Cryptography;
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
