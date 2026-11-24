using System.Text;
using Rodaje.App.Services.Collab;

namespace Rodaje.App.Tests.Collab;

public sealed class CollabInviteTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Host = new('a', 64);

    internal static string Ticket(string app = "rodaje", long? expires = null)
    {
        long caduca = expires ?? Now.AddHours(2).ToUnixTimeSeconds();
        string json = $$"""{"app":"{{app}}","anfitrion":"{{Host}}","id":"0123456789abcdef","secreto":"{{new string('b', 64)}}","caduca":{{caduca}}}""";
        return Encode(json);
    }

    private static string Encode(string json) =>
        "dathinel1." + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void El_enlace_el_billete_suelto_y_un_mensaje_con_el_enlace_dentro_dan_el_mismo_billete()
    {
        string t = Ticket();
        foreach (string pegado in new[]
        {
            CollabInvite.ToLink(t),
            t,
            "  " + CollabInvite.ToLink(t) + "\r\n",
            $"Hola, entra aquí: {CollabInvite.ToLink(t)} \nGracias",
            "​" + CollabInvite.ToLink(t),
        })
        {
            Assert.Equal(InviteProblem.None, CollabInvite.TryParse(pegado, Now, out InviteInfo? info));
            Assert.Equal(t, info!.Ticket);
            Assert.Equal(Now.AddHours(2), info.Expires);
        }
    }

    [Fact]
    public void Un_billete_partido_en_varias_lineas_por_el_chat_se_junta()
    {
        string t = Ticket();
        string partido = CollabInvite.ToLink(t).Insert(60, " \r\n  ");
        Assert.Equal(InviteProblem.None, CollabInvite.TryParse(partido, Now, out InviteInfo? info));
        Assert.Equal(t, info!.Ticket);
    }

    [Theory]
    [InlineData(null, InviteProblem.Empty)]
    [InlineData("", InviteProblem.Empty)]
    [InlineData("   \r\n", InviteProblem.Empty)]
    [InlineData("hola", InviteProblem.NotAnInvite)]
    [InlineData("https://ejemplo.com/rodaje", InviteProblem.NotAnInvite)]
    [InlineData("dathinel1.", InviteProblem.NotAnInvite)]
    [InlineData("dathinel1.@@@", InviteProblem.NotAnInvite)]
    [InlineData("dathinel1.abc", InviteProblem.Damaged)]
    [InlineData("dathinel1.eyJhcHAiOiJyb2RhamUifQ", InviteProblem.Damaged)]
    public void Lo_que_no_es_una_invitacion_dice_por_que(string? texto, InviteProblem esperado)
    {
        Assert.Equal(esperado, CollabInvite.TryParse(texto, Now, out InviteInfo? info));
        Assert.Null(info);
    }

    [Fact]
    public void Una_invitacion_de_otro_programa_y_una_caducada_se_distinguen()
    {
        Assert.Equal(InviteProblem.OtherApp, CollabInvite.TryParse(Ticket(app: "unicomp"), Now, out _));
        Assert.Equal(InviteProblem.Expired, CollabInvite.TryParse(Ticket(expires: Now.AddMinutes(-1).ToUnixTimeSeconds()), Now, out _));
        Assert.Equal(InviteProblem.None, CollabInvite.TryParse(Ticket(expires: Now.AddMinutes(1).ToUnixTimeSeconds()), Now, out _));
    }

    [Fact]
    public void Un_texto_enorme_no_se_procesa()
    {
        Assert.Equal(InviteProblem.NotAnInvite, CollabInvite.TryParse(new string('x', 100_000), Now, out _));
        Assert.Equal(InviteProblem.Damaged, CollabInvite.TryParse("dathinel1." + new string('A', 6000), Now, out _));
    }

    [Fact]
    public void Fecha_absurda_en_el_billete_no_lanza()
    {
        string json = $$"""{"app":"rodaje","anfitrion":"{{Host}}","caduca":9223372036854775807}""";
        Assert.Equal(InviteProblem.None, CollabInvite.TryParse(Encode(json), Now, out InviteInfo? info));
        Assert.NotNull(info!.Expires);
    }
}
