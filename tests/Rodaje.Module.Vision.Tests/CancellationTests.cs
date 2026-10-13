using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Xunit.Abstractions;

namespace Rodaje.Module.Vision.Tests;

/// <summary>
/// Una inferencia cancelada se corta en ONNX Runtime (<see cref="RunOptions.Terminate"/> enlazado al token, como en Voz) y
/// sale como <see cref="OperationCanceledException"/>. No hacen falta los modelos de visión: un modelo mínimo, hecho para
/// esta prueba, es un <c>Loop</c> de 2^62 vueltas de <c>Tanh</c> que no termina solo; sin el enlace, la llamada no vuelve.
/// </summary>
public sealed class CancellationTests(ITestOutputHelper output)
{
    /// <summary>
    /// <c>y = Loop(M = 2^62, cond = true, x)</c> con un cuerpo <c>v = Tanh(v)</c>; entrada «x» y salida «y», float de forma
    /// libre (opset 17). Generado con el paquete <c>onnx</c> de Python (<c>onnx.helper</c>) y serializado.
    /// </summary>
    private const string EndlessModel =
        "CAgSDnJvZGFqZS1wcnVlYmFzOvgBCqwBCgFNCgFDCgF4EgF5IgRMb29wKpcBCgRib2R5MosBChMKBHZfaW4SBXZfb3V0IgRUYW5oCh0KB2NvbmRfaW4S" +
        "CGNvbmRfb3V0IghJZGVudGl0eRIEYm9keVoLCgFpEgYKBAgHEgBaEQoHY29uZF9pbhIGCgQICRIAWgwKBHZfaW4SBAoCCAFiEgoIY29uZF9vdXQSBgoE" +
        "CAkSAGINCgV2X291dBIECgIIAaABBRIHZW5kbGVzcyoQEAc6CYCAgICAgICAQEIBTSoIEAkqAQFCAUNaEAoBeBILCgkIARIFCgMSAW5iEAoBeRILCgkI" +
        "ARIFCgMSAW5CBAoAEBE=";

    private static LoadedModel Endless() => new(new InferenceSession(Convert.FromBase64String(EndlessModel)), "cpu");

    [Fact]
    public async Task Cancelling_terminates_a_running_inference()
    {
        LoadedModel model = Endless();
        using CancellationTokenSource cts = new();
        Task<float[][]> task = Task.Factory.StartNew(() => model.Run("x", [0.5f, -0.5f], [2], ["y"], cts.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await Task.Delay(300);
        Assert.False(task.IsCompleted, "El modelo sin fin terminó solo: la prueba no mide nada.");

        Stopwatch afterCancel = Stopwatch.StartNew();
        cts.Cancel();
        bool finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))) == task;
        Assert.True(finished, "La inferencia cancelada siguió corriendo 30 s: Terminate no está enlazado al token.");
        output.WriteLine($"Cortada {afterCancel.ElapsedMilliseconds} ms después de cancelar.");

        OperationCanceledException ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(cts.Token, ex.CancellationToken);

        // Solo se desecha si volvió: Dispose espera a que acabe el Run en curso.
        model.Dispose();
    }

    [Fact]
    public async Task An_already_cancelled_token_does_not_start_the_inference()
    {
        LoadedModel model = Endless();
        using CancellationTokenSource cts = new();
        cts.Cancel();
        Task<float[][]> task = Task.Factory.StartNew(() => model.Run("x", [1f], [1], ["y"], cts.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))) == task, "Con el token ya cancelado, la inferencia empezó igual.");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        model.Dispose();
    }
}
