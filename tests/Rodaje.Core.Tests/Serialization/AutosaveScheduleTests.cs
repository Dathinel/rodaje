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
    public void Constructor_RejectsNonPositiveInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AutosaveSchedule(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AutosaveSchedule(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(-1), TimeSpan.Zero));
    }
}
