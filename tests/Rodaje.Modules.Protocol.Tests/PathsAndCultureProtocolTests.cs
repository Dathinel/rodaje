using System.Globalization;
using System.Text.Json;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// Familia 7 (Protocolo): Rutas y cultura:
/// comportamiento del protocolo bajo es-CO y tr-TR:
/// funciones con «i», JSON con decimales y coma, textos con acentos y eñes,
/// y regiones de memoria compartida sin defectos de la «i» turca.
/// </summary>
public sealed class PathsAndCultureProtocolTests
{
    [Theory]
    [InlineData("es-CO")]
    [InlineData("tr-TR")]
    public async Task Protocolo_Cultura_Handshake_NegociaCorrectamente(string cultureName)
    {
        using (CultureScope.Set(cultureName))
        {
            var options = new ModuleClientOptions
            {
                Culture = cultureName,
                App = "rodaje",
                AppVersion = "1.0.0"
            };

            (ModuleClient client, Task<bool> host) = await Support.ConnectInProcessAsync(options: options);
            await using (client)
            {
                Assert.Equal(1, client.Protocol);
                Assert.Equal("prueba", client.Module.Id);
                await client.PingAsync();
            }

            Assert.True(await host);
        }
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("tr-TR")]
    public async Task Protocolo_Cultura_NombresDeFuncionConI_NoSeRompen(string cultureName)
    {
        using (CultureScope.Set(cultureName))
        {
            var info = new ModuleInfo("mod-cultura", "1.0.0", "Módulo",
            [
                new FunctionInfo("identificar", "Identificar"),
                new FunctionInfo("info", "Info")
            ]);

            var host = new ModuleHost(info);
            host.Register("identificar", (call, _) => ValueTask.FromResult(new ModuleOutput(call.Parameters)));
            host.Register("info", (_, _) => ValueTask.FromResult(new ModuleOutput(Support.Json("{\"version\":1}"))));

            (ModuleClient client, Task<bool> hostTask) = await Support.ConnectInProcessAsync(host);
            await using (client)
            {
                JsonElement param = Support.Json("{\"id\":123}");
                ModuleResult res1 = await client.InvokeAsync("identificar", param);
                Assert.True(JsonElement.DeepEquals(param, res1.Value!.Value));

                ModuleResult res2 = await client.InvokeAsync("info");
                Assert.Equal(1, res2.Value!.Value.GetProperty("version").GetInt32());
            }

            Assert.True(await hostTask);
        }
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("tr-TR")]
    public async Task Protocolo_Cultura_ParametrosJsonConTildesYEne_Preservados(string cultureName)
    {
        using (CultureScope.Set(cultureName))
        {
            (ModuleClient client, Task<bool> host) = await Support.ConnectInProcessAsync();
            await using (client)
            {
                string jsonText = """{"texto":"canción de año nuevo con ñandú 🎬","tildes":"áéíóúÁÉÍÓÚ"}""";
                JsonElement param = Support.Json(jsonText);
                ModuleResult res = await client.InvokeAsync("eco", param);

                Assert.True(JsonElement.DeepEquals(param, res.Value!.Value));
                Assert.Equal("canción de año nuevo con ñandú 🎬", res.Value!.Value.GetProperty("texto").GetString());
            }

            Assert.True(await host);
        }
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("tr-TR")]
    public async Task Protocolo_Cultura_NumerosDecimalesEnJson_PuntoFlotanteNoSeAfectaPorComa(string cultureName)
    {
        using (CultureScope.Set(cultureName))
        {
            (ModuleClient client, Task<bool> host) = await Support.ConnectInProcessAsync();
            await using (client)
            {
                // En es-CO el separador decimal estándar es coma, pero en JSON es punto obligatorio
                JsonElement param = Support.Json("{\"tasa\":0.25,\"escala\":1.5}");
                ModuleResult res = await client.InvokeAsync("eco", param);

                Assert.True(JsonElement.DeepEquals(param, res.Value!.Value));
                Assert.Equal(0.25, res.Value!.Value.GetProperty("tasa").GetDouble());
                Assert.Equal(1.5, res.Value!.Value.GetProperty("escala").GetDouble());
            }

            Assert.True(await host);
        }
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("tr-TR")]
    public async Task Protocolo_Cultura_MensajesDeErrorConTildesYEne_Recibidos(string cultureName)
    {
        using (CultureScope.Set(cultureName))
        {
            var info = new ModuleInfo("mod-err", "1.0.0", "ErrorMod", [new FunctionInfo("fallar-es", "FallarES")]);
            var host = new ModuleHost(info);
            host.Register("fallar-es", (_, _) => throw new InvalidOperationException("Falló la operación: diseño de año 2026."));

            (ModuleClient client, Task<bool> hostTask) = await Support.ConnectInProcessAsync(host);
            await using (client)
            {
                var ex = await Assert.ThrowsAsync<ModuleCallException>(() => client.InvokeAsync("fallar-es"));
                Assert.Contains("Falló la operación: diseño de año 2026.", ex.Message, StringComparison.Ordinal);
            }

            Assert.True(await hostTask);
        }
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("tr-TR")]
    public void Protocolo_Cultura_SharedMemoryRegion_NombreValidoBajoCultura(string cultureName)
    {
        using (CultureScope.Set(cultureName))
        {
            using var region = SharedMemoryRegion.Create(4096);
            Assert.True(Limits.RegionName(region.Name));
            Assert.StartsWith("Rodaje.Mod.", region.Name, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("tr-TR")]
    public void Protocolo_Cultura_LimitsIdentifier_BajoCulturaTurca(string cultureName)
    {
        using (CultureScope.Set(cultureName))
        {
            Assert.Null(Limits.Identifier("identificador.valido"));
            Assert.Null(Limits.Identifier("ping"));
            Assert.Null(Limits.Identifier("info-modulo"));

            Assert.NotNull(Limits.Identifier("IDENTIFICADOR")); // mayúsculas no permitidas
            Assert.NotNull(Limits.Identifier("con espacios"));
            // La «ı» sin punto y la «İ» con punto de tr-TR no son letras ASCII: no pasan por «i».
            Assert.NotNull(Limits.Identifier("ıdentificador"));
            Assert.NotNull(Limits.Identifier("İdentificador"));
        }
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _prevCulture;
        private readonly CultureInfo _prevUiCulture;

        private CultureScope(string cultureName)
        {
            _prevCulture = CultureInfo.CurrentCulture;
            _prevUiCulture = CultureInfo.CurrentUICulture;
            var culture = new CultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public static CultureScope Set(string cultureName) => new(cultureName);

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _prevCulture;
            CultureInfo.CurrentUICulture = _prevUiCulture;
        }
    }
}
