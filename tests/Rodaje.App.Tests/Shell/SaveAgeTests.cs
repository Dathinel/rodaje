using Rodaje.App.Shell;

namespace Rodaje.App.Tests.Shell;

/// <summary>El texto «Guardado hace N min» de la barra de estado.</summary>
public sealed class SaveAgeTests
{
    [Theory]
    [InlineData(0, "hace un momento")]
    [InlineData(59, "hace un momento")]
    [InlineData(60, "hace 1 min")]
    [InlineData(179, "hace 2 min")]
    [InlineData(90 * 60, "hace 90 min")]
    [InlineData(91 * 60, "hace 1 h")]
    [InlineData(5 * 3600 + 59 * 60, "hace 5 h")]
    [InlineData(24 * 3600, "hace 1 día")]
    [InlineData(3 * 24 * 3600 + 3600, "hace 3 días")]
    public void Ago_RedondeaHaciaAbajoYCambiaDeUnidad(int seconds, string expected)
    {
        Assert.Equal(expected, SaveAge.Ago(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Ago_UnTiempoNegativoCuentaComoAhora()
    {
        Assert.Equal("hace un momento", SaveAge.Ago(TimeSpan.FromSeconds(-30)));
    }

    [Fact]
    public void Format_SinGuardadoNiCopiaQuedaVacio()
    {
        Assert.Equal("", SaveAge.Format(null, null, dirty: true));
    }

    [Fact]
    public void Format_PrefiereElGuardadoSalvoQueHayaCambiosYUnaCopiaMasNueva()
    {
        TimeSpan save = TimeSpan.FromMinutes(10);
        TimeSpan copy = TimeSpan.FromMinutes(3);

        Assert.Equal("Guardado hace 10 min", SaveAge.Format(save, copy, dirty: false));
        Assert.Equal("Copia automática hace 3 min", SaveAge.Format(save, copy, dirty: true));
        Assert.Equal("Guardado hace 10 min", SaveAge.Format(save, TimeSpan.FromMinutes(12), dirty: true));
        Assert.Equal("Guardado hace 10 min", SaveAge.Format(save, null, dirty: true));
        Assert.Equal("Copia automática hace 3 min", SaveAge.Format(null, copy, dirty: true));
    }
}
