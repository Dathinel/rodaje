using Dathinel.Sala;
using Rodaje.App.Services.Collab;

namespace Rodaje.App.Tests.Collab;

public sealed class CollabBasicsTests
{
    private static string Key(char c) => new(c, 64);

    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "rodaje-anclados-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void El_color_de_una_clave_es_siempre_el_mismo_y_esta_en_rango()
    {
        for (int i = 0; i < 256; i++)
        {
            string key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i)));
            int c = MemberColors.IndexFor(key);
            Assert.InRange(c, 0, MemberColors.Count - 1);
            Assert.Equal(c, MemberColors.IndexFor(key));
        }
    }

    [Theory]
    [InlineData("  Luis  ", "Luis")]
    [InlineData("Ana\tMaría\r\n", "Ana María")]
    [InlineData("a\u0000b", "ab")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void El_nombre_se_limpia(string? entrada, string? esperado) =>
        Assert.Equal(esperado, CollabNames.NormalizeAlias(entrada));

    [Fact]
    public void Un_nombre_de_mas_de_40_letras_no_vale() =>
        Assert.Null(CollabNames.NormalizeAlias(new string('x', 41)));

    [Fact]
    public void Los_anclados_se_guardan_y_vuelven_y_lo_danado_se_aparta()
    {
        string folder = TempFolder();
        try
        {
            Assert.Empty(AnchoredStore.Load(folder));
            AnchoredStore.Save(folder, [new Miembro(Key('a'), "Luis", Rol.Mirar), new Miembro(Key('b'), "Eva", Rol.Editar)]);
            IReadOnlyList<Miembro> back = AnchoredStore.Load(folder);
            Assert.Equal(2, back.Count);
            Assert.Equal(Rol.Mirar, back.Single(m => m.Alias == "Luis").Rol);

            File.WriteAllText(Path.Combine(folder, AnchoredStore.FileName), "{no es json");
            Assert.Empty(AnchoredStore.Load(folder));
            Assert.Contains(Directory.GetFiles(folder), f => f.Contains("danado", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [Fact]
    public void Un_anclado_con_clave_o_nombre_invalidos_se_descarta()
    {
        string folder = TempFolder();
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(
                Path.Combine(folder, AnchoredStore.FileName),
                $$"""[{"key":"corta","alias":"X","role":"editar"},{"key":"{{Key('c')}}","alias":"","role":"editar"},{"key":"{{Key('d')}}","alias":"Ok","role":"raro"}]""");
            Miembro only = Assert.Single(AnchoredStore.Load(folder));
            Assert.Equal("Ok", only.Alias);
            Assert.Equal(Rol.Editar, only.Rol);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Cada_codigo_de_error_de_Sala_da_un_texto_que_dice_que_hacer()
    {
        foreach (string code in new[] { "rechazado", "version", "billete-invalido", "sin-conexion", "llena", "ya-abierta", "sin-red", "ocupado", "identidad", "superada", "otro-cualquiera" })
        {
            string text = CollabMessages.For(new SalaException(code, ""));
            Assert.False(string.IsNullOrWhiteSpace(text), code);
            Assert.DoesNotContain("dathinel", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(":\\", text, StringComparison.Ordinal);
        }

        Assert.Equal("Mensaje de Sala.", CollabMessages.For(new SalaException("version", "Mensaje de Sala.")));
    }
}
