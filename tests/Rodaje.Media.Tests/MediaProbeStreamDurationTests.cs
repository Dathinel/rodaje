using Rodaje.Core.Timing;
using Rodaje.Media.Probe;

namespace Rodaje.Media.Tests;

/// <summary>
/// Revisión de #109: la duración de un flujo que no la declara sale de la del contenedor menos lo que tarda en empezar. Las
/// pruebas con FFmpeg solo exigían <c>Duration &gt;= 0</c>, así que devolver <see cref="Time.Zero"/> en vez de «desconocida»
/// también pasaba. Estas prueban el cálculo sin FFmpeg.
/// </summary>
public sealed class MediaProbeStreamDurationTests
{
    [Theory]
    [InlineData(10.0, 12.0, 0.0)] // empieza después del final
    [InlineData(10.0, 10.0, 0.0)] // empieza justo en el final
    [InlineData(10.0, 15.0, 5.0)] // con inicio del contenedor desplazado, justo en el final
    [InlineData(10.0, 40.0, 5.0)]
    public void StreamStartingAtOrAfterTheEnd_IsUnknown(double container, double start, double containerStart)
    {
        Assert.Null(MediaProbe.RemainingContainerDuration(Time.FromSeconds(container), Time.FromSeconds(start), Time.FromSeconds(containerStart)));
    }

    [Fact]
    public void UnknownContainerDuration_IsUnknown()
    {
        Assert.Null(MediaProbe.RemainingContainerDuration(null, Time.Zero, Time.Zero));
    }

    [Theory]
    [InlineData(10.0, 0.0, 0.0, 10.0)]
    [InlineData(10.0, 2.0, 0.0, 8.0)]
    [InlineData(10.0, 7.0, 5.0, 8.0)]
    [InlineData(10.0, 3.0, 5.0, 12.0)] // empieza antes que el contenedor
    public void StreamStartingBeforeTheEnd_GetsTheRest(double container, double start, double containerStart, double expected)
    {
        Assert.Equal(Time.FromSeconds(expected),
            MediaProbe.RemainingContainerDuration(Time.FromSeconds(container), Time.FromSeconds(start), Time.FromSeconds(containerStart)));
    }

    [Fact]
    public void OneTickLeft_IsKept()
    {
        Time container = Time.FromSeconds(10);
        Assert.Equal(Time.FromTicks(1),
            MediaProbe.RemainingContainerDuration(container, Time.FromTicks(container.Ticks - 1), Time.Zero));
    }
}
