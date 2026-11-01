using Rodaje.Core.Serialization;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>Cuándo toca escribir una copia de autoguardado.</summary>
public sealed class AutosaveScheduleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static AutosaveSchedule Create() =>
        new(interval: TimeSpan.FromMinutes(2), quietPeriod: TimeSpan.FromSeconds(5), minimumGap: TimeSpan.FromSeconds(20));

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void WithoutEdits_IsNeverDue()
    {
        AutosaveSchedule schedule = Create();

        Assert.False(schedule.HasPendingChanges);
        Assert.False(schedule.IsDue(At(3600)));
    }

    [Fact]
    public void AfterAnEdit_IsDueOnceTheQuietPeriodPasses()
    {
        AutosaveSchedule schedule = Create();
        schedule.NoteEdit(At(0));

        Assert.False(schedule.IsDue(At(4.9)));
        Assert.True(schedule.IsDue(At(5)));
    }

    [Fact]
    public void ContinuousEditing_IsDueAtTheInterval()
    {
        AutosaveSchedule schedule = Create();
        for (int second = 0; second < 119; second++)
        {
            schedule.NoteEdit(At(second));
            Assert.False(schedule.IsDue(At(second + 0.5)));
        }

        schedule.NoteEdit(At(119));
        Assert.True(schedule.IsDue(At(120)));
    }

    [Fact]
    public void AfterAnAttempt_WaitsTheMinimumGap()
    {
        AutosaveSchedule schedule = Create();
        schedule.NoteEdit(At(0));
        schedule.NoteAttempt(At(10));

        // El intento falló (no se anotó al día): sigue pendiente, pero no antes de 20 s.
        Assert.True(schedule.HasPendingChanges);
        Assert.False(schedule.IsDue(At(29)));
        Assert.True(schedule.IsDue(At(30)));
    }

    [Fact]
    public void CaughtUp_ClearsPendingChanges()
    {
        AutosaveSchedule schedule = Create();
        schedule.NoteEdit(At(0));
        schedule.NoteAttempt(At(6));
        schedule.NoteCaughtUp(At(6));

        Assert.False(schedule.HasPendingChanges);
        Assert.False(schedule.IsDue(At(600)));
    }

    [Fact]
    public void EditsDuringTheWrite_StayPending()
    {
        AutosaveSchedule schedule = Create();
        schedule.NoteEdit(At(0));
        schedule.NoteAttempt(At(6));
        schedule.NoteEdit(At(7));
        schedule.NoteCaughtUp(At(6));

        Assert.True(schedule.HasPendingChanges);
        Assert.False(schedule.IsDue(At(25)));
        Assert.True(schedule.IsDue(At(26)));
    }

    [Fact]
    public void SetInterval_CuentaElIntervaloNuevoDesdeElCambio()
    {
        // Se edita cada dos segundos, así que la calma (5 s) nunca llega y solo decide el intervalo.
        AutosaveSchedule schedule = Create();
        schedule.NoteEdit(At(0));
        schedule.NoteEdit(At(100));

        schedule.SetInterval(TimeSpan.FromMinutes(1), At(101));

        Assert.Equal(TimeSpan.FromMinutes(1), schedule.Interval);
        Assert.True(schedule.HasPendingChanges);
        // El primer cambio sin copia es de hace más de un minuto, pero el minuto se cuenta desde el cambio de intervalo.
        schedule.NoteEdit(At(158));
        Assert.False(schedule.IsDue(At(160)));
        schedule.NoteEdit(At(160));
        Assert.True(schedule.IsDue(At(162)));
    }

    [Fact]
    public void SetInterval_AlargarloEsperaLoElegidoDesdeElCambio()
    {
        AutosaveSchedule schedule = Create();
        schedule.NoteEdit(At(0));
        schedule.NoteEdit(At(118));

        schedule.SetInterval(TimeSpan.FromMinutes(30), At(119));

        schedule.NoteEdit(At(121));
        Assert.False(schedule.IsDue(At(123)));
        schedule.NoteEdit(At(119 + 30 * 60 - 1));
        Assert.True(schedule.IsDue(At(119 + 30 * 60)));
    }

    [Fact]
    public void SetInterval_SinCambiosPendientesNoInventaNinguno()
    {
        AutosaveSchedule schedule = Create();

        schedule.SetInterval(TimeSpan.FromMinutes(1), At(10));

        Assert.False(schedule.HasPendingChanges);
        Assert.False(schedule.IsDue(At(3600)));
    }

    [Fact]
    public void SetInterval_SameValueDoesNotPostponePendingEdits()
    {
        AutosaveSchedule schedule = Create();
        schedule.NoteEdit(At(0));
        schedule.NoteEdit(At(119));

        schedule.SetInterval(schedule.Interval, At(119));

        Assert.True(schedule.HasPendingChanges);
        Assert.False(schedule.IsDue(At(119.9)));
        Assert.True(schedule.IsDue(At(120)));
    }

    [Fact]
    public void SetInterval_RechazaCeroONegativo()
    {
        var schedule = new AutosaveSchedule();

        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.SetInterval(TimeSpan.Zero, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.SetInterval(TimeSpan.FromMinutes(-1), T0));
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

    [Fact]
    public void Constructor_RejectsNonPositiveInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AutosaveSchedule(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AutosaveSchedule(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(-1), TimeSpan.Zero));
    }
}
