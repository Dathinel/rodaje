using System.Text.Json;
using Xunit.Abstractions;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// Fuzz del marco y del JSON sin tubería: <see cref="MessageChannel.Parse"/> y <see cref="MessageChannel.ReceiveAsync"/>
/// solo pueden devolver un mensaje válido, saltar un tipo desconocido o lanzar <see cref="ModuleProtocolException"/>.
/// Lo que aceptan vuelve a pasar igual por el cable. Semillas fijas (<see cref="FuzzSupport.BaseSeed"/>).
/// </summary>
public sealed class MessageFuzzTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(FuzzDirection.ToEditor)]
    [InlineData(FuzzDirection.ToModule)]
    internal void Hostile_payloads_parse_or_fail_with_a_protocol_error(FuzzDirection direction)
    {
        const int cases = 20_000;
        FuzzFrames frames = new(new Random(FuzzSupport.BaseSeed + (int)direction));
        int accepted = 0, skipped = 0, rejected = 0, loneSurrogates = 0;
        for (int i = 0; i < cases; i++)
        {
            byte[] payload = frames.Payload(direction);
            ModuleMessage? message;
            try
            {
                message = MessageChannel.Parse(payload);
            }
            catch (ModuleProtocolException)
            {
                rejected++;
                continue;
            }
            catch (Exception ex)
            {
                Assert.Fail($"Caso {i}: {ex.GetType().Name} con {Show(payload)}\n{ex}");
                throw;
            }
            if (message is null)
            {
                skipped++;
                continue;
            }
            accepted++;
            Assert.Null(message.Validate());
            // Lo aceptado se vuelve a escribir y se lee igual. Salvo el defecto conocido: un sustituto suelto dentro de un
            // JsonElement (ProtocolDefectTests.A_lone_surrogate_in_parameters_is_rejected), que se cuenta aparte.
            byte[] again;
            try
            {
                again = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJsonContext.Default.ModuleMessage);
            }
            catch (JsonException ex) when (message is InvokeMessage or ResultMessage && ex.InnerException is InvalidOperationException
                && FuzzSupport.HasSurrogateEscape(payload))
            {
                loneSurrogates++;
                continue;
            }
            ModuleMessage? back = MessageChannel.Parse(again);
            Assert.NotNull(back);
            Assert.Equal(message.GetType(), back.GetType());
            Assert.Equal(again, JsonSerializer.SerializeToUtf8Bytes(back, ProtocolJsonContext.Default.ModuleMessage));
        }
        output.WriteLine($"{direction}: {accepted} aceptados ({loneSurrogates} con un sustituto suelto que no se puede reescribir), {skipped} tipos desconocidos saltados, {rejected} rechazados.");
        // Que el generador no se vuelva inútil: tiene que haber de todo.
        Assert.True(accepted > cases / 10, $"solo {accepted} aceptados");
        Assert.True(rejected > cases / 10, $"solo {rejected} rechazados");
        Assert.True(skipped > 0);
    }

    [Fact]
    public async Task Hostile_streams_end_or_fail_with_a_protocol_error()
    {
        const int streams = 2_000;
        FuzzFrames frames = new(new Random(FuzzSupport.BaseSeed + 2));
        int clean = 0, broken = 0, messages = 0;
        for (int i = 0; i < streams; i++)
        {
            List<byte> data = [];
            for (int k = frames.Random.Next(1, 8); k > 0; k--)
                data.AddRange(frames.Frame(i % 2 == 0 ? FuzzDirection.ToEditor : FuzzDirection.ToModule));
            if (frames.Random.Next(4) == 0) data.RemoveRange(frames.Random.Next(data.Count), 1);
            using MessageChannel reader = new(new MemoryStream([.. data]));
            try
            {
                while (await reader.ReceiveAsync().AsTask().WaitAsync(FuzzSupport.CaseCap) is not null) messages++;
                clean++;
            }
            catch (ModuleProtocolException)
            {
                broken++;
            }
            catch (Exception ex)
            {
                Assert.Fail($"Flujo {i}: {ex.GetType().Name}\n{ex}");
            }
        }
        output.WriteLine($"{clean} flujos terminaron limpios, {broken} con error de protocolo, {messages} mensajes leídos.");
        Assert.True(clean > 0 && broken > 0 && messages > 0);
    }

    private static string Show(byte[] payload) =>
        payload.Length > 300 ? $"{payload.Length} bytes: {Convert.ToHexString(payload, 0, 150)}…" : Convert.ToHexString(payload);
}
