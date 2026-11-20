using Rodaje.App.Services.Collab;

namespace Rodaje.App.Tests.Collab;

public sealed class OperationClockTests
{
    [Fact]
    public void Dice_cuanto_lleva_y_solo_avisa_del_ultimo_avance_cuando_hace_rato_que_no_hay()
    {
        long now = 0;
        OperationClock clock = new(() => Interlocked.Read(ref now));
        Assert.Equal("Llevas 0:00.", clock.Describe());

        Interlocked.Add(ref now, 25_000);
        clock.Progressed();
        Interlocked.Add(ref now, 3_000);
        Assert.Equal("Llevas 0:28.", clock.Describe());

        Interlocked.Add(ref now, 5_000);
        Assert.Equal("Llevas 0:33. Sigue trabajando: último avance hace 8 s.", clock.Describe());

        Interlocked.Add(ref now, 3_600_000);
        Assert.StartsWith("Llevas 1:00:33.", clock.Describe(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0, 30, null)]
    [InlineData(0.5, 1, null)]
    [InlineData(1.0, 60, null)]
    [InlineData(0.5, 30, "unos 30 s")]
    [InlineData(0.25, 60, "unos 3 min")]
    [InlineData(0.9, 90, "unos 10 s")]
    public void El_tiempo_que_falta_solo_se_estima_cuando_hay_base(double fraction, int elapsedSeconds, string? expected) =>
        Assert.Equal(expected, OperationClock.Remaining(fraction, TimeSpan.FromSeconds(elapsedSeconds)));

    [Fact]
    public void Cerrar_Rodaje_pregunta_solo_si_hay_una_sala_con_otras_personas_o_se_esta_dentro_de_una()
    {
        RoomMember me = new(new string('a', 64), "Ana", Dathinel.Sala.Rol.Editar, true, true, "x-y-z-w", 0);
        RoomMember luis = new(new string('b', 64), "Luis", Dathinel.Sala.Rol.Editar, false, false, "x-y-z-w", 1);
        RoomMember gone = luis with { Online = false };

        Assert.Null(CollabClosing.Describe(null));
        Assert.Null(CollabClosing.Describe(RoomState.Closed));
        Assert.Null(CollabClosing.Describe(RoomState.Closed with { Phase = RoomPhase.Hosting, IsHost = true, Members = [me] }));
        Assert.Null(CollabClosing.Describe(RoomState.Closed with { Phase = RoomPhase.Hosting, IsHost = true, Members = [me, gone] }));
        (string text, string confirm)? host = CollabClosing.Describe(RoomState.Closed with { Phase = RoomPhase.Hosting, IsHost = true, Members = [me, luis] });
        Assert.Contains("1 persona", host!.Value.text, StringComparison.Ordinal);
        Assert.Equal("Cerrar Rodaje y la sala", host.Value.confirm);
        Assert.NotNull(CollabClosing.Describe(RoomState.Closed with { Phase = RoomPhase.Connected, IsHost = false, Members = [luis, me with { IsHost = false }] }));
    }
}
