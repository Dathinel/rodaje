using System.Text.RegularExpressions;

namespace Rodaje.Core.Tests.Architecture;

/// <summary>
/// Prueba de arquitectura sobre el texto de Visión y Voz (tarea 244): su trabajo que bloquea (preparar, inferir y escribir
/// en Visión; sintetizar en Voz) corre con <c>ModuleHost.RunBlockingAsync</c>, en un hilo propio, y no con
/// <c>Task.Run</c> en el pool (tarea 195, #193). Con el pool ocupado, el bucle de <c>ModuleHost</c> no contestaba el
/// <c>ping</c> y el editor mataba un módulo sano. Que <c>RunBlockingAsync</c> no usa el pool lo prueba
/// <c>BlockingWorkTests</c> del protocolo; aquí se comprueba que los dos módulos lo usan. Las funciones de Visión están en
/// el <c>Program.cs</c> de nivel superior y la síntesis necesita el modelo de Kokoro, así que no se pueden llamar sin
/// modelos desde una prueba. Lee el fuente sin comentarios ni cadenas, como <see cref="AsyncSafetyTests"/>.
/// </summary>
public sealed partial class ModuleBlockingWorkTests
{
    // No se fija el número de llamadas: añadir o quitar una legítima no debe romper la prueba (tarea 314).
    public static TheoryData<string> Modules => new()
    {
        "modules/Rodaje.Module.Vision/Program.cs",
        "modules/Rodaje.Module.Voice/VoiceFunctions.cs",
    };

    [Theory]
    [MemberData(nameof(Modules))]
    public void The_blocking_work_of_the_module_runs_off_the_pool(string file) => AssertRunsOffThePool(Code(file));

    [Fact]
    public void The_check_catches_the_code_before_task_195()
    {
        string old = AsyncSafetyTests.Strip("""
            result = await Task.Run(() => synth.Speak(request, null, ct), ct).ConfigureAwait(false);
            Task<MatteInput> p = Task.Factory.StartNew(() => Prepare(frame), ct, TaskCreationOptions.None, TaskScheduler.Default);
            """);
        Assert.Equal(2, PoolRegex().Count(old));
        Assert.Equal(0, RunBlockingRegex().Count(old));

        Assert.ThrowsAny<Exception>(() => AssertRunsOffThePool(old));

        string current = AsyncSafetyTests.Strip("result = await ModuleHost.RunBlockingAsync(() => synth.Speak(request, null, ct), ct); // no Task.Run(");
        Assert.Empty(PoolRegex().Matches(current));
        Assert.Equal(1, RunBlockingRegex().Count(current));
        AssertRunsOffThePool(current);
    }

    [Fact]
    public void The_check_does_not_depend_on_the_number_of_calls()
    {
        string more = AsyncSafetyTests.Strip("""
            a = await ModuleHost.RunBlockingAsync(() => Prepare(frame), ct);
            b = await ModuleHost.RunBlockingAsync(() => Infer(input), ct);
            c = await ModuleHost.RunBlockingAsync(() => Write(output), ct);
            """);
        AssertRunsOffThePool(more);

        string none = AsyncSafetyTests.Strip("a = Prepare(frame);");
        Assert.ThrowsAny<Exception>(() => AssertRunsOffThePool(none));
    }

    private static void AssertRunsOffThePool(string code)
    {
        Assert.Empty(PoolRegex().Matches(code));
        Assert.True(RunBlockingRegex().Count(code) >= 1, "el módulo no llama a ModuleHost.RunBlockingAsync");
    }

    private static string Code(string file)
    {
        string path = Path.Combine(AsyncSafetyTests.RepoRoot(), file);
        Assert.True(File.Exists(path), $"no se encontró {file}");
        return AsyncSafetyTests.Strip(File.ReadAllText(path));
    }

    [GeneratedRegex(@"\bTask\s*\.\s*(Run|Factory\s*\.\s*StartNew)\s*[(<]")]
    private static partial Regex PoolRegex();

    [GeneratedRegex(@"\bModuleHost\s*\.\s*RunBlockingAsync\s*[(<]")]
    private static partial Regex RunBlockingRegex();
}
