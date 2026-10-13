using Rodaje.Core.Timing;
using static Rodaje.Audio.WasapiOutput;

namespace Rodaje.Audio.Tests;

/// <summary>
/// La decisión de <c>WasapiOutput.Request</c> sin dispositivo. Regresión: una orden de transporte que perdía la carrera
/// con <see cref="WasapiOutput.Dispose"/> (pasaba la comprobación de entrada y tomaba el candado después) volvía a señalar
/// el evento del hilo de audio, que Dispose ya había cerrado. Antes solo lo cubría una prueba con hardware.
/// </summary>
public sealed class WasapiTransportRequestTests
{
    [Fact]
    public void A_pause_that_loses_the_race_with_dispose_is_ignored()
    {
        Assert.Equal(TransportRequest.Ignore, DecideRequest(shutdown: true, running: false, flush: null));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(null, true)]
    [InlineData(false, true)]
    public void Start_or_flush_after_dispose_throws_instead_of_signalling(bool? running, bool flush)
    {
        Assert.Equal(TransportRequest.Disposed, DecideRequest(shutdown: true, running, flush ? Time.FromSeconds(1) : null));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(null, true)]
    [InlineData(false, true)]
    public void Without_dispose_every_order_reaches_the_audio_thread(bool? running, bool flush)
    {
        Assert.Equal(TransportRequest.Signal, DecideRequest(shutdown: false, running, flush ? Time.FromSeconds(1) : null));
    }
}
