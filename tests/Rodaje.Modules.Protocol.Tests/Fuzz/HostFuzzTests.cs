using System.IO.Pipes;
using System.Text;
using Xunit.Abstractions;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// Un «editor» hostil escrito a mano contra el <see cref="ModuleHost"/> real (el de las funciones de prueba de
/// <see cref="Support.EchoHost"/>), por una tubería real. Saluda con versiones raras o con basura, manda llamadas con ids
/// repetidos, cancelaciones y pings con ids al azar, búferes de regiones que no existen, JSON roto y largos falsos, y
/// termina cerrando, pidiendo <c>shutdown</c> o mandando basura. Propiedades:
/// <list type="bullet">
/// <item><c>RunAsync</c> termina antes de <see cref="FuzzSupport.CaseCap"/>, devuelve un <c>bool</c> y no lanza.</item>
/// <item>Todo lo que el módulo escribe es un mensaje válido del protocolo para el editor.</item>
/// </list>
/// Dos excepciones de <c>RunAsync</c> son defectos conocidos y aquí se cuentan y registran en vez de fallar: una
/// <see cref="ObjectDisposedException"/> de <c>CancellationTokenSource</c> al cerrar
/// (<see cref="ProtocolDefectTests.Shutdown_with_calls_finishing_ends_the_host_without_throwing"/>) y una
/// <see cref="System.Text.Json.JsonException"/> al no poder escribir la respuesta de <c>eco</c> a unos parámetros con un
/// sustituto UTF-16 suelto, solo si el caso mandó uno (<see cref="ProtocolDefectTests.A_lone_surrogate_in_parameters_is_rejected"/>).
/// Ninguna otra excepción se da por buena.
/// </summary>
public sealed class HostFuzzTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_hostile_editor_never_hangs_or_crashes_the_host()
    {
        Tally tally = new();
        await FuzzSupport.RunCasesAsync(300, seed => RunCaseAsync(seed, tally));
        output.WriteLine(tally.ToString());
        Assert.True(tally.Clean > 0 && tally.Refused > 0 && tally.Replies > 0, tally.ToString());
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public async Task A_hostile_editor_never_hangs_or_crashes_the_host_long_run()
    {
        Tally tally = new();
        await FuzzSupport.RunCasesAsync(5000, seed => RunCaseAsync(seed, tally), first: 2_000_000);
        output.WriteLine(tally.ToString());
    }

    private static async Task RunCaseAsync(int seed, Tally tally)
    {
        Random random = new(seed);
        FuzzFrames frames = new(random, maxId: 8);
        int min = random.Next(1, 3), max = min + random.Next(0, 2);
        ModuleHost host = new(Support.EchoInfo, min, max) { MaxConcurrentCalls = random.Next(1, 6), HandshakeTimeout = TimeSpan.FromMilliseconds(300) };
        ModuleHost echo = Support.EchoHost(min, max, host.MaxConcurrentCalls);
        host = random.Next(2) == 0 ? echo : host; // Con funciones registradas o sin ninguna.

        byte[] hello = random.Next(5) switch
        {
            0 => frames.Frame(FuzzDirection.ToModule),
            1 => Support.Frame($$"""{"type":"hello","minProtocol":{{random.Next(-1, 4)}},"maxProtocol":{{random.Next(-1, 5)}},"app":"rodaje","appVersion":"1"}"""),
            _ => Support.Frame("""{"type":"hello","minProtocol":1,"maxProtocol":2,"app":"rodaje","appVersion":"1"}"""),
        };
        bool mild = random.Next(3) != 0;
        int count = random.Next(0, 20);
        long nextId = 0;
        byte[][] script = [.. Enumerable.Range(0, count).Select(_ => mild && random.Next(15) != 0 ? MildFrame(frames, ref nextId) : frames.Frame(FuzzDirection.ToModule))];
        int[] delays = [.. Enumerable.Range(0, count).Select(_ => random.Next(5) == 0 ? random.Next(1, 20) : 0)];
        Ending ending = (Ending)random.Next(4);
        byte[] tail = frames.Frame(FuzzDirection.ToModule);

        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        Task<bool> run = host.RunAsync(module);
        Task<List<byte[]>> drain = FuzzSupport.DrainAsync(editor);
        try
        {
            await FuzzSupport.TryWriteAsync(editor, hello);
            for (int i = 0; i < script.Length; i++)
            {
                if (delays[i] > 0) await Task.Delay(delays[i]);
                if (!await FuzzSupport.TryWriteAsync(editor, script[i])) break;
            }
            switch (ending)
            {
                case Ending.Shutdown:
                    await FuzzSupport.TryWriteAsync(editor, Support.Frame("""{"type":"shutdown"}"""));
                    break;
                case Ending.Garbage:
                    await FuzzSupport.TryWriteAsync(editor, tail);
                    break;
                case Ending.LingerThenClose:
                    await Task.Delay(random.Next(1, 60));
                    break;
            }
            if (ending != Ending.Shutdown) editor.Dispose();

            try
            {
                bool clean = await FuzzSupport.Capped(run, "ModuleHost.RunAsync");
                if (clean) Interlocked.Increment(ref tally.Clean);
                else Interlocked.Increment(ref tally.Refused);
            }
            catch (ObjectDisposedException ex) when (ex.ObjectName?.Contains("CancellationTokenSource", StringComparison.Ordinal) == true
                || ex.Message.Contains("CancellationTokenSource", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref tally.KnownDefect);
            }
            catch (System.Text.Json.JsonException ex) when (ex.InnerException is InvalidOperationException && script.Any(FuzzSupport.HasSurrogateEscape))
            {
                Interlocked.Increment(ref tally.KnownSurrogate);
            }
        }
        finally
        {
            editor.Dispose();
            module.Dispose();
        }

        List<byte[]> replies = await FuzzSupport.Capped(drain, "la lectura del editor");
        foreach (byte[] reply in replies)
        {
            ModuleMessage? message;
            try
            {
                message = MessageChannel.Parse(reply);
            }
            catch (ModuleProtocolException ex)
            {
                throw new InvalidOperationException($"El módulo mandó un mensaje inválido: {Encoding.UTF8.GetString(reply)}", ex);
            }
            Assert.True(message is WelcomeMessage or ResultMessage or ErrorMessage or ProgressMessage or PongMessage or LogMessage,
                $"El módulo no manda {message?.GetType().Name}.");
            Interlocked.Increment(ref tally.Replies);
        }
    }

    /// <summary>Un mensaje aceptable para el módulo; a menudo una llamada a una función que existe.</summary>
    private static byte[] MildFrame(FuzzFrames frames, ref long nextId)
    {
        Random random = frames.Random;
        int pick = random.Next(10);
        if (pick < 1) return frames.ValidFrame(FuzzDirection.ToModule);
        if (pick < 4) return Support.Frame($$"""{"type":"{{(random.Next(3) == 0 ? "ping" : "cancel")}}","id":{{random.NextInt64(1, nextId + 2)}}}""");
        string function = random.Next(4) switch
        {
            0 => "eco",
            1 => "contar",
            2 => "fallar",
            _ => "copiar",
        };
        string parameters = function == "contar"
            ? $$"""{"pasos":{{random.Next(-2, 40)}},"msPorPaso":{{random.Next(-2, 8)}}}"""
            : random.Next(3) == 0 ? "\"hola\"" : "null";
        string buffers = random.Next(6) == 0 ? ",\"buffers\":[" + frames.Buffer("\"Rodaje.Mod.ajena\"") + "]" : "";
        // Ids nuevos casi siempre; a veces uno repetido (si sigue en curso, el módulo corta: es lo que dice el protocolo).
        long id = random.Next(12) == 0 && nextId > 0 ? random.NextInt64(1, nextId + 1) : ++nextId;
        return Support.Frame($$"""{"type":"invoke","id":{{id}},"function":"{{function}}","parameters":{{parameters}}{{buffers}}}""");
    }

    private enum Ending
    {
        Close,
        Shutdown,
        Garbage,
        LingerThenClose,
    }

    private sealed class Tally
    {
#pragma warning disable CA1051 // Contadores para Interlocked.
        public int Clean;
        public int Refused;
        public int Replies;
        public int KnownDefect;
        public int KnownSurrogate;
#pragma warning restore CA1051

        public override string ToString() =>
            $"terminó bien {Clean}, cortó por protocolo o versión {Refused}, mensajes del módulo {Replies}, defectos conocidos: ObjectDisposedException al cerrar {KnownDefect}, JsonException por un sustituto suelto {KnownSurrogate}";
    }
}
