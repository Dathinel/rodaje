using Dathinel.Sala;
using Rodaje.App.Services.Collab;

namespace Rodaje.App.Tests.Collab;

public sealed class RoomRecoveryViewTests
{
    private static readonly RoomMember Ana = new("ana", "Ana", Rol.Editar, false, false, "", 0);
    private static readonly RoomState Disconnected = RoomState.Closed with
    {
        Phase = RoomPhase.Reconnecting,
        MyKey = "propia",
        Members = [Ana],
    };

    [Fact]
    public void El_suplente_ve_la_accion_y_los_demas_su_nombre()
    {
        Assert.True(RoomRecoveryView.CanTakeHost(Disconnected, "propia"));
        Assert.Equal("El anfitrión se ha desconectado.", RoomRecoveryView.HostNotice(Disconnected, "propia"));
        Assert.False(RoomRecoveryView.CanTakeHost(Disconnected, Ana.Key));
        Assert.Equal("Esperando a que Ana tome la sala; tus cambios se guardan aquí.", RoomRecoveryView.HostNotice(Disconnected, Ana.Key));
    }

    [Theory]
    [InlineData(RoomPhase.Closed)]
    [InlineData(RoomPhase.Starting)]
    [InlineData(RoomPhase.Connected)]
    [InlineData(RoomPhase.Hosting)]
    [InlineData(RoomPhase.Ended)]
    public void Solo_se_ofrece_tomar_tras_perder_al_anfitrion(RoomPhase phase)
    {
        RoomState state = Disconnected with { Phase = phase };
        Assert.False(RoomRecoveryView.CanTakeHost(state, state.MyKey));
        Assert.Null(RoomRecoveryView.HostNotice(state, state.MyKey));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("desconocida")]
    public void Sin_suplente_conocido_se_espera_sin_mostrar_claves(string? standby)
    {
        Assert.False(RoomRecoveryView.CanTakeHost(Disconnected, standby));
        Assert.False(RoomRecoveryView.IsStandby(Ana, standby));
        Assert.Equal("El anfitrión se ha desconectado. Esperando a que vuelva la conexión; tus cambios se guardan aquí.",
            RoomRecoveryView.HostNotice(Disconnected, standby));
    }

    [Fact]
    public void El_rol_mirar_y_el_anfitrion_no_pueden_tomar()
    {
        Assert.False(RoomRecoveryView.CanTakeHost(Disconnected with { MyRole = Rol.Mirar }, "propia"));
        Assert.False(RoomRecoveryView.CanTakeHost(Disconnected with { IsHost = true }, "propia"));
        Assert.False(RoomRecoveryView.CanTakeHost(Disconnected with { MyKey = "" }, ""));
        Assert.True(RoomRecoveryView.IsStandby(Ana, Ana.Key));
    }

    [Fact]
    public void Solo_el_anfitrion_pasa_a_otro_editor_conectado()
    {
        RoomState host = Disconnected with { Phase = RoomPhase.Hosting, IsHost = true };
        Assert.True(RoomRecoveryView.CanHandOver(host, Ana));
        Assert.False(RoomRecoveryView.CanHandOver(host with { IsHost = false }, Ana));
        Assert.False(RoomRecoveryView.CanHandOver(host with { Phase = RoomPhase.Reconnecting }, Ana));
        Assert.False(RoomRecoveryView.CanHandOver(host, Ana with { IsMe = true }));
        Assert.False(RoomRecoveryView.CanHandOver(host, Ana with { IsHost = true }));
        Assert.False(RoomRecoveryView.CanHandOver(host, Ana with { Role = Rol.Mirar }));
        Assert.False(RoomRecoveryView.CanHandOver(host, Ana with { Online = false }));
        Assert.Equal("Ana pasa a hospedar la sala. Tú sigues dentro como editor.", RoomRecoveryView.HandOverConfirmation(Ana.Alias));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "1 cambio no entró · Ver")]
    [InlineData(2, "2 cambios no entraron · Ver")]
    public void El_aviso_indica_cuantos_cambios_quedan(int count, string expected) =>
        Assert.Equal(expected, RoomRecoveryView.UnsettledCount(count));

    [Fact]
    public void El_traspaso_se_oculta_fuera_de_la_sala_hospedada()
    {
        foreach (RoomPhase phase in Enum.GetValues<RoomPhase>())
        {
            if (phase == RoomPhase.Hosting) continue;
            Assert.False(RoomRecoveryView.CanHandOver(Disconnected with { Phase = phase, IsHost = true }, Ana));
        }
    }
}
