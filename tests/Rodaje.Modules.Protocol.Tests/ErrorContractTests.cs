using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// Las promesas de la sección «Errores» de <c>docs/protocolo-modulos.md</c>, una prueba por frase (tarea 353). Las que ya
/// tienen su prueba nombrada en la guía no se repiten.
/// </summary>
public sealed class ErrorContractTests
{
    private const string Hello = """{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"1"}""";

    /// <summary>Un anfitrión en el mismo proceso y el lado del editor en crudo, ya saludado.</summary>
    private static async Task<(NamedPipeServerStream Editor, Task<bool> Host)> RawHostAsync(ModuleHost? host = null)
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        Task<bool> run = (host ?? Support.EchoHost()).RunAsync(module);
        await editor.WriteAsync(Support.Frame(Hello));
        Assert.Contains("\"welcome\"", await Support.ReadFrameAsync(editor), StringComparison.Ordinal);
        return (editor, run);
    }

    private static async Task<string?> InvokeRawAsync(Stream editor, long id, string function)
    {
        await editor.WriteAsync(Support.Frame($$"""{"type":"invoke","id":{{id}},"function":"{{function}}"}"""));
        return await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound);
    }

    private static ModuleHost HostThrowing(Exception error)
    {
        ModuleHost host = new(Support.EchoInfo);
        host.Register("fallar", (_, _) => throw error);
        host.Register("eco", (call, _) => ValueTask.FromResult(new ModuleOutput(call.Parameters)));
        return host;
    }

    private static async Task<ModuleCallException> CallFailsAsync(ModuleHost host)
    {
        (ModuleClient client, _) = await Support.ConnectInProcessAsync(host);
        await using (client)
        {
            ModuleCallException error = await Assert.ThrowsAsync<ModuleCallException>(() => client.InvokeAsync("fallar").WaitAsync(Support.CiBound));
            await client.InvokeAsync("eco").WaitAsync(Support.CiBound);
            return error;
        }
    }

    // «Marcos reales: {"type":"error","code":"unknown-function","message":"El m\u00F3dulo no tiene la funci\u00F3n \u00ABnada\u00BB.","id":1}»
    [Fact]
    public async Task Unknown_function_frame_matches_the_guide()
    {
        (NamedPipeServerStream editor, _) = await RawHostAsync();
        await using (editor)
            Assert.Equal("""{"type":"error","code":"unknown-function","message":"El m\u00F3dulo no tiene la funci\u00F3n \u00ABnada\u00BB.","id":1}""",
                await InvokeRawAsync(editor, 1, "nada"));
    }

    // «{"type":"error","code":"internal","message":"InvalidOperationException: Fallo a prop\u00F3sito.","id":2}»
    [Fact]
    public async Task Internal_frame_matches_the_guide()
    {
        (NamedPipeServerStream editor, _) = await RawHostAsync();
        await using (editor)
            Assert.Equal("""{"type":"error","code":"internal","message":"InvalidOperationException: Fallo a prop\u00F3sito.","id":2}""",
                await InvokeRawAsync(editor, 2, "fallar"));
    }

    // «`busy`: El módulo, si ya atiende el máximo de llamadas a la vez (4 en el anfitrión de C#).»
    // «{"type":"error","code":"busy","message":"El m\u00F3dulo est\u00E1 ocupado.","id":2}»
    [Fact]
    public async Task Busy_frame_matches_the_guide_and_the_default_limit_is_four()
    {
        Assert.Equal(4, new ModuleHost(Support.EchoInfo).MaxConcurrentCalls);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ModuleHost host = new(Support.EchoInfo) { MaxConcurrentCalls = 1 };
        host.Register("contar", async (_, _) =>
        {
            await release.Task;
            return new ModuleOutput();
        });
        host.Register("eco", (call, _) => ValueTask.FromResult(new ModuleOutput(call.Parameters)));
        (NamedPipeServerStream editor, _) = await RawHostAsync(host);
        await using (editor)
        {
            await editor.WriteAsync(Support.Frame("""{"type":"invoke","id":1,"function":"contar"}"""));
            Assert.Equal("""{"type":"error","code":"busy","message":"El m\u00F3dulo est\u00E1 ocupado.","id":2}""", await InvokeRawAsync(editor, 2, "eco"));
            release.SetResult();
            Assert.Equal("""{"type":"result","id":1}""", await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound));
        }
    }

    // «`out-of-memory`: El anfitrión de C#, si la función lanza `OutOfMemoryException` o `InsufficientExecutionStackException`.»
    [Fact]
    public async Task Out_of_memory_and_insufficient_stack_are_out_of_memory()
    {
#pragma warning disable CA2201 // Se lanza a propósito para ver cómo la contesta el anfitrión.
        Assert.Equal(ErrorCodes.OutOfMemory, (await CallFailsAsync(HostThrowing(new OutOfMemoryException("Sin memoria.")))).Code);
#pragma warning restore CA2201
        Assert.Equal(ErrorCodes.OutOfMemory, (await CallFailsAsync(HostThrowing(new InsufficientExecutionStackException("Sin pila.")))).Code);
    }

    // «El anfitrión de C# manda «`Tipo`: mensaje» de la excepción, «Resultado inválido: …»»
    [Fact]
    public async Task An_invalid_result_is_answered_as_internal()
    {
        ModuleHost host = new(Support.EchoInfo);
        host.Register("fallar", (_, _) => ValueTask.FromResult(new ModuleOutput(Buffers: [new SharedBufferRef("", 0, 16)])));
        host.Register("eco", (call, _) => ValueTask.FromResult(new ModuleOutput(call.Parameters)));
        ModuleCallException error = await CallFailsAsync(host);
        Assert.Equal(ErrorCodes.Internal, error.Code);
        Assert.StartsWith("Resultado inválido: ", error.Message, StringComparison.Ordinal);
    }

    // «En un módulo de C#, lanza `ModuleCallException(código, mensaje)` (o cualquier `ModuleException`) y el anfitrión
    // manda ese código y ese mensaje.»
    [Fact]
    public async Task A_module_exception_travels_with_its_code_and_message()
    {
        ModuleCallException call = await CallFailsAsync(HostThrowing(new ModuleCallException(ErrorCodes.InvalidParameters, "Falta «x».")));
        Assert.Equal((ErrorCodes.InvalidParameters, "Falta «x»."), (call.Code, call.Message));
        ModuleCallException any = await CallFailsAsync(HostThrowing(new ModuleException("codigo-propio", "Otro fallo.")));
        Assert.Equal(("codigo-propio", "Otro fallo."), (any.Code, any.Message));
    }

    // «`message` es un texto de hasta 4096 caracteres (el anfitrión de C# recorta los más largos con «…»).»
    [Fact]
    public async Task Long_messages_are_trimmed_to_4096_with_an_ellipsis()
    {
        ModuleCallException error = await CallFailsAsync(HostThrowing(new ModuleCallException("largo", new string('a', 5000))));
        Assert.Equal(4096, error.Message.Length);
        Assert.Equal(new string('a', 4095) + "…", error.Message);
    }

    // La misma promesa con el corte en medio de un par sustituto: la llamada recibe igual su mensaje recortado (el
    // sustituto suelto sale como U+FFFD), no se queda sin respuesta.
    [Fact]
    public async Task Trimming_inside_a_surrogate_pair_still_answers_the_call()
    {
        string message = new string('a', 4094) + string.Concat(Enumerable.Repeat("🎬", 10));
        ModuleCallException error = await CallFailsAsync(HostThrowing(new ModuleCallException("largo", message)));
        Assert.Equal("largo", error.Code);
        Assert.Equal(4096, error.Message.Length);
        Assert.StartsWith(new string('a', 4094), error.Message, StringComparison.Ordinal);
        Assert.EndsWith("…", error.Message, StringComparison.Ordinal);
    }

    // «`buffer-out-of-range`: Un búfer que no se puede usar: región con nombre inválido o que no existe […] (`SharedMemoryRegion.cs`).»
    [Theory]
    [InlineData("")]
    [InlineData("con espacio")]
    [InlineData("Local\\otra")]
    public void A_region_with_an_invalid_name_is_buffer_out_of_range(string name)
    {
        ModuleCallException error = Assert.Throws<ModuleCallException>(() => SharedMemoryRegion.Open(name));
        Assert.Equal(ErrorCodes.BufferOutOfRange, error.Code);
    }

    // «`code` solo tiene que ser un identificador válido: el editor acepta códigos fuera de la tabla y los pasa tal cual»
    // «cualquier otro código, en `ModuleCallException` con `Code` y `Message`.»
    [Fact]
    public async Task The_editor_passes_unknown_codes_through_and_keeps_the_connection()
    {
        ModuleClient client = await Support.HostileAsync(async module =>
        {
            for (int i = 0; i < 2; i++)
            {
                long id = JsonDocument.Parse((await Support.ReadFrameAsync(module))!).RootElement.GetProperty("id").GetInt64();
                await module.WriteAsync(Support.Frame(i == 0
                    ? $$"""{"type":"error","code":"codigo-del-futuro","message":"Algo nuevo.","id":{{id}}}"""
                    : $$"""{"type":"result","id":{{id}},"value":7}"""));
            }
        });
        await using (client)
        {
            ModuleCallException error = await Assert.ThrowsAsync<ModuleCallException>(() => client.InvokeAsync("eco").WaitAsync(Support.CiBound));
            Assert.Equal(("codigo-del-futuro", "Algo nuevo."), (error.Code, error.Message));
            Assert.Equal(7, (await client.InvokeAsync("eco").WaitAsync(Support.CiBound)).Value!.Value.GetInt32());
        }
    }

    // «Del lado del editor (`ModuleClient.Dispatch`, `ModuleExceptions.cs`): `cancelled` se convierte en `OperationCanceledException`»
    [Fact]
    public async Task Cancelled_from_the_module_is_an_operation_canceled_exception()
    {
        ModuleClient client = await Support.HostileAsync(async module =>
        {
            long id = JsonDocument.Parse((await Support.ReadFrameAsync(module))!).RootElement.GetProperty("id").GetInt64();
            await module.WriteAsync(Support.Frame($$"""{"type":"error","code":"cancelled","message":"Cancelado.","id":{{id}}}"""));
        });
        await using (client)
        {
            Exception error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.InvokeAsync("eco").WaitAsync(Support.CiBound));
            Assert.IsNotAssignableFrom<ModuleException>(error);
        }
    }

    // «Un `error` **sin `id`** después del saludo («El módulo informó un error de conexión: …»).»
    // «ante cualquiera de estos casos corta la conexión, falla todas las llamadas en curso con una `ModuleException`»
    [Fact]
    public async Task An_error_without_id_closes_the_connection_with_its_message()
    {
        ModuleClient client = await Support.HostileAsync(async module =>
        {
            await Support.ReadFrameAsync(module);
            await Support.ReadFrameAsync(module);
            await module.WriteAsync(Support.Frame("""{"type":"error","code":"internal","message":"Se rompió."}"""));
        });
        await using (client)
        {
            Task first = client.InvokeAsync("eco");
            Task second = client.InvokeAsync("eco");
            ModuleProtocolException error = await Assert.ThrowsAsync<ModuleProtocolException>(() => first.WaitAsync(Support.CiBound));
            Assert.Equal("El módulo informó un error de conexión: Se rompió.", error.Message);
            await Assert.ThrowsAnyAsync<ModuleException>(() => second.WaitAsync(Support.CiBound));
            await Assert.ThrowsAnyAsync<ModuleException>(() => client.Completion);
        }
    }

    public static TheoryData<string> ForbiddenFromTheModule() =>
    [
        """{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"x","appVersion":"1"}""",
        """{"type":"invoke","id":1,"function":"eco"}""",
        """{"type":"cancel","id":1}""",
        """{"type":"ping","id":1}""",
        """{"type":"shutdown"}""",
        Support.WelcomeJson,
        """{"type":"Result","id":1}""",
        """{"type":"result","id":"1"}""",
    ];

    // «Un mensaje que el módulo no puede mandar (`hello`, `invoke`, `cancel`, `ping`, `shutdown`, o un `welcome` después
    // del saludo).» «Un `type` que no es un identificador válido.» «tipos equivocados (un `id` como texto…)»
    [Theory]
    [MemberData(nameof(ForbiddenFromTheModule))]
    public async Task A_message_the_module_cannot_send_closes_the_connection(string json)
    {
        ModuleClient client = await Support.HostileAsync(async module =>
        {
            await Support.ReadFrameAsync(module);
            await module.WriteAsync(Support.Frame(json));
        });
        await using (client)
        {
            await Assert.ThrowsAsync<ModuleProtocolException>(() => client.InvokeAsync("eco").WaitAsync(Support.CiBound));
            await Assert.ThrowsAsync<ModuleProtocolException>(() => client.Completion.WaitAsync(Support.CiBound));
        }
    }

    // «| `ModuleProtocolException` | `malformed` |» … «| `ModuleExitedException` | `disconnected` | … trae `ExitCode`.»
    // «`disconnected` es solo el código de esas dos excepciones del editor; no está en `ErrorCodes` y no viaja por el cable.»
    [Fact]
    public void Editor_exceptions_carry_the_codes_of_the_table()
    {
        Assert.Equal(ErrorCodes.Malformed, new ModuleProtocolException().Code);
        Assert.Equal(ErrorCodes.VersionIncompatible, new ModuleVersionException().Code);
        Assert.Equal("disconnected", new ModuleDisconnectedException().Code);
        ModuleExitedException exited = new(3, null);
        Assert.Equal(("disconnected", 3), (exited.Code, exited.ExitCode));
        Assert.IsAssignableFrom<ModuleDisconnectedException>(exited);
        Assert.Equal(ErrorCodes.Busy, new ModuleCallException(ErrorCodes.Busy, "x").Code);
        string[] codes = [.. typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetValue(null)!)];
        Assert.Equal(9, codes.Length);
        Assert.DoesNotContain("disconnected", codes);
    }

    // «`version-incompatible`: El módulo, en el saludo y sin `id`, si no hay versión común.»
    [Fact]
    public async Task Version_incompatible_is_sent_without_id_in_the_handshake()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        await using (editor)
        {
            Task<bool> host = Support.EchoHost().RunAsync(module);
            await editor.WriteAsync(Support.Frame("""{"type":"hello","minProtocol":2,"maxProtocol":2,"app":"rodaje","appVersion":"1"}"""));
            using JsonDocument reply = JsonDocument.Parse((await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound))!);
            Assert.Equal("error", reply.RootElement.GetProperty("type").GetString());
            Assert.Equal(ErrorCodes.VersionIncompatible, reply.RootElement.GetProperty("code").GetString());
            Assert.False(reply.RootElement.TryGetProperty("id", out _));
            Assert.False(await host.WaitAsync(Support.CiBound));
        }
    }

    // «un mensaje inválido le hace mandar un `error` `malformed` sin `id` y salir con el código 2» «Marco real, tras un
    // `invoke` con `"id":0`: {"type":"error","code":"malformed","message":"Mensaje \u00ABInvokeMessage\u00BB inv\u00E1lido: id de llamada inv\u00E1lido."}»
    [Fact]
    public async Task Malformed_frame_after_an_invoke_with_id_zero_matches_the_guide()
    {
        (NamedPipeServerStream editor, Task<bool> host) = await RawHostAsync();
        await using (editor)
        {
            Assert.Equal("""{"type":"error","code":"malformed","message":"Mensaje \u00ABInvokeMessage\u00BB inv\u00E1lido: id de llamada inv\u00E1lido."}""",
                await InvokeRawAsync(editor, 0, "eco"));
            Assert.False(await host.WaitAsync(Support.CiBound));
        }
    }
}
