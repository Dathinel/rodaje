using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Rodaje.Modules.Protocol;

namespace Rodaje.Experimentos.Eco;

/// <summary>
/// <c>--solo-socket</c>: lo que también corre en Windows 10+. Los sockets de dominio Unix de .NET sirven
/// ahí, así que la prueba hace el camino del protocolo sin procesos ni P/Invoke: escucha en una ruta de
/// %TEMP%, conecta desde el mismo proceso, saluda (<c>hello</c>/<c>welcome</c>) y mide un <c>ping</c> por
/// el <see cref="MessageChannel"/> real.
/// </summary>
internal static class SoloSocket
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("== Eco --solo-socket: socket de dominio Unix, saludo y ping ==");
        string dir = Path.Combine(Path.GetTempPath(), "rodaje-eco-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "m.sock");
        int bytes = Encoding.UTF8.GetByteCount(path);
        int hechas = 0;
        const int total = 3;

        Socket listener = UnixTransport.Listen(path);
        Console.WriteLine($"  pasa   socket-escucha            {path} ({bytes} B)");
        hechas++;
        try
        {
            Task servidor = Task.Run(async () =>
            {
                Socket conn = await listener.AcceptAsync().ConfigureAwait(false);
                using MessageChannel ch = new(new NetworkStream(conn, ownsSocket: true));
                if (await ch.ReceiveAsync().ConfigureAwait(false) is not HelloMessage hello)
                    return;
                int chosen = Math.Min(ProtocolVersion.Current, hello.MaxProtocol);
                if (chosen < Math.Max(ProtocolVersion.Current, hello.MinProtocol))
                    return;
                await ch.SendAsync(new WelcomeMessage(chosen, EcoInfo.Modulo)).ConfigureAwait(false);
                while (await ch.ReceiveAsync().ConfigureAwait(false) is { } m)
                {
                    switch (m)
                    {
                        case PingMessage ping:
                            await ch.SendAsync(new PongMessage(ping.Id)).ConfigureAwait(false);
                            break;
                        case ShutdownMessage:
                            return;
                    }
                }
            });

            Socket client = await UnixTransport.ConnectAsync(path).ConfigureAwait(false);
            using MessageChannel channel = new(new NetworkStream(client, ownsSocket: true));
            await channel.SendAsync(new HelloMessage(ProtocolVersion.Current, ProtocolVersion.Current, "rodaje", "1.0.3", "es-CO")).ConfigureAwait(false);
            bool saludo = await channel.ReceiveAsync().ConfigureAwait(false) is WelcomeMessage { Protocol: ProtocolVersion.Current } w
                && w.Module.Functions.Any(f => f.Id == "eco");
            Console.WriteLine($"  {(saludo ? "pasa " : "FALLA")}  saludo-v1                 hello/welcome con «eco»");
            if (saludo) hechas++;

            long t0 = Stopwatch.GetTimestamp();
            await channel.SendAsync(new PingMessage(1)).ConfigureAwait(false);
            bool pong = false;
            while (await channel.ReceiveAsync().ConfigureAwait(false) is { } m)
            {
                if (m is PongMessage { Id: 1 })
                {
                    pong = true;
                    break;
                }
            }
            double us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
            Console.WriteLine($"  {(pong ? "pasa " : "FALLA")}  ping-pong                 {us:0} µs de ida y vuelta");
            if (pong) hechas++;

            await channel.SendAsync(new ShutdownMessage()).ConfigureAwait(false);
            await servidor.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        finally
        {
            listener.Dispose();
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { Directory.Delete(dir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Console.WriteLine($"Resultado: {hechas}/{total} — {(hechas == total ? "correcto" : "falla")}");
        return hechas == total ? 0 : 2;
    }
}
