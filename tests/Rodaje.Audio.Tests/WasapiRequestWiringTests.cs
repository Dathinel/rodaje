using System.Reflection;
using System.Runtime.CompilerServices;
using Rodaje.Core.Timing;

namespace Rodaje.Audio.Tests;

/// <summary>
/// Que las órdenes de transporte de <see cref="WasapiOutput"/> pasen de verdad por <c>DecideRequest</c>:
/// <see cref="WasapiTransportRequestTests"/> prueba la decisión suelta, y nada comprobaba que <c>Request</c> la usara.
/// Sin dispositivo: la salida se arma sin constructor (sin hilo ni eventos), con el estado de un Dispose que ganó la
/// carrera después de la comprobación de entrada (<c>_shutdown</c> puesto y <c>_disposed</c> todavía no).
/// </summary>
/// <remarks>
/// Si <c>Request</c> dejara de consultar la decisión, la pausa anotaría la orden (<c>_requested</c> pasaría a 1) y
/// señalaría el evento del hilo de audio, ya cerrado; y Start o Flush no lanzarían.
/// </remarks>
public sealed class WasapiRequestWiringTests
{
    [Fact]
    public void A_pause_after_shutdown_is_ignored_without_touching_the_audio_thread()
    {
        WasapiOutput output = ShutDown();

        ((IRenderTransport)output).Pause();

        Assert.Equal(0L, Field<long>(output, "_requested"));
        Assert.Equal(0, Field<int>(output, "_controlTimeouts"));
    }

    [Fact]
    public void Start_or_flush_after_shutdown_throws_without_recording_the_order()
    {
        WasapiOutput output = ShutDown();

        Assert.Throws<ObjectDisposedException>(() => ((IRenderTransport)output).Start());
        Assert.Throws<ObjectDisposedException>(() => ((IRenderTransport)output).Flush(Time.FromSeconds(1)));

        Assert.Equal(0L, Field<long>(output, "_requested"));
        Assert.Equal(0L, Field<long>(output, "_flushRequests"));
    }

    /// <summary>Una salida sin constructor con el estado que deja Dispose bajo el candado de control.</summary>
    private static WasapiOutput ShutDown()
    {
        var output = (WasapiOutput)RuntimeHelpers.GetUninitializedObject(typeof(WasapiOutput));
        FieldOf("_control").SetValue(output, new object());
        FieldOf("_shutdown").SetValue(output, true);
        return output;
    }

    private static T Field<T>(WasapiOutput output, string name) => (T)FieldOf(name).GetValue(output)!;

    private static FieldInfo FieldOf(string name) =>
        typeof(WasapiOutput).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"WasapiOutput ya no tiene el campo {name}: actualizar esta prueba.");
}
