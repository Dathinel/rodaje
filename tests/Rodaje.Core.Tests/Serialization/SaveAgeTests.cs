using Rodaje.Core.Serialization;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>El texto «Guardado hace N min» de la barra de estado y el intervalo configurable del autoguardado.</summary>
public sealed class SaveAgeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public void SetInterval_AcortarloHaceQueToqueAntesYAlargarloQueEspere()
    {
        var schedule = new AutosaveSchedule(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20));
        schedule.NoteEdit(T0);
        schedule.NoteEdit(T0.AddSeconds(70));

        Assert.False(schedule.IsDue(T0.AddSeconds(71)));

        schedule.SetInterval(TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromMinutes(1), schedule.Interval);
        Assert.True(schedule.IsDue(T0.AddSeconds(71)));

        schedule.SetInterval(TimeSpan.FromMinutes(30));
        Assert.False(schedule.IsDue(T0.AddSeconds(71)));
        Assert.True(schedule.IsDue(T0.AddSeconds(76)));
    }

    [Fact]
    public void SetInterval_RechazaCeroONegativo()
    {
        var schedule = new AutosaveSchedule();

        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.SetInterval(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.SetInterval(TimeSpan.FromMinutes(-1)));
        Assert.Equal(TimeSpan.FromMinutes(AutosaveSchedule.DefaultIntervalMinutes), schedule.Interval);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    [InlineData(30, 30)]
    [InlineData(999, 30)]
    [InlineData(-5, 1)]
    public void ClampMinutes_AcotaDeUnoATreinta(int minutes, int expected)
    {
        Assert.Equal(expected, AutosaveSchedule.ClampMinutes(minutes));
    }
}
