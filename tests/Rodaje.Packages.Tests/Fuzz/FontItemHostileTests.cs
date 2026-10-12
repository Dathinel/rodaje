using System.Text.Json;

namespace Rodaje.Packages.Tests.Fuzz;

/// <summary>
/// Casos hostiles del elemento <c>font</c> del manifiesto (D-029, docs/formato-paquete.md «Fuentes»): rutas que salen
/// del paquete o confunden a Windows, extensiones, cantidades y tamaños en el borde, licencia, Unicode y un fuzz de
/// listas de archivos con semilla fija (<see cref="ContentReaderFuzz.Seed"/>). Un manifiesto con fuentes solo puede
/// ser inválido con su error en español o válido con archivos que <c>TitleFontFiles</c> puede registrar sin salir de
/// la carpeta.
/// </summary>
public sealed class FontItemHostileTests
{
    private static string Font(string files, string license = "\"OFL-1.1\"", string extra = "") =>
        $$"""[{ "id": "inter", "kind": "font", "name": "Inter", "license": {{license}}, "files": {{files}}{{extra}} }]""";

    private static string Files(params string[] names) => JsonSerializer.Serialize(names);

    private static ManifestResult Read(string items, string? directory = null) =>
        ManifestReader.Read(Samples.Manifest(items: items), directory);

    private static string Errors(ManifestResult result)
    {
        Assert.False(result.IsValid, "se esperaba un manifiesto inválido");
        return string.Join(" ", result.Errors);
    }

    /// <summary>Lo que cumple todo elemento <c>font</c> de un manifiesto válido.</summary>
    private static void AssertWellFormed(ManifestResult result, string? directory)
    {
        foreach (PackageItem font in result.Manifest!.Items.Where(i => i.Kind == ItemKind.Font))
        {
            Assert.False(string.IsNullOrWhiteSpace(font.License), "una fuente sin licencia");
            Assert.NotNull(font.Files);
            Assert.InRange(font.Files.Count, 1, PackageItem.MaxFontFiles);
            foreach (string relative in font.Files)
            {
                Assert.Contains(Path.GetExtension(relative).ToLowerInvariant(), PackageItem.FontExtensions);
                Assert.True(SafePaths.TryNormalizeRelative(relative, out string? normalized), relative);
                if (directory is not null)
                {
                    string full = Path.GetFullPath(Path.Combine(directory, normalized));
                    Assert.True(SafePaths.IsInside(full, directory) && File.Exists(full), relative);
                }
            }
        }
    }

    public static TheoryData<string> HostilePaths() =>
    [
        "../inter.ttf", "..\\inter.ttf", "fuentes/../../inter.ttf", "/etc/inter.ttf", "\\inter.ttf", "C:\\Windows\\Fonts\\arial.ttf", "C:arial.ttf",
        "\\\\servidor\\fuentes\\inter.ttf", "//servidor/fuentes/inter.ttf", "fuentes//inter.ttf", "./inter.ttf", "fuentes/./inter.ttf",
        "inter.ttf:secreto", "inter.ttf::$DATA", "con.ttf", "NUL.otf", "fuentes/aux.ttc", "COM1.ttf", "lpt¹.otf", "inter.ttf.",
        "fuentes /inter.ttf", "int?er.ttf", "int*er.ttf", "int\"er.ttf", "int<er>.ttf", "int|er.ttf",
        new string('a', 237) + ".ttf", string.Join('/', Enumerable.Repeat("d", 120)) + "/f.ttf",
    ];

    [Theory]
    [MemberData(nameof(HostilePaths))]
    public void Las_rutas_que_salen_o_confunden_a_windows_se_rechazan(string path)
    {
        string errors = Errors(Read(Font(Files(path))));
        Assert.Contains("inter", errors, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("inter.TTF")]
    [InlineData("inter.Otf")]
    [InlineData("coleccion.ttc")]
    [InlineData("coleccion.OTC")]
    [InlineData("fuentes/ñandú/Inter Display-Bold Italic.ttf")]
    [InlineData("フォント/ヒラギノ.otf")]
    [InlineData("a.b.c.ttf")]
    [InlineData(".ttf")]
    public void Las_extensiones_de_fuente_valen_en_cualquier_caja(string path)
    {
        ManifestResult result = Read(Font(Files(path)));
        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.Equal([path], Assert.Single(result.Manifest!.Items).Files);
    }

    [Theory]
    [InlineData("inter.woff2")]
    [InlineData("inter.ttf.exe")]
    [InlineData("inter.ttf.dll")]
    [InlineData("inter")]
    [InlineData("inter.ttf/")]
    [InlineData("inter.fon")]
    [InlineData("inter.pfb")]
    [InlineData("inter.ｔｔｆ")]
    [InlineData("fuentes/")]
    public void Lo_que_no_es_ttf_otf_ttc_u_otc_se_rechaza(string path) =>
        Assert.False(Read(Font(Files(path))).IsValid);

    [Theory]
    [InlineData("\"inter.ttf\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("[[\"inter.ttf\"]]")]
    [InlineData("[\"inter.ttf\", 2]")]
    [InlineData("[]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"  \"]")]
    public void Files_tiene_que_ser_una_lista_de_rutas(string files) =>
        Assert.False(Read(Font(files)).IsValid);

    [Fact]
    public void De_uno_a_treinta_y_dos_archivos()
    {
        string[] names = [.. Enumerable.Range(0, 33).Select(i => $"f{i}.ttf")];
        Assert.True(Read(Font(Files(names[..32]))).IsValid);
        Assert.Contains("de 1 a 32", Errors(Read(Font(Files(names)))), StringComparison.Ordinal);

        // Repetidos cuentan una vez: 32 distintos y uno repetido valen.
        ManifestResult repeated = Read(Font(Files([.. names[..32], "f0.ttf"])));
        Assert.True(repeated.IsValid);
        Assert.Equal(32, repeated.Manifest!.Items[0].Files!.Count);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("[\"OFL-1.1\"]")]
    [InlineData("{}")]
    public void Una_licencia_que_no_es_texto_invalida_la_fuente(string license) =>
        Assert.Contains("license", Errors(Read(Font(Files("inter.ttf"), license))), StringComparison.Ordinal);

    [Fact]
    public void Una_licencia_de_mas_de_64_caracteres_se_rechaza() =>
        Assert.Contains("license", Errors(Read(Font(Files("inter.ttf"), $"\"{new string('L', 65)}\""))), StringComparison.Ordinal);

    [Fact]
    public void File_sirve_para_una_sola_fuente_y_files_manda_si_estan_los_dos()
    {
        ManifestResult single = ManifestReader.Read(Samples.Manifest(items: """[{ "id": "a", "kind": "font", "name": "A", "license": "OFL-1.1", "file": "a.otf" }]"""));
        Assert.Equal(["a.otf"], Assert.Single(single.Manifest!.Items).Files);

        ManifestResult both = Read(Font(Files("b.ttf"), extra: ", \"file\": \"a.otf\""));
        Assert.Equal(["b.ttf"], Assert.Single(both.Manifest!.Items).Files);

        // Un file que no es fuente no se registra: no está en Files.
        ManifestResult other = Read(Font(Files("b.ttf"), extra: ", \"file\": \"leeme.txt\""));
        Assert.True(other.IsValid);
        Assert.Equal(["b.ttf"], other.Manifest!.Items[0].Files);

        Assert.Contains("no es un archivo de fuente", Errors(ManifestReader.Read(Samples.Manifest(items: """[{ "id": "a", "kind": "font", "name": "A", "license": "OFL-1.1", "file": "a.exe" }]"""))), StringComparison.Ordinal);
    }

    [Fact]
    public void Con_la_carpeta_comprueba_que_cada_archivo_exista_y_no_sea_una_carpeta()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.Combine("fuentes", "carpeta.ttf"));
        File.WriteAllBytes(folder.Combine("fuentes", "a.ttf"), [1, 2, 3]);
        File.WriteAllBytes(folder.Combine("fuera.ttf"), [1]);

        Assert.True(Read(Font(Files("fuentes/a.ttf")), folder.Path).IsValid);
        Assert.True(Read(Font(Files("fuentes\\a.ttf")), folder.Path).IsValid);
        Assert.Contains("falta el archivo", Errors(Read(Font(Files("fuentes/carpeta.ttf")), folder.Path)), StringComparison.Ordinal);
        Assert.Contains("falta el archivo", Errors(Read(Font(Files("fuentes/b.ttf")), folder.Path)), StringComparison.Ordinal);
        Assert.False(Read(Font(Files("fuentes/../fuera.ttf")), folder.Path).IsValid);
    }

    [Fact]
    public void Cien_megas_justos_valen_y_un_byte_mas_no()
    {
        using var folder = new TempFolder();
        string[] names = [.. Enumerable.Range(0, 6).Select(i => $"f{i}.ttf")];
        for (int i = 0; i < 5; i++)
        {
            using FileStream file = File.Create(folder.Combine(names[i]));
            file.SetLength(PackageItem.MaxFontFileBytes);
        }

        using (FileStream small = File.Create(folder.Combine(names[5])))
        {
            small.SetLength(1);
        }

        Assert.True(Read(Font(Files(names[..5])), folder.Path).IsValid);
        Assert.Contains("100 MB", Errors(Read(Font(Files(names)), folder.Path)), StringComparison.Ordinal);
    }

    [Fact]
    public void Un_sustituto_suelto_en_una_ruta_invalida_el_manifiesto()
    {
        string items = """[{ "id": "inter", "kind": "font", "name": "Inter", "license": "OFL-1.1", "files": ["inter\ud800.ttf"] }]""";
        Assert.Contains("no es Unicode válido", Errors(ManifestReader.Read(Samples.Manifest(items: items))), StringComparison.Ordinal);
    }

    /// <summary>
    /// Listas de archivos armadas al azar con trozos válidos y hostiles: el manifiesto es inválido con sus errores, o
    /// válido con rutas de fuente que no salen de la carpeta (y que existen, cuando se da la carpeta).
    /// </summary>
    [Fact]
    public void Listas_de_archivos_al_azar_cumplen_las_reglas_o_se_rechazan()
    {
        string[] parts = ["fuentes", "a", "Inter-Bold", "..", ".", "", " ", "con", "x:y", "ñ", "🎬", "a\u202Eb", "a\u0001b", "C:", "\\", "nul.txt", "x.", "d d"];
        string[] extensions = [".ttf", ".otf", ".TTC", ".otc", ".exe", "", ".ttf.exe", ".woff"];
        using var folder = new TempFolder();
        File.WriteAllBytes(folder.Combine("a.ttf"), [0]);
        Directory.CreateDirectory(folder.Combine("fuentes"));
        File.WriteAllBytes(folder.Combine("fuentes", "a.otf"), [0]);
        int valid = 0;
        for (int i = 0; i < 1500; i++)
        {
            int seed = ContentReaderFuzz.Seed + 60_000 + i;
            var random = new Random(seed);
            string[] files = [.. Enumerable.Range(0, random.Next(0, 5)).Select(_ =>
                string.Join(random.Next(4) == 0 ? "\\" : "/", Enumerable.Range(0, random.Next(1, 4)).Select(_ => parts[random.Next(parts.Length)])) + extensions[random.Next(extensions.Length)])];
            if (random.Next(4) == 0)
            {
                files = [.. files, random.Next(2) == 0 ? "a.ttf" : "fuentes/a.otf"];
            }

            string? directory = random.Next(2) == 0 ? folder.Path : null;
            ManifestResult result;
            try
            {
                result = Read(Font(Files(files)), directory);
            }
            catch (Exception ex)
            {
                Assert.Fail($"[semilla {seed}] {ex.GetType().Name}: {ex.Message} con {Files(files)}");
                throw;
            }

            if (result.IsValid)
            {
                valid++;
                AssertWellFormed(result, directory);
            }
            else
            {
                Assert.All(result.Errors, e => Assert.False(string.IsNullOrWhiteSpace(e)));
            }
        }

        Assert.True(valid > 0);
    }

    /// <summary>
    /// Defecto: <c>FontFiles</c> solo comprueba que <c>license</c> sea un texto, sin mirar que quede algo tras limpiarlo.
    /// Una fuente con <c>"license": ""</c>, <c>"   "</c> o solo marcas de dirección es válida y queda con
    /// <c>License = null</c>, aunque formato-paquete.md dice que «license es obligatoria» porque «una fuente de terceros
    /// no tiene la licencia del paquete». Lo que debería pasar: el mismo error que sin <c>license</c>.
    /// </summary>
    [Fact(Skip = "Defecto: una fuente con \"license\": \"\" (o solo espacios o marcas bidi) es válida y queda sin licencia.")]
    public void Una_fuente_con_licencia_vacia_no_es_valida()
    {
        foreach (string license in new[] { "\"\"", "\"   \"", "\"\\u202e\"", "\"\\u0000\"" })
        {
            Assert.Contains("license", Errors(Read(Font(Files("inter.ttf"), license))), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Defecto: las rutas de <c>files</c> (y de <c>file</c>, de cualquier elemento) pasan por <c>SafeText.Clean</c> antes
    /// de comprobarse, así que una ruta con una marca de dirección o un control (<c>fuentes/x\u202Efdp.ttf</c>,
    /// <c>a\u0001.ttf</c>, <c>"\tinter.ttf"</c>, <c>" inter.ttf"</c>) no se rechaza: se convierte en otra (<c>fuentes/xfdp.ttf</c>,
    /// <c>a.ttf</c>, <c>inter.ttf</c>) y, si esa existe, se acepta y se registra un archivo distinto del escrito.
    /// formato-paquete.md, «Textos»: «las rutas que los traen se rechazan». Lo que debería pasar: rechazarla como hace
    /// <c>SafePaths.TryNormalizeRelative</c> con la ruta original.
    /// </summary>
    [Fact(Skip = "Defecto: una ruta de files o file con marcas bidi o controles se limpia en vez de rechazarse (fuentes/x\\u202Efdp.ttf pasa como fuentes/xfdp.ttf).")]
    public void Una_ruta_con_marcas_de_direccion_o_controles_se_rechaza()
    {
        foreach (string path in new[] { "fuentes/x\u202Efdp.ttf", "a\u0001.ttf", "\tinter.ttf", "inter\u200F.otf", " inter.ttf", "inter.ttf " })
        {
            Assert.False(Read(Font(Files(path))).IsValid, path);
            string file = JsonSerializer.Serialize(path);
            Assert.False(ManifestReader.Read(Samples.Manifest(items: $$"""[{ "id": "a", "kind": "font", "name": "A", "license": "OFL-1.1", "file": {{file}} }]""")).IsValid, path);
            Assert.False(ManifestReader.Read(Samples.Manifest(items: $$"""[{ "id": "d", "kind": "transition", "name": "D", "file": {{file.Replace(".ttf", ".json", StringComparison.Ordinal).Replace(".otf", ".json", StringComparison.Ordinal)}} }]""")).IsValid, path);
        }
    }
}
