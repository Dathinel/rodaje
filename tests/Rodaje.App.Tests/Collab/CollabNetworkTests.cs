using Dathinel.Sala;
using Rodaje.App.Services.Collab;
using Rodaje.App.Services.Settings;

namespace Rodaje.App.Tests.Collab;

public sealed class CollabNetworkTests
{
    [Theory]
    [InlineData("https://rele.midominio.es", "https://rele.midominio.es/")]
    [InlineData("  https://rele.midominio.es:3340  ", "https://rele.midominio.es:3340/")]
    [InlineData("https://192.168.1.20:3340", "https://192.168.1.20:3340/")]
    [InlineData("HTTPS://RELE.MIDOMINIO.ES", "https://rele.midominio.es/")]
    public void Una_direccion_de_rele_buena_se_normaliza(string raw, string expected)
    {
        Assert.True(CollabNetwork.TryNormalizeRelayUrl(raw, out string? url, out _));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://rele.midominio.es")]              // sin TLS
    [InlineData("rele.midominio.es")]                    // sin esquema
    [InlineData("https://")]                             // sin servidor
    [InlineData("https://usuario:clave@rele.es")]        // credenciales
    [InlineData("https://rele.es/ruta")]                 // ruta
    [InlineData("https://rele.es/?param=1")]             // consulta
    [InlineData("https://rele.es/#parte")]               // fragmento
    public void Una_direccion_de_rele_mala_dice_por_que(string raw)
    {
        Assert.False(CollabNetwork.TryNormalizeRelayUrl(raw, out string? url, out string? problem));
        Assert.Null(url);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void Sin_ajustes_la_red_sale_por_los_reles_publicos_de_n0()
    {
        Red red = CollabNetwork.ForSettings(new AppSettings());
        Assert.Equal("n0", red.Rele);
        Assert.False(red.SoloRele);
    }

    [Fact]
    public void Solo_por_rele_llega_a_la_red()
    {
        Red red = CollabNetwork.ForSettings(new AppSettings { CollabRelayOnly = true });
        Assert.Equal("n0", red.Rele);
        Assert.True(red.SoloRele);
    }

    [Fact]
    public void Un_rele_propio_pone_su_direccion_en_la_red()
    {
        var settings = new AppSettings { CollabRelayUrl = "https://rele.midominio.es", CollabRelayOnly = true };
        Red red = CollabNetwork.ForSettings(settings);
        Assert.Equal("https://rele.midominio.es/", red.Rele);
        Assert.True(red.SoloRele);
        Assert.NotEqual("n0", red.Rele);   // un relé propio no es el de pruebas: copia ligera y originales se permiten
    }

    [Fact]
    public void Una_direccion_de_rele_guardada_y_mala_avisa_al_abrir_o_entrar()
    {
        var settings = new AppSettings { CollabRelayUrl = "http://sin-tls.es" };
        CollabException ex = Assert.Throws<CollabException>(() => CollabNetwork.ForSettings(settings));
        Assert.Contains("relé", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Los_ajustes_se_releen_cada_vez_para_que_valgan_sin_reiniciar()
    {
        var settings = new AppSettings();
        var env = new CollabEnvironment("dathinel-red", "identidad", "1.0")
        {
            RedSource = () => CollabNetwork.ForSettings(settings),
        };
        Assert.Equal("n0", env.RedSource!().Rele);
        settings.CollabRelayUrl = "https://rele.midominio.es";
        Assert.Equal("https://rele.midominio.es/", env.RedSource!().Rele);
    }

    [Fact]
    public void El_nombre_en_la_sala_usa_el_de_preferencias_si_vale()
    {
        Assert.Equal(CollabHost.SuggestedAlias(), CollabHost.SuggestedAlias(new AppSettings()));
        Assert.Equal("Lupe", CollabHost.SuggestedAlias(new AppSettings { CollabAlias = "  Lupe  " }));
    }

    [Fact]
    public void Un_nombre_de_preferencias_que_no_vale_vuelve_al_usuario_de_windows()
    {
        var settings = new AppSettings { CollabAlias = new string('x', CollabNames.MaxAlias + 10) };
        Assert.Equal(CollabHost.SuggestedAlias(), CollabHost.SuggestedAlias(settings));
    }

    [Fact]
    public void La_carpeta_de_las_copias_usa_la_de_preferencias_si_es_absoluta()
    {
        string documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rodaje", "Salas");
        Assert.Equal(documents, CollabNetwork.DefaultRoomsFolder(null));
        Assert.Equal(documents, CollabNetwork.DefaultRoomsFolder(new AppSettings()));
        string own = Path.Combine(Path.GetTempPath(), "salas-propias");
        Assert.Equal(own, CollabNetwork.DefaultRoomsFolder(new AppSettings { CollabRoomsFolder = own }));
        Assert.Equal(documents, CollabNetwork.DefaultRoomsFolder(new AppSettings { CollabRoomsFolder = "relativa\\salas" }));
    }

    [Fact]
    public void Los_ajustes_se_normalizan_al_guardar()
    {
        var settings = new AppSettings
        {
            CollabRelayUrl = "  https://rele.es  ",
            CollabAlias = "   ",
            CollabRoomsFolder = " ",
        };
        settings.Normalize();
        Assert.Equal("https://rele.es", settings.CollabRelayUrl);
        Assert.Null(settings.CollabAlias);
        Assert.Null(settings.CollabRoomsFolder);
    }
}
