using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

internal static class Support
{
    public static readonly ModuleInfo EchoInfo = new("prueba", "1.0.0", "Prueba",
    [
        new FunctionInfo("eco", "Eco"),
        new FunctionInfo("contar", "Contar"),
        new FunctionInfo("fallar", "Fallar"),
        new FunctionInfo("copiar", "Copiar"),
    ]);

    /// <summary>
    /// Cota de las pruebas que corren en la CI y miden algo que debe pasar «pronto» (fallar, matar, salir). Holgada para el
    /// runner de Windows cargado, donde 1 s llegó a 4,6 s sin defecto, pero no un tope contra cuelgues: un retraso de
    /// segundos sigue fallando. La cota fina va en las gemelas <c>TiempoReal</c>.
    /// </summary>
    public static readonly TimeSpan CiBound = TimeSpan.FromSeconds(10);

    /// <summary>El módulo de ejemplo; <c>RODAJE_MODULO_ECO</c> apunta a otro, por ejemplo el publicado con AOT.</summary>
    public static string EchoExe =>
        Environment.GetEnvironmentVariable("RODAJE_MODULO_ECO") is { Length: > 0 } exe ? exe : Path.Combine(AppContext.BaseDirectory, "Rodaje.Module.Echo.exe");

    public static async Task<(NamedPipeServerStream Editor, NamedPipeClientStream Module)> PipePairAsync()
    {
        string name = "Rodaje.Mod.prueba." + Guid.NewGuid().ToString("N");
        NamedPipeServerStream editor = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            ModuleProcess.PipeBufferBytes, ModuleProcess.PipeBufferBytes);
        NamedPipeClientStream module = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await Task.WhenAll(editor.WaitForConnectionAsync(), module.ConnectAsync());
        return (editor, module);
    }

    /// <summary>Un módulo en el mismo proceso con las funciones de prueba.</summary>
    public static ModuleHost EchoHost(int min = 1, int max = 1, int maxConcurrent = 4)
    {
        ModuleHost host = new(EchoInfo, min, max) { MaxConcurrentCalls = maxConcurrent };
        host.Register("eco", (call, _) => ValueTask.FromResult(new ModuleOutput(call.Parameters)));
        host.Register("contar", async (call, ct) =>
        {
            int steps = Int(call.Parameters, "pasos", 10);
            int delay = Int(call.Parameters, "msPorPaso", 5);
            for (int i = 1; i <= steps; i++)
            {
                await Task.Delay(delay, ct);
                await call.ReportProgressAsync((double)i / steps, "contando", ct);
            }
            return new ModuleOutput();
        });
        host.Register("fallar", (_, _) => throw new InvalidOperationException("Fallo a propósito."));
        host.Register("copiar", (call, _) =>
        {
            call.Map(call.Buffers[0]).CopyTo(call.Map(call.Buffers[1]));
            return ValueTask.FromResult(new ModuleOutput(Buffers: [call.Buffers[1]]));
        });
        return host;
    }

    public static async Task<(ModuleClient Client, Task<bool> Host)> ConnectInProcessAsync(ModuleHost? host = null, ModuleClientOptions? options = null)
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await PipePairAsync();
        Task<bool> run = (host ?? EchoHost()).RunAsync(module);
        ModuleClient client = await ModuleClient.ConnectAsync(editor, options);
        return (client, run);
    }

    public static JsonElement Json(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static byte[] Frame(string json) => Frame(Encoding.UTF8.GetBytes(json));

    public static byte[] Frame(byte[] payload)
    {
        byte[] frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Lee un marco crudo del otro lado.</summary>
    public static async Task<string?> ReadFrameAsync(Stream stream)
    {
        byte[] header = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(header);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        byte[] payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        await stream.ReadExactlyAsync(payload);
        return Encoding.UTF8.GetString(payload);
    }

    public const string WelcomeJson =
        """{"type":"welcome","protocol":1,"module":{"id":"hostil","version":"1.0.0","name":"Hostil","functions":[{"id":"eco","name":"Eco"}]}}""";

    /// <summary>Un «módulo» escrito a mano que saluda bien y luego hace lo que diga <paramref name="afterHello"/>.</summary>
    public static async Task<ModuleClient> HostileAsync(Func<Stream, Task> afterHello, string welcome = WelcomeJson, ModuleClientOptions? options = null)
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await PipePairAsync();
        _ = Task.Run(async () =>
        {
            await ReadFrameAsync(module);
            await module.WriteAsync(Frame(welcome));
            await afterHello(module);
        });
        return await ModuleClient.ConnectAsync(editor, options);
    }

    private static int Int(JsonElement? parameters, string name, int fallback) =>
        parameters is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int n) ? n : fallback;
}
