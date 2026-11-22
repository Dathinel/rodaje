using Rodaje.App.Services.Collab;

namespace Rodaje.App.Tests.Collab;

public sealed class CollabClosingTests
{
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
