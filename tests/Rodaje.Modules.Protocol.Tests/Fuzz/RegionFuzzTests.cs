using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// Fuzz de los búferes en memoria compartida, con regiones de verdad (solo Windows: en Linux no hay
/// <c>MemoryMappedFile</c> con nombre). Las capacidades son múltiplos de 4096 para que la región que abre el módulo (que
/// toma el tamaño de la vista, redondeado a página) mida lo mismo que la que creó el editor.
/// </summary>
public sealed class RegionFuzzTests
{
    private const int Cases = 200;

    /// <summary>
    /// Un módulo hostil devuelve un búfer al azar en un <c>result</c>: el cliente lo acepta solo si está entero dentro de
    /// una región suya; si no, corta con <see cref="ModuleProtocolException"/>. Nunca otra excepción ni un cuelgue.
    /// </summary>
    [WindowsFact]
    public async Task Result_buffers_are_accepted_only_inside_the_editor_regions()
    {
        await FuzzSupport.RunCasesAsync(Cases, async seed =>
        {
            Random random = new(seed);
            long capacity = 4096L * random.Next(1, 5);
            (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
            TaskCompletionSource<string> regionName = new(TaskCreationOptions.RunContinuationsAsynchronously);
            (long offset, long length) = Range(random, capacity);
            int nameKind = random.Next(6);
            Task moduleSide = Task.Run(async () =>
            {
                if (await FuzzSupport.ReadFrameAsync(module) is null) return;
                if (!await FuzzSupport.TryWriteAsync(module, Support.Frame(Support.WelcomeJson))) return;
                string real = await regionName.Task;
                string region = nameKind switch
                {
                    0 => "Rodaje.Mod.ajena",
                    1 => real + "x",
                    _ => real,
                };
                if (await FuzzSupport.ReadFrameAsync(module) is not { } invoke) return;
                long id = JsonDocument.Parse(invoke).RootElement.GetProperty("id").GetInt64();
                string offsetText = offset.ToString(CultureInfo.InvariantCulture), lengthText = length.ToString(CultureInfo.InvariantCulture);
                await FuzzSupport.TryWriteAsync(module, Support.Frame(
                    $$"""{"type":"result","id":{{id}},"buffers":[{"region":"{{region}}","offset":{{offsetText}},"length":{{lengthText}}}]}"""));
                await FuzzSupport.DrainAsync(module);
            });
            try
            {
                ModuleClient client = await FuzzSupport.Capped(ModuleClient.ConnectAsync(editor), "ConnectAsync");
                await using (client)
                {
                    SharedMemoryRegion region = client.CreateRegion(capacity);
                    regionName.SetResult(region.Name);
                    bool inside = nameKind >= 2 && offset >= 0 && length > 0 && length <= int.MaxValue && offset <= capacity - length;
                    Task<ModuleResult> call = client.InvokeAsync("eco");
                    if (inside)
                    {
                        ModuleResult result = await FuzzSupport.Capped(call, "la llamada");
                        SharedBufferRef buffer = Assert.Single(result.Buffers);
                        Assert.Equal(length, region.GetSpan(buffer).Length);
                    }
                    else
                    {
                        await Assert.ThrowsAsync<ModuleProtocolException>(() => FuzzSupport.Capped(call, "la llamada"));
                    }
                }
            }
            finally
            {
                editor.Dispose();
                module.Dispose();
                await FuzzSupport.Capped(moduleSide, "el módulo hostil");
            }
        });
    }

    /// <summary>
    /// El editor pasa a <c>copiar</c> dos búferes al azar de una región real: el módulo copia si los dos caben y el
    /// destino es lo bastante grande; si un búfer se sale de la región, responde <c>buffer-out-of-range</c>; si el destino
    /// es corto, <c>internal</c>. La conexión sigue sirviendo después.
    /// </summary>
    [WindowsFact]
    public async Task Copy_with_random_buffers_is_exact_or_refused_with_the_right_code()
    {
        (ModuleClient client, Task<bool> host) = await Support.ConnectInProcessAsync();
        await using (client)
        {
            Random random = new(FuzzSupport.BaseSeed);
            for (int i = 0; i < Cases; i++)
            {
                long capacity = 4096L * random.Next(1, 5);
                SharedMemoryRegion region = client.CreateRegion(capacity);
                (long fromOffset, long fromLength) = ValidRange(random, capacity);
                (long toOffset, long toLength) = ValidRange(random, capacity);
                SharedBufferRef from = new(region.Name, fromOffset, fromLength);
                SharedBufferRef to = new(region.Name, toOffset, toLength);
                bool fromInside = fromOffset <= capacity - fromLength, toInside = toOffset <= capacity - toLength;
                Task<ModuleResult> call = client.InvokeAsync("copiar", buffers: [from, to]);
                string context = $"caso {i}: región {capacity}, de {fromOffset}+{fromLength} a {toOffset}+{toLength}";
                if (!fromInside || !toInside)
                {
                    ModuleCallException ex = await Assert.ThrowsAsync<ModuleCallException>(() => FuzzSupport.Capped(call, context));
                    Assert.True(ex.Code == ErrorCodes.BufferOutOfRange, context + ": " + ex.Code);
                }
                else if (fromLength > toLength)
                {
                    ModuleCallException ex = await Assert.ThrowsAsync<ModuleCallException>(() => FuzzSupport.Capped(call, context));
                    Assert.True(ex.Code == ErrorCodes.Internal, context + ": " + ex.Code);
                }
                else
                {
                    Assert.Equal(to, Assert.Single((await FuzzSupport.Capped(call, context)).Buffers));
                }
                client.ReleaseRegion(region);
            }
            Assert.Equal("1", (await client.InvokeAsync("eco", Support.Json("1"))).Value!.Value.GetRawText());
        }
        Assert.True(await FuzzSupport.Capped(host, "RunAsync"));
    }

    /// <summary>Desplazamiento y largo cualesquiera: dentro, en el borde, fuera o con valores que desbordan.</summary>
    private static (long Offset, long Length) Range(Random random, long capacity) => random.Next(5) switch
    {
        0 => (random.NextInt64(0, capacity), 1),
        1 => (0, capacity),
        2 => (random.NextInt64(0, capacity + 64), random.NextInt64(1, capacity + 64)),
        3 => (capacity - random.Next(0, 16), random.Next(1, 32)),
        _ => (random.Next(3) switch { 0 => long.MaxValue - 1, 1 => 9223372036854775000, _ => 0 }, random.Next(2) == 0 ? int.MaxValue : 1),
    };

    /// <summary>Un rango que pasa la validación del mensaje (dentro o fuera de la región).</summary>
    private static (long Offset, long Length) ValidRange(Random random, long capacity) => random.Next(4) switch
    {
        0 => (0, capacity),
        1 => (capacity - random.Next(0, 16), random.Next(1, 32)),
        _ => (random.NextInt64(0, capacity), random.NextInt64(1, capacity / 2 + 1)),
    };
}
