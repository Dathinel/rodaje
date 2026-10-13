using System.Globalization;
using System.Text;
using Rodaje.Packages.Content;
using Xunit.Abstractions;

namespace Rodaje.Packages.Tests.Fuzz;

/// <summary>
/// Casos hostiles y fuzz de <c>rodaje.safezones</c> v1: tamaños en el borde, números extremos, Unicode en las etiquetas,
/// JSON roto y mutaciones al azar con semilla fija (<see cref="ContentReaderFuzz.Seed"/>). El lector solo puede devolver
/// un juego de zonas que cumple el formato o lanzar <see cref="PackageContentException"/> con un motivo.
/// </summary>
public sealed class SafeZonesHostileTests(ITestOutputHelper output)
{
    private const string Reels = """
        { "format": "rodaje.safezones", "version": 1, "id": "reels", "name": "Reels",
          "aspect": { "width": 9, "height": 16 }, "center": true,
          "zones": [
            { "label": "Botones", "rect": [0.86, 0.5, 0.14, 0.38] },
            { "label": "Nombre y pie", "kind": "interface", "rect": [0, 0.78, 0.84, 0.17] },
            { "label": "Títulos 🎬", "kind": "safe", "rect": [0.05, 0.05, 0.9, 0.9] } ] }
        """;

    private static string With(string zones, string extra = "") =>
        $$"""{ "format": "rodaje.safezones", "version": 1, "name": "Z"{{extra}}, "zones": [ {{zones}} ] }""";

    private static string Zone(string rect, string label = "\"x\"") => $$"""{ "label": {{label}}, "rect": [{{rect}}] }""";

    private static SafeZonePreset Read(string json) => ContentReaders.ReadSafeZones(ContentReaderFuzz.Utf8(json));

    private static string Error(string json) => Assert.Throws<PackageContentException>(() => Read(json)).Message;

    /// <summary>Lo que cumple todo juego de zonas aceptado (docs/formato-paquete.md, «safe-zones»).</summary>
    internal static void AssertWellFormed(SafeZonePreset preset)
    {
        Assert.InRange(preset.Name.Length, 1, SafeText.MaxName);
        ContentReaderFuzz.AssertClean(preset.Name, "name");
        Assert.True(preset.Id.Length == 0 || Identifiers.IsValid(preset.Id), $"id «{preset.Id}»");
        Assert.True((preset.AspectWidth, preset.AspectHeight) == (0, 0) || (preset.AspectWidth is >= 1 and <= 100 && preset.AspectHeight is >= 1 and <= 100));
        Assert.InRange(preset.Zones.Count, 1, 32);
        foreach (SafeZone zone in preset.Zones)
        {
            Assert.InRange(zone.Label.Length, 1, 60);
            ContentReaderFuzz.AssertClean(zone.Label, "label");
            Assert.True(Enum.IsDefined(zone.Kind));
            foreach (double v in (double[])[zone.Left, zone.Top, zone.Width, zone.Height])
            {
                Assert.True(double.IsFinite(v));
            }

            Assert.True(zone.Left >= 0 && zone.Top >= 0 && zone.Width > 0 && zone.Height > 0, zone.ToString());
            Assert.True(zone.Left + zone.Width <= 1 + 1e-9 && zone.Top + zone.Height <= 1 + 1e-9, zone.ToString());
        }
    }

    [Theory]
    [InlineData("0, 0, 1, 1")]
    [InlineData("0, 0, 4.9e-324, 4.9e-324")]
    [InlineData("0.5, 0.5, 0.5000005, 0.5")]
    [InlineData("1e-300, 0, 0.1, 0.1")]
    [InlineData("0.0, -0.0, 1.0, 1E0")]
    [InlineData("0.99999, 0, 0.00001, 1")]
    public void Acepta_los_rectangulos_en_el_borde(string rect)
    {
        SafeZonePreset preset = Read(With(Zone(rect)));
        AssertWellFormed(preset);
    }

    [Theory]
    [InlineData("0.5, 0, 0.5000011, 0.1", "se sale")]
    [InlineData("0, 0, -4.9e-324, 0.1", "se sale")]
    [InlineData("0, -1e-300, 0.1, 0.1", "se sale")]
    [InlineData("1, 0, 0.1, 0.1", "se sale")]
    [InlineData("0, 0, 1e400, 0.1", "rect[2] tiene que ser un número")]
    [InlineData("0, 0, -1e400, 0.1", "rect[2] tiene que ser un número")]
    [InlineData("0, 0, 0.1, null", "rect[3] tiene que ser un número")]
    [InlineData("0, 0, 0.1, \"0.1\"", "rect[3] tiene que ser un número")]
    [InlineData("0, 0, 0.1, [0.1]", "rect[3] tiene que ser un número")]
    [InlineData("0, 0, 0.1, 0.1, 0.1", "[x, y, ancho, alto]")]
    [InlineData("", "[x, y, ancho, alto]")]
    public void Rechaza_los_rectangulos_fuera_o_raros(string rect, string expected) =>
        Assert.Contains(expected, Error(With(Zone(rect))), StringComparison.Ordinal);

    [Theory]
    [InlineData("\"\\u202e\"")]
    [InlineData("\"\\u0000\\u0001\\u001f\"")]
    [InlineData("\"   \"")]
    [InlineData("\"\\u200e\\u200f\\u2066\\u2069\\u061c\"")]
    [InlineData("\"\\n\\t\\r\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("[\"a\"]")]
    public void Una_etiqueta_vacia_tras_limpiarla_o_que_no_es_texto_se_rechaza(string label) =>
        Assert.Contains("label tiene que tener", Error(With(Zone("0, 0, 0.1, 0.1", label))), StringComparison.Ordinal);

    [Theory]
    [InlineData("\"\\ud800\"")]
    [InlineData("\"a\\udc00b\"")]
    [InlineData("\"\\ud83d\"")]
    public void Un_sustituto_suelto_en_la_etiqueta_invalida_el_archivo(string label) =>
        Assert.Contains("no es Unicode válido", Error(With(Zone("0, 0, 0.1, 0.1", label))), StringComparison.Ordinal);

    [Fact]
    public void Una_etiqueta_con_controles_y_marcas_de_direccion_se_limpia()
    {
        SafeZonePreset preset = Read(With(Zone("0, 0, 0.1, 0.1", "\"\\u202eLogo\\u0007 \\u200fpie\\u2066\"")));
        Assert.Equal("Logo pie", preset.Zones[0].Label);
    }

    [Fact]
    public void Las_etiquetas_se_miden_en_caracteres_tras_limpiar()
    {
        // 60 letras con controles y marcas por medio: limpias son 60 y valen.
        string noisy = string.Concat(Enumerable.Repeat("a\\u202e\\u0001", 60));
        Assert.Equal(60, Read(With(Zone("0, 0, 0.1, 0.1", $"\"{noisy}\""))).Zones[0].Label.Length);
        Assert.Contains("label tiene que tener", Error(With(Zone("0, 0, 0.1, 0.1", $"\"{new string('a', 61)}\""))), StringComparison.Ordinal);

        // 30 emojis ocupan 60 unidades UTF-16: el límite cuenta unidades, así que 31 ya no caben.
        Assert.Equal(60, Read(With(Zone("0, 0, 0.1, 0.1", $"\"{string.Concat(Enumerable.Repeat("🎬", 30))}\""))).Zones[0].Label.Length);
        Assert.Contains("label tiene que tener", Error(With(Zone("0, 0, 0.1, 0.1", $"\"{string.Concat(Enumerable.Repeat("🎬", 31))}\""))), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "width": 100, "height": 1 }""", 100, 1)]
    [InlineData("""{ "width": 1, "height": 100 }""", 1, 100)]
    [InlineData("null", 0, 0)]
    public void Acepta_las_formas_en_el_borde(string aspect, int width, int height)
    {
        SafeZonePreset preset = Read(With(Zone("0, 0, 0.1, 0.1"), $", \"aspect\": {aspect}"));
        Assert.Equal((width, height), (preset.AspectWidth, preset.AspectHeight));
    }

    [Theory]
    [InlineData("""{ "width": 101, "height": 1 }""", "aspect.width queda fuera de rango")]
    [InlineData("""{ "width": 9, "height": -16 }""", "aspect.height queda fuera de rango")]
    [InlineData("""{ "width": 9.5, "height": 16 }""", "aspect.width tiene que ser un entero")]
    [InlineData("""{ "width": 9e0, "height": 16 }""", "aspect.width tiene que ser un entero")]
    [InlineData("""{ "width": 9223372036854775808, "height": 16 }""", "aspect.width tiene que ser un entero")]
    [InlineData("""{ "width": 4294967305, "height": 16 }""", "aspect.width queda fuera de rango")]
    [InlineData("""{ "width": "9", "height": 16 }""", "aspect.width tiene que ser un entero")]
    [InlineData("""{ "width": 9 }""", "aspect.height tiene que ser un entero")]
    [InlineData("""[9, 16]""", "aspect tiene que ser un objeto")]
    [InlineData("\"9:16\"", "aspect tiene que ser un objeto")]
    public void Rechaza_las_formas_fuera_de_rango(string aspect, string expected) =>
        Assert.Contains(expected, Error(With(Zone("0, 0, 0.1, 0.1"), $", \"aspect\": {aspect}")), StringComparison.Ordinal);

    [Theory]
    [InlineData("\"interface\"", SafeZoneKind.Interface)]
    [InlineData("\"safe\"", SafeZoneKind.Safe)]
    public void El_tipo_se_lee(string kind, SafeZoneKind expected) =>
        Assert.Equal(expected, Read(With($$"""{ "label": "x", "kind": {{kind}}, "rect": [0, 0, 0.1, 0.1] }""")).Zones[0].Kind);

    [Theory]
    [InlineData("\"Safe\"")]
    [InlineData("\"SAFE\"")]
    [InlineData("\"safe \"")]
    [InlineData("\"ѕafe\"")]
    [InlineData("\"\\u202esafe\"")]
    public void El_tipo_distingue_mayusculas_y_letras_parecidas(string kind) =>
        Assert.Contains("no existe", Error(With($$"""{ "label": "x", "kind": {{kind}}, "rect": [0, 0, 0.1, 0.1] }""")), StringComparison.Ordinal);

    [Theory]
    [InlineData("", "No es JSON válido")]
    [InlineData("   ", "No es JSON válido")]
    [InlineData("[]", "tiene que ser un objeto JSON")]
    [InlineData("\"rodaje.safezones\"", "tiene que ser un objeto JSON")]
    [InlineData("""{ "format": "rodaje.safezones", "version": 1, "name": "Z", "zones": [ """, "No es JSON válido")]
    [InlineData("""{ "format": "rodaje.safezones" "version": 1 }""", "No es JSON válido")]
    [InlineData("""{ "format": "rodaje.safezones", "version": 1, "name": "Z", "zones": "todas" }""", "Falta zones")]
    [InlineData("""{ "format": "rodaje.safezones", "version": 1, "name": "Z", "zones": null }""", "Falta zones")]
    [InlineData("""{ "format": "rodaje.safezones", "version": 1, "name": "Z", "zones": [ null ] }""", "zones[0] tiene que ser un objeto")]
    [InlineData("""{ "format": "rodaje.safezones", "version": 0, "name": "Z", "zones": [] }""", "version tiene que ser un entero")]
    [InlineData("""{ "format": "rodaje.safezones", "version": 1.0, "name": "Z", "zones": [] }""", "version tiene que ser un entero")]
    [InlineData("""{ "format": "rodaje.safezones", "version": "1", "name": "Z", "zones": [] }""", "version tiene que ser un entero")]
    [InlineData("""{ "format": "rodaje.SafeZones", "version": 1, "name": "Z", "zones": [] }""", "se esperaba «rodaje.safezones»")]
    [InlineData("""{ "format": "rodaje.textstyle", "version": 1, "name": "Z", "zones": [] }""", "se esperaba «rodaje.safezones»")]
    [InlineData("""{ "version": 1, "name": "Z", "zones": [] }""", "Falta format")]
    [InlineData("""{ "format": "rodaje.safezones", "version": 1, "name": "\u202e", "zones": [] }""", "Falta name")]
    [InlineData("""{ "format": "rodaje.safezones", "version": 1, "id": "Reels", "name": "Z", "zones": [] }""", "id «Reels» no es válido")]
    [InlineData("""{ "format": "rodaje.safezones", "version": 1, "id": "con", "name": "Z", "zones": [] }""", "id «con» no es válido")]
    [InlineData("""{ "format": "rodaje.safezones", "version": 1, "id": 5, "name": "Z", "zones": [] }""", "no es válido")]
    public void Rechaza_el_json_roto_o_ajeno(string json, string expected) =>
        Assert.Contains(expected, Error(json), StringComparison.Ordinal);

    [Fact]
    public void Rechaza_el_anidamiento_profundo_sin_desbordar_la_pila()
    {
        string deep = new string('[', 5000) + new string(']', 5000);
        Assert.Contains("No es JSON válido", Error(With(Zone("0, 0, 0.1, 0.1"), $", \"otro\": {deep}")), StringComparison.Ordinal);
    }

    [Fact]
    public void Un_mega_justo_se_lee_y_uno_mas_se_rechaza()
    {
        string head = $$"""{ "format": "rodaje.safezones", "version": 1, "name": "Z", "zones": [ {{Zone("0, 0, 0.1, 0.1")}} ], "pad": """ + "\"";
        int room = PresetReader.MaxFileBytes - Encoding.UTF8.GetByteCount(head) - 3;
        string exact = head + new string('x', room) + "\" }";
        string over = head + new string('x', room + 1) + "\" }";
        Assert.Equal(PresetReader.MaxFileBytes, Encoding.UTF8.GetByteCount(exact));
        Assert.Single(Read(exact).Zones);
        Assert.Contains("1 MB", Error(over), StringComparison.Ordinal);

        // Sin poder medir el flujo (una tubería, un zip): también corta al pasar de 1 MB.
        Assert.Contains("1 MB", Assert.Throws<PackageContentException>(() => ContentReaders.ReadSafeZones(new UnseekableStream(Encoding.UTF8.GetBytes(over)))).Message, StringComparison.Ordinal);
        Assert.Single(ContentReaders.ReadSafeZones(new UnseekableStream(Encoding.UTF8.GetBytes(exact))).Zones);
    }

    [Theory]
    [InlineData("[0, 0, 0.1, 0.1]", "[0.5, 0.5, 0.5, 0.5]")]
    [InlineData("[0, 0, 0.1, 0.1]", "[0.5, 0.5, 0.9, 0.5]")]
    [InlineData("[0.5, 0.5, 0.9, 0.5]", "[0, 0, 0.1, 0.1]")]
    public void Las_propiedades_repetidas_no_rompen_las_reglas(string first, string second)
    {
        // JsonDocument se queda con una de las dos: sea cual sea, o se rechaza o el resultado cumple el formato.
        string json = $$"""{ "format": "rodaje.safezones", "version": 1, "name": "Z", "zones": [ { "label": "x", "rect": {{first}}, "rect": {{second}} } ] }""";
        try
        {
            AssertWellFormed(Read(json));
        }
        catch (PackageContentException ex)
        {
            Assert.Contains("se sale", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Treinta_y_dos_zonas_de_sesenta_caracteres_se_leen()
    {
        string zones = string.Join(", ", Enumerable.Range(0, 32).Select(i =>
            Zone(string.Create(CultureInfo.InvariantCulture, $"{i / 32.0}, 0, {1 / 32.0}, 1"), $"\"{new string((char)('a' + (i % 26)), 60)}\"")));
        SafeZonePreset preset = Read(With(zones));
        Assert.Equal(32, preset.Zones.Count);
        AssertWellFormed(preset);
    }

    /// <summary>Mutaciones al azar de un archivo válido: un juego de zonas que cumple el formato o un error con motivo.</summary>
    [Fact]
    public void Mutaciones_al_azar_dan_zonas_validas_o_un_error()
    {
        const int Cases = 4000;
        byte[] original = Encoding.UTF8.GetBytes(Reels);
        int accepted = 0;
        for (int i = 0; i < Cases; i++)
        {
            var random = new Random(ContentReaderFuzz.Seed + i);
            byte[] data = ContentReaderFuzz.Mutate(random, original);
            try
            {
                AssertWellFormed(ContentReaders.ReadSafeZones(new MemoryStream(data)));
                accepted++;
            }
            catch (PackageContentException ex)
            {
                Assert.False(string.IsNullOrWhiteSpace(ex.Message));
            }
            catch (Exception ex)
            {
                Assert.Fail($"[semilla {ContentReaderFuzz.Seed + i}] {ex.GetType().Name}: {ex.Message}\n{ContentReaderFuzz.Show(data)}");
            }
        }

        output.WriteLine($"{accepted} de {Cases} mutaciones se aceptaron.");
        Assert.InRange(accepted, Cases / 50, Cases - (Cases / 20));
    }

    /// <summary>
    /// Ida y vuelta con el escritor para juegos al azar con valores de 4 decimales (los que guarda el editor del visor):
    /// lo escrito se lee igual.
    /// </summary>
    [Fact]
    public void El_escritor_devuelve_lo_mismo_con_cuatro_decimales()
    {
        for (int i = 0; i < 2000; i++)
        {
            var random = new Random(ContentReaderFuzz.Seed + 10_000 + i);
            SafeZonePreset preset = RandomPreset(random, decimals: 4);
            byte[] json = ContentReaders.WriteSafeZones(preset, random.Next(2) == 0 ? null : "safe-zones");
            SafeZonePreset back;
            try
            {
                back = ContentReaders.ReadSafeZones(new MemoryStream(json));
            }
            catch (PackageContentException ex)
            {
                Assert.Fail($"[semilla {ContentReaderFuzz.Seed + 10_000 + i}] {ex.Message}\n{Encoding.UTF8.GetString(json)}");
                throw;
            }

            Assert.Equal(preset with { Zones = [] }, back with { Zones = [] });
            Assert.Equal(preset.Zones.Count, back.Zones.Count);
            foreach ((SafeZone a, SafeZone b) in preset.Zones.Zip(back.Zones))
            {
                // El lector recorta el ancho a 1 − x; con decimales binarios eso mueve el último bit.
                Assert.Equal((a.Label, a.Kind), (b.Label, b.Kind));
                Assert.Equal(a.Left, b.Left, 1e-12);
                Assert.Equal(a.Top, b.Top, 1e-12);
                Assert.Equal(a.Width, b.Width, 1e-12);
                Assert.Equal(a.Height, b.Height, 1e-12);
            }
        }
    }

    /// <summary>
    /// Defecto: <c>WriteSafeZones</c> redondea cada número a 4 decimales por separado, así que una zona que el lector aceptó
    /// puede no volver a leerse. Pegada al borde derecho (x + ancho = 1) con más de 4 decimales, como las que deja el editor
    /// al arrastrar: x = 0,98415 y ancho = 1 − x se escriben 0,9842 y 0,0159, que suman 1,0001 y «se salen de la imagen».
    /// Un ancho menor que 0,00005 se escribe 0 y tampoco se lee. Con la semilla 20262012 sale en 11 de 1000 (la primera, x = 0,92555)
    /// zonas pegadas al borde. Lo que debería pasar: el escritor da un archivo que su lector acepta (por ejemplo,
    /// redondeando x + ancho y no el ancho, o sin redondear).
    /// </summary>
    [Fact]
    public void El_escritor_da_un_archivo_que_su_lector_acepta()
    {
        AssertRoundTrip(new SafeZone("Borde", SafeZoneKind.Interface, 0.98415, 0, 1 - 0.98415, 0.5));
        AssertRoundTrip(new SafeZone("Fina", SafeZoneKind.Safe, 0.5, 0.5, 0.00004, 0.1));
        var random = new Random(20262012);
        for (int i = 0; i < 1000; i++)
        {
            double x = Math.Round(random.NextDouble(), 5);
            if (x < 1)
            {
                AssertRoundTrip(new SafeZone("Borde", SafeZoneKind.Interface, x, 0, 1 - x, 0.5));
            }
        }

        static void AssertRoundTrip(SafeZone zone)
        {
            SafeZonePreset preset = new("", "Z", 0, 0, [zone], false);
            AssertWellFormed(ContentReaders.ReadSafeZones(new MemoryStream(ContentReaders.WriteSafeZones(preset))));
            AssertWellFormed(ContentReaders.ReadSafeZones(new MemoryStream(ContentReaders.WriteSafeZones(
                ContentReaders.ReadSafeZones(new MemoryStream(ContentReaders.WriteSafeZones(preset)))))));
        }
    }

    /// <summary>Un juego de zonas válido al azar, con valores de <paramref name="decimals"/> decimales.</summary>
    private static SafeZonePreset RandomPreset(Random random, int decimals)
    {
        double Fraction(double max) => Math.Min(max, Math.Round(random.NextDouble() * max, decimals));
        var zones = new List<SafeZone>();
        for (int z = random.Next(1, 33); z > 0; z--)
        {
            double unit = Math.Pow(10, -decimals);
            double x = Fraction(1 - unit);
            double y = Fraction(1 - unit);
            double w = Math.Max(unit, Fraction(Math.Round(1 - x, decimals)));
            double h = Math.Max(unit, Fraction(Math.Round(1 - y, decimals)));
            string label = new([.. Enumerable.Range(0, random.Next(1, 61)).Select(_ => "abcñ éxyz"[random.Next(9)])]);
            label = SafeText.Clean(label);
            zones.Add(new SafeZone(label.Length == 0 ? "z" : label, random.Next(2) == 0 ? SafeZoneKind.Interface : SafeZoneKind.Safe, x, y, w, h));
        }

        (int aw, int ah) = random.Next(3) == 0 ? (0, 0) : (random.Next(1, 101), random.Next(1, 101));
        return new SafeZonePreset(random.Next(2) == 0 ? "" : "zonas-" + random.Next(1000).ToString(CultureInfo.InvariantCulture), "Zonas " + random.Next(100).ToString(CultureInfo.InvariantCulture), aw, ah, zones, random.Next(2) == 0);
    }

    /// <summary>Un flujo que no se puede medir ni mover, como una entrada de zip o una tubería.</summary>
    private sealed class UnseekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 7_001));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
