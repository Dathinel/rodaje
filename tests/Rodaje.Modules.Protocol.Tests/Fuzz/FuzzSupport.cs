using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// Utilidades del fuzz: corre casos con semilla propia (<see cref="BaseSeed"/> + número de caso), con un tope de tiempo
/// por caso y en paralelo, y junta los fallos con su semilla para poder repetir uno solo.
/// </summary>
internal static partial class FuzzSupport
{
    public const int BaseSeed = 20261011;

    /// <summary>Tope de tiempo de cada paso de un caso; pasarlo cuenta como cuelgue.</summary>
    public static readonly TimeSpan CaseCap = TimeSpan.FromSeconds(10);

    /// <summary>Corre <paramref name="count"/> casos con las semillas <c>BaseSeed + first … </c> y falla con la lista de semillas malas.</summary>
    public static async Task RunCasesAsync(int count, Func<int, Task> runCase, int first = 0, int parallel = 8)
    {
        ConcurrentBag<(int Seed, Exception Error)> failures = [];
        using SemaphoreSlim gate = new(parallel);
        List<Task> all = [];
        for (int i = 0; i < count; i++)
        {
            int seed = BaseSeed + first + i;
            await gate.WaitAsync();
            all.Add(Task.Run(async () =>
            {
                try
                {
                    await runCase(seed);
                }
#pragma warning disable CA1031 // Se junta cualquier fallo para informarlo con su semilla.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    failures.Add((seed, ex));
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        await Task.WhenAll(all);
        if (!failures.IsEmpty)
        {
            StringBuilder text = new($"{failures.Count} de {count} casos fallaron. ");
            foreach ((int seed, Exception error) in failures.OrderBy(f => f.Seed).Take(5))
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"[semilla {seed}] {error}\n");
            Assert.Fail(text.ToString());
        }
    }

    /// <summary>Espera <paramref name="task"/> hasta <see cref="CaseCap"/>; si no termina, es un cuelgue.</summary>
    public static async Task<T> Capped<T>(Task<T> task, string what)
    {
        try
        {
            return await task.WaitAsync(CaseCap);
        }
        catch (TimeoutException) when (!task.IsCompleted)
        {
            throw new TimeoutException($"Colgado: {what} no terminó en {CaseCap.TotalSeconds:0} s.");
        }
    }

    public static async Task Capped(Task task, string what)
    {
        try
        {
            await task.WaitAsync(CaseCap);
        }
        catch (TimeoutException) when (!task.IsCompleted)
        {
            throw new TimeoutException($"Colgado: {what} no terminó en {CaseCap.TotalSeconds:0} s.");
        }
    }

    /// <summary>Un marco con el largo correcto.</summary>
    public static byte[] Frame(byte[] payload)
    {
        byte[] frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Lee un marco crudo; <c>null</c> si la conexión terminó o se rompió (al lado de prueba no le importa cómo).</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream)
    {
        try
        {
            byte[] header = new byte[4];
            await stream.ReadExactlyAsync(header);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is <= 0 or > ProtocolVersion.MaxFrameBytes) return null;
            byte[] payload = new byte[length];
            await stream.ReadExactlyAsync(payload);
            return payload;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Lee todo lo que llegue hasta que se corte y lo devuelve; así el otro lado nunca se bloquea al escribir.</summary>
    public static Task<List<byte[]>> DrainAsync(Stream stream) => Task.Run(async () =>
    {
        List<byte[]> frames = [];
        while (await ReadFrameAsync(stream) is { } frame) frames.Add(frame);
        return frames;
    });

    /// <summary>
    /// Si el JSON lleva un sustituto UTF-16 escapado (<c>\ud800</c>…<c>\udfff</c>): es lo que dispara el defecto de
    /// <see cref="ProtocolDefectTests.A_lone_surrogate_in_parameters_is_rejected"/>. Un par bien formado también cuenta;
    /// solo sirve para atribuir un fallo a ese defecto, no para decidir que lo hay.
    /// </summary>
    public static bool HasSurrogateEscape(byte[] json) => SurrogateEscape().IsMatch(Encoding.UTF8.GetString(json));

    [GeneratedRegex(@"\\u[dD][89a-fA-F][0-9a-fA-F]{2}")]
    private static partial Regex SurrogateEscape();

    /// <summary>Escribe sin que importe si el otro lado ya cerró.</summary>
    public static async Task<bool> TryWriteAsync(Stream stream, byte[] data)
    {
        try
        {
            await stream.WriteAsync(data);
            await stream.FlushAsync();
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>Omite la prueba fuera de Windows: la memoria compartida con nombre no existe en Linux («Named maps are not supported»).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Solo Windows: la memoria compartida con nombre (MemoryMappedFile) no existe en Linux.";
    }
}
