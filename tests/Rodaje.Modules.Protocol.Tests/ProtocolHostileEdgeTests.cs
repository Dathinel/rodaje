using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// Familia 6: Protocolo de módulos:
/// mensajes truncados, JSON inválido, mensaje enorme, campos faltantes, tipos cambiados,
/// respuesta con id que no corresponde, corte de tubería mid-response, cancelación durante petición,
/// y manejo limpio de la sesión según el diseño del protocolo.
/// </summary>
public sealed class ProtocolHostileEdgeTests
{
    private static byte[] Frame(string json) => Support.Frame(json);

    private static byte[] Frame(byte[] bytes) => Support.Frame(bytes);

    private static async Task<(ModuleClient Client, NamedPipeClientStream ModulePipe)> ConnectRawModuleAsync(ModuleClientOptions? options = null)
    {
        (NamedPipeServerStream editorPipe, NamedPipeClientStream modulePipe) = await Support.PipePairAsync();

        Task<ModuleClient> clientTask = ModuleClient.ConnectAsync(editorPipe, options);

        // Leer el hello del editor
        string? hello = await Support.ReadFrameAsync(modulePipe);
        Assert.NotNull(hello);
        Assert.Contains("\"hello\"", hello, StringComparison.Ordinal);

        // Responder con welcome válido
        string welcome = """
        {
          "type": "welcome",
          "protocol": 1,
          "module": {
            "id": "modulo-prueba",
            "version": "1.0.0",
            "name": "Módulo de Prueba",
            "functions": [
              { "id": "eco", "name": "Eco" },
              { "id": "lenta", "name": "Lenta" }
            ]
          }
        }
        """;
        await modulePipe.WriteAsync(Frame(welcome));
        await modulePipe.FlushAsync();

        ModuleClient client = await clientTask;
        return (client, modulePipe);
    }

    [Fact]
    public async Task Protocolo_MensajeTruncado_MarcoCortadoAMitadDeCuerpo_LanzaModuleProtocolException()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            // Escribir un encabezado de 100 bytes pero mandar solo 10 bytes y cerrar la tubería
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, 100);
            await modulePipe.WriteAsync(header);
            await modulePipe.WriteAsync(new byte[10]);
            await modulePipe.FlushAsync();
            modulePipe.Dispose(); // cerrar abruptamente a mitad del mensaje

            var ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => client.Completion);
            Assert.Contains("cortó a mitad de un mensaje", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Protocolo_MensajeTruncado_EncabezadoDeLargoCortado_CierraComoFinDeFlujo()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            // Mandar solo 2 bytes del encabezado de 4 bytes y cerrar
            await modulePipe.WriteAsync(new byte[2]);
            await modulePipe.FlushAsync();
            modulePipe.Dispose();

            // Al cortar a mitad del encabezado de 4 bytes, ReadExactlyOrEndAsync lanza ModuleProtocolException
            var ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => client.Completion);
            Assert.Contains("cortó a mitad de un mensaje", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Protocolo_JsonInvalido_SintaxisRota_CortaConModuleProtocolException()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            // Mensaje que no es JSON válido
            await modulePipe.WriteAsync(Frame("""{"type": "invoke", "id": 1, "function": """));
            await modulePipe.FlushAsync();

            var ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => client.Completion);
            Assert.Contains("malformado", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Protocolo_JsonInvalido_UnicodeInvalidoSustitutoSuelto_LanzaModuleProtocolException()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            // JSON con un sustituto UTF-16 suelto escapado \ud800
            await modulePipe.WriteAsync(Frame("""{"type":"log","level":"info","text":"\ud800"}"""));
            await modulePipe.FlushAsync();

            var ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => client.Completion);
            Assert.Contains("malformado", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Protocolo_MensajeEnorme_SuperaMaxFrameBytes_LanzaModuleProtocolException()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            // Marco de 1 MB + 1 byte
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, ProtocolVersion.MaxFrameBytes + 1);
            await modulePipe.WriteAsync(header);
            await modulePipe.FlushAsync();

            var ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => client.Completion);
            Assert.Contains("el máximo es", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Protocolo_CamposQueFaltan_WelcomeSinModule_CortaAlSaludar()
    {
        (NamedPipeServerStream editorPipe, NamedPipeClientStream modulePipe) = await Support.PipePairAsync();
        await using (editorPipe)
        await using (modulePipe)
        {
            Task<ModuleClient> clientTask = ModuleClient.ConnectAsync(editorPipe);

            string? hello = await Support.ReadFrameAsync(modulePipe);
            Assert.NotNull(hello);

            // Welcome sin campo obligatorio "module"
            await modulePipe.WriteAsync(Frame("""{"type":"welcome","protocol":1}"""));
            await modulePipe.FlushAsync();

            var ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => clientTask);
            Assert.Contains("malformado", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Protocolo_CamposQueFaltan_InvokeSinFunction_LanzaArgumentExceptionEnCliente()
    {
        (ModuleClient client, Task<bool> host) = await Support.ConnectInProcessAsync();
        await using (client)
        {
            // InvokeAsync con function vacía o inválida falla en la validación local del cliente
            await Assert.ThrowsAsync<ArgumentException>(() => client.InvokeAsync(""));
            await Assert.ThrowsAsync<ArgumentException>(() => client.InvokeAsync("ID_CON_MAYUSCULAS"));
        }
        Assert.True(await host);
    }

    [Fact]
    public async Task Protocolo_TiposCambiados_IdEsTextoEnResultMessage_LanzaModuleProtocolException()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            // Módulo envía result donde "id" es string en lugar de int/long
            await modulePipe.WriteAsync(Frame("""{"type":"result","id":"uno","value":null}"""));
            await modulePipe.FlushAsync();

            var ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => client.Completion);
            Assert.Contains("malformado", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Protocolo_TiposCambiados_ProgressFractionEsString_LanzaModuleProtocolException()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            // fraction como string
            await modulePipe.WriteAsync(Frame("""{"type":"progress","id":1,"fraction":"cincuenta"}"""));
            await modulePipe.FlushAsync();

            var ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => client.Completion);
            Assert.Contains("malformado", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Protocolo_RespuestaConIdQueNoCorresponde_EsIgnoradaYSesionSigueViva()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            // El módulo manda un result y un progress para un id=9999 que no existe
            await modulePipe.WriteAsync(Frame("""{"type":"progress","id":9999,"fraction":0.5}"""));
            await modulePipe.WriteAsync(Frame("""{"type":"result","id":9999,"value":null}"""));
            await modulePipe.WriteAsync(Frame("""{"type":"error","code":"internal","message":"fallo","id":9999}"""));
            await modulePipe.FlushAsync();

            // La conexión sigue viva: un ping posterior responde pong sin problemas
            Task pingTask = client.PingAsync();

            // Leer ping del módulo
            string? pingFrame = await Support.ReadFrameAsync(modulePipe);
            Assert.NotNull(pingFrame);
            using var doc = JsonDocument.Parse(pingFrame);
            long pingId = doc.RootElement.GetProperty("id").GetInt64();

            // Responder pong
            await modulePipe.WriteAsync(Frame($$$"""{"type":"pong","id":{{{pingId}}}}"""));
            await modulePipe.FlushAsync();

            await pingTask;
            Assert.False(client.Completion.IsCompleted);
        }
    }

    [Fact]
    public async Task Protocolo_RespuestaConIdNegativoOCero_LanzaModuleProtocolException()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            // Id 0 no es válido en ResultMessage (RequestId exige > 0)
            await modulePipe.WriteAsync(Frame("""{"type":"result","id":0}"""));
            await modulePipe.FlushAsync();

            var ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => client.Completion);
            Assert.Contains("id de llamada inválido", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Protocolo_ModuloCierraTuberiaAMitadDeRespuesta_LanzaModuleDisconnectedException()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            Task<ModuleResult> call = client.InvokeAsync("eco", Support.Json("{}"));

            // Leer el invoke que manda el editor
            string? invokeFrame = await Support.ReadFrameAsync(modulePipe);
            Assert.NotNull(invokeFrame);

            // Cerrar la tubería sin responder al invoke
            modulePipe.Dispose();

            await Assert.ThrowsAnyAsync<ModuleDisconnectedException>(() => call);
            await Assert.ThrowsAnyAsync<ModuleDisconnectedException>(() => client.Completion);
        }
    }

    [Fact]
    public async Task Protocolo_CancelacionDurantePeticion_ModuloConfirma_LanzaOperationCanceledException_SesionSigue()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            using var cts = new CancellationTokenSource();
            Task<ModuleResult> call = client.InvokeAsync("lenta", null, cancellationToken: cts.Token);

            string? invokeFrame = await Support.ReadFrameAsync(modulePipe);
            Assert.NotNull(invokeFrame);
            using var doc = JsonDocument.Parse(invokeFrame);
            long callId = doc.RootElement.GetProperty("id").GetInt64();

            // Cancelar la llamada desde el cliente
            cts.Cancel();

            // El cliente debe enviar un mensaje de cancelación al módulo
            string? cancelFrame = await Support.ReadFrameAsync(modulePipe);
            Assert.NotNull(cancelFrame);
            Assert.Contains("\"cancel\"", cancelFrame, StringComparison.Ordinal);

            // El módulo confirma la cancelación con error code "cancelled"
            await modulePipe.WriteAsync(Frame($$$"""{"type":"error","code":"cancelled","message":"Cancelado","id":{{{callId}}}}"""));
            await modulePipe.FlushAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

            // La conexión NO se cerró y sigue operativa
            Assert.False(client.Completion.IsCompleted);

            // Una llamada posterior puede realizarse normalmente
            Task<ModuleResult> secondCall = client.InvokeAsync("eco", Support.Json("{\"ok\":1}"));
            string? secondInvoke = await Support.ReadFrameAsync(modulePipe);
            Assert.NotNull(secondInvoke);
            using var doc2 = JsonDocument.Parse(secondInvoke);
            long id2 = doc2.RootElement.GetProperty("id").GetInt64();

            await modulePipe.WriteAsync(Frame($$$"""{"type":"result","id":{{{id2}}},"value":{"ok":1}}"""));
            await modulePipe.FlushAsync();

            ModuleResult res = await secondCall;
            Assert.NotNull(res.Value);
        }
    }

    [Fact]
    public async Task Protocolo_CancelacionDurantePeticion_ModuloNoConfirma_EditorCortaConTimeout()
    {
        var options = new ModuleClientOptions
        {
            CancelGrace = TimeSpan.FromMilliseconds(100) // tiempo corto para la prueba
        };

        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync(options);
        await using (client)
        await using (modulePipe)
        {
            using var cts = new CancellationTokenSource();
            Task<ModuleResult> call = client.InvokeAsync("lenta", null, cancellationToken: cts.Token);

            string? invokeFrame = await Support.ReadFrameAsync(modulePipe);
            Assert.NotNull(invokeFrame);

            // Cancelar la petición
            cts.Cancel();

            // El módulo lee el cancel pero NO responde nada
            string? cancelFrame = await Support.ReadFrameAsync(modulePipe);
            Assert.NotNull(cancelFrame);

            // Al vencer CancelGrace (100 ms), el editor corta la conexión por colgado
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            var ex = await Assert.ThrowsAnyAsync<ModuleDisconnectedException>(() => client.Completion);
            Assert.Contains("no confirmó la cancelación", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Protocolo_ModuloEnviaMensajeDesconocidoPeroJsonValido_SeSaltaYSesionSigue()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            // Mensaje de tipo futuro no conocido (identificador válido), pero JSON válido
            await modulePipe.WriteAsync(Frame("""{"type":"mensaje-futuro","propiedad":12345}"""));
            await modulePipe.FlushAsync();

            // La sesión sigue utilizable: responder ping
            Task ping = client.PingAsync();
            string? pingReq = await Support.ReadFrameAsync(modulePipe);
            Assert.NotNull(pingReq);
            using var doc = JsonDocument.Parse(pingReq);
            long pId = doc.RootElement.GetProperty("id").GetInt64();

            await modulePipe.WriteAsync(Frame($$$"""{"type":"pong","id":{{{pId}}}}"""));
            await modulePipe.FlushAsync();

            await ping;
            Assert.False(client.Completion.IsCompleted);
        }
    }

    [Fact]
    public async Task Protocolo_ModuloEnviaMensajeDesconocidoNoJson_CortaConModuleProtocolException()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            // Marco con tipo desconocido pero que tiene basura detrás que no es JSON
            await modulePipe.WriteAsync(Frame("""{"type":"futuro"} basura no json"""));
            await modulePipe.FlushAsync();

            var ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => client.Completion);
            Assert.Contains("malformado", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Protocolo_ModuloEnviaWelcomeConRangoIncompatible_LanzaModuleVersionException()
    {
        (NamedPipeServerStream editorPipe, NamedPipeClientStream modulePipe) = await Support.PipePairAsync();
        await using (editorPipe)
        await using (modulePipe)
        {
            var options = new ModuleClientOptions { MinProtocol = 1, MaxProtocol = 1 };
            Task<ModuleClient> clientTask = ModuleClient.ConnectAsync(editorPipe, options);

            await Support.ReadFrameAsync(modulePipe);

            // Módulo responde con protocolo 5 (incompatible con rango 1..1)
            await modulePipe.WriteAsync(Frame("""
            {
              "type": "welcome",
              "protocol": 5,
              "module": {
                "id": "m",
                "version": "1.0.0",
                "name": "M",
                "functions": []
              }
            }
            """));
            await modulePipe.FlushAsync();

            var ex = await Assert.ThrowsAsync<ModuleVersionException>(() => clientTask);
            Assert.Contains("protocolo 5", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Protocolo_ModuloEnviaBuferFueraDeLasRegiones_CortaConModuleProtocolException()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            using var region = client.CreateRegion(4096);
            Task<ModuleResult> call = client.InvokeAsync("eco", null, buffers: [new SharedBufferRef(region.Name, 0, 1024)]);

            string? invokeFrame = await Support.ReadFrameAsync(modulePipe);
            Assert.NotNull(invokeFrame);
            using var doc = JsonDocument.Parse(invokeFrame);
            long callId = doc.RootElement.GetProperty("id").GetInt64();

            // Módulo devuelve un búfer en una región inexistente "region-fantasma"
            string badResult = $$"""
            {
              "type": "result",
              "id": {{callId}},
              "buffers": [
                { "region": "region-fantasma", "offset": 0, "length": 1024 }
              ]
            }
            """;
            await modulePipe.WriteAsync(Frame(badResult));
            await modulePipe.FlushAsync();

            var ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => call);
            Assert.Contains("fuera de las regiones del editor", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Protocolo_PingEnviadoPorModulo_RechazadoPorEditor()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            // Solo el editor envía pings; si el módulo envía un ping, el editor debe rechazarlo
            await modulePipe.WriteAsync(Frame("""{"type":"ping","id":1}"""));
            await modulePipe.FlushAsync();

            var ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => client.Completion);
            Assert.Contains("El módulo no manda «PingMessage»", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Protocolo_ModuloEnviaLog_SeRecibeEnEventoSinCerrarSesion()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            LogMessage? receivedLog = null;
            client.Log += log => receivedLog = log;

            await modulePipe.WriteAsync(Frame("""{"type":"log","level":"info","text":"Prueba de registro"}"""));
            await modulePipe.FlushAsync();

            // Esperar con ping para garantizar que el log fue despachado
            Task ping = client.PingAsync();
            string? pingReq = await Support.ReadFrameAsync(modulePipe);
            Assert.NotNull(pingReq);
            using var doc = JsonDocument.Parse(pingReq);
            long pId = doc.RootElement.GetProperty("id").GetInt64();

            await modulePipe.WriteAsync(Frame($$$"""{"type":"pong","id":{{{pId}}}}"""));
            await modulePipe.FlushAsync();
            await ping;

            Assert.NotNull(receivedLog);
            Assert.Equal("info", receivedLog.Level);
            Assert.Equal("Prueba de registro", receivedLog.Text);
            Assert.False(client.Completion.IsCompleted);
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(16, 4096)]
    public async Task Protocolo_ModuloDevuelveBuferQueSeSaleDeUnaRegionDelEditor_CortaLaLlamada(long before, long past)
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            using var region = client.CreateRegion(4096);
            Task<ModuleResult> call = client.InvokeAsync("eco", null, buffers: [new SharedBufferRef(region.Name, 0, 16)]);
            using var doc = JsonDocument.Parse((await Support.ReadFrameAsync(modulePipe))!);
            long callId = doc.RootElement.GetProperty("id").GetInt64();

            // La región existe, pero el tramo empieza `before` bytes antes de su final y termina `past` bytes después.
            await modulePipe.WriteAsync(Frame($$"""{"type":"result","id":{{callId}},"buffers":[{"region":"{{region.Name}}","offset":{{region.Capacity - before}},"length":{{before + past}}}]}"""));
            await modulePipe.FlushAsync();

            var ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => call);
            Assert.Contains("fuera de las regiones del editor", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Protocolo_ModuloDevuelveUnaRegionQueExistePeroNoEsDeEsteCliente_CortaLaLlamada()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            using var region = client.CreateRegion(4096);
            using var foreign = SharedMemoryRegion.Create(4096);
            Task<ModuleResult> call = client.InvokeAsync("eco", null, buffers: [new SharedBufferRef(region.Name, 0, 16)]);
            using var doc = JsonDocument.Parse((await Support.ReadFrameAsync(modulePipe))!);
            long callId = doc.RootElement.GetProperty("id").GetInt64();

            await modulePipe.WriteAsync(Frame($$"""{"type":"result","id":{{callId}},"buffers":[{"region":"{{foreign.Name}}","offset":0,"length":16}]}"""));
            await modulePipe.FlushAsync();

            var ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => call);
            Assert.Contains("fuera de las regiones del editor", ex.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("""{"type":"pong","id":1}{"type":"pong","id":2}""")]
    [InlineData("{\"type\":\"log\",\"level\":\"info\",\"text\":\"x\"}\0")]
    [InlineData("""{"type":"result","id":1.0}""")]
    [InlineData("""{"type":"result","id":"1"}""")]
    [InlineData("""{"type":"result","id":9223372036854775808}""")]
    [InlineData("""[{"type":"pong","id":1}]""")]
    public async Task Protocolo_MarcoQueNoEsUnSoloMensajeBienFormado_CortaConMalformado(string frame)
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            await modulePipe.WriteAsync(Frame(frame));
            await modulePipe.FlushAsync();

            var ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => client.Completion);
            Assert.Contains("malformado", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Protocolo_AvalanchaDeTiposDesconocidosYRespuestasSinDueno_NoTumbaLaSesion()
    {
        (ModuleClient client, NamedPipeClientStream modulePipe) = await ConnectRawModuleAsync();
        await using (client)
        await using (modulePipe)
        {
            using var flood = new MemoryStream();
            for (int i = 1; i <= 5000; i++)
            {
                flood.Write(Frame($$"""{"type":"futuro-{{i % 7}}","relleno":"{{new string('x', 200)}}"}"""));
                flood.Write(Frame($$"""{"type":"result","id":{{1_000_000 + i}},"value":{{i}}}"""));
                flood.Write(Frame($$"""{"type":"progress","id":{{2_000_000 + i}},"fraction":0.5}"""));
            }

            await modulePipe.WriteAsync(flood.ToArray());
            await modulePipe.FlushAsync();

            Task ping = client.PingAsync();
            using var doc = JsonDocument.Parse((await Support.ReadFrameAsync(modulePipe))!);
            await modulePipe.WriteAsync(Frame($$"""{"type":"pong","id":{{doc.RootElement.GetProperty("id").GetInt64()}}}"""));
            await modulePipe.FlushAsync();

            await ping;
            Assert.False(client.Completion.IsCompleted);
        }
    }
}
