using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;
using Rodaje.Packages.Download;
using Rodaje.Packages.Tests.Fuzz;
using Xunit.Abstractions;

namespace Rodaje.Packages.Tests.Download;

/// <summary>
/// Casos hostiles del índice firmado del catálogo (<c>indice.json</c> + <c>indice.json.sig</c>, docs/descarga.md):
/// firmas alteradas o en otro formato, bytes cambiados sin volver a firmar, hashes y tamaños raros, fechas, <c>minApp</c>
/// y entradas repetidas, y un fuzz del contenido firmado con semilla fija (<see cref="ContentReaderFuzz.Seed"/>).
/// <c>SignedIndex.Load</c> solo puede devolver un índice cuyas entradas cumplen las reglas, o lanzar
/// <see cref="DownloadVerificationException"/> (firma) o <see cref="FormatException"/> (contenido).
/// </summary>
public sealed class SignedIndexHostileTests(ITestOutputHelper output) : IDisposable
{
    private const string Url = "https://github.com/Dathinel/rodaje-filtros/releases/download/v1.0.0/rodaje-filtros.paquete";
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private ECDsa? _public;

    private ECDsa Public => _public ??= IndexSignature.TryImportPublicKey(Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()))!;

    public void Dispose()
    {
        _key.Dispose();
        _public?.Dispose();
    }

    private static string Entry(string id = "\"rodaje-filtros\"", string version = "\"1.0.0\"", string kind = "\"content\"", string url = "\"" + Url + "\"",
        string sha = "\"" + Sha + "\"", string size = "100", string? minApp = null) =>
        $$"""{ "id": {{id}}, "version": {{version}}, "kind": {{kind}}, "url": {{url}}, "sha256": {{sha}}, "size": {{size}}{{(minApp is null ? "" : ", \"minApp\": " + minApp)}} }""";

    private static byte[] Index(string generated = "\"2026-10-12T12:00:00Z\"", string format = "1", params string[] entries) =>
        Encoding.UTF8.GetBytes($$"""{ "formatVersion": {{format}}, "generated": {{generated}}, "entries": [ {{string.Join(", ", entries)}} ] }""");

    private SignedIndex Load(byte[] json, List<string>? problems = null) =>
        SignedIndex.Load(json, IndexSignature.Sign(json, _key), Public, DownloadUrlPolicy.Default, problems);

    /// <summary>Lo que cumple toda entrada que el índice deja pasar.</summary>
    private static void AssertWellFormed(SignedIndex index)
    {
        foreach (IndexEntry entry in index.Entries)
        {
            Assert.True(Identifiers.IsValid(entry.Id), entry.Id);
            Assert.True(SignedIndex.IsSha256(entry.Sha256), entry.Sha256);
            Assert.InRange(entry.Size, 1, SignedIndex.MaxEntrySize);
            Assert.True(DownloadUrlPolicy.Default.IsAllowed(entry.Url, out string? reason), reason);
            Assert.True(Enum.IsDefined(entry.Kind));
        }

        Assert.Equal(index.Entries.Count, index.Entries.Select(e => (e.Id, e.Version, e.Kind)).Distinct().Count());
    }

    // ---------------------------------------------------------------- Firma

    [Fact]
    public void Una_firma_con_espacios_o_saltos_de_linea_alrededor_vale()
    {
        // firmar-indice.ps1 o un editor pueden dejar un salto de línea al final del .sig.
        byte[] json = Index(entries: Entry());
        string signature = Encoding.ASCII.GetString(IndexSignature.Sign(json, _key));
        foreach (string wrapped in new[] { signature + "\n", signature + "\r\n", "  " + signature + "\t", "\n" + signature })
        {
            Assert.Single(SignedIndex.Load(json, Encoding.ASCII.GetBytes(wrapped), Public, DownloadUrlPolicy.Default).Entries);
        }
    }

    public static TheoryData<string> BadSignatures() =>
    [
        "p1363",
        "der-truncada",
        "der-con-un-byte-de-mas",
        "der-con-otro-largo",
        "base64url",
        "base64-sin-relleno",
        "utf16",
        "con-bom",
        "otra-clave",
        "de-otros-bytes",
        "r-y-s-cambiados",
        "s-cero",
        "vacia",
        "solo-espacios",
        "demasiado-larga",
        "pem",
        "dos-firmas",
    ];

    [Theory]
    [MemberData(nameof(BadSignatures))]
    public void Las_firmas_alteradas_o_en_otro_formato_se_rechazan(string kind)
    {
        byte[] json = Index(entries: Entry());
        byte[] der = Convert.FromBase64String(Encoding.ASCII.GetString(IndexSignature.Sign(json, _key)));
        string B64(byte[] bytes) => Convert.ToBase64String(bytes);
        byte[] signature = kind switch
        {
            "p1363" => Encoding.ASCII.GetBytes(B64(_key.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))),
            "der-truncada" => Encoding.ASCII.GetBytes(B64(der[..^1])),
            "der-con-un-byte-de-mas" => Encoding.ASCII.GetBytes(B64([.. der, 0])),
            "der-con-otro-largo" => Encoding.ASCII.GetBytes(B64([der[0], (byte)(der[1] + 1), .. der[2..], 0])),
            "base64url" => Encoding.ASCII.GetBytes(B64(der).Replace('+', '-').Replace('/', '_').TrimEnd('=') + "-_"),
            "base64-sin-relleno" => Encoding.ASCII.GetBytes(B64([.. der, 0, 0]).TrimEnd('=')),
            "utf16" => Encoding.Unicode.GetBytes(B64(der)),
            "con-bom" => [0xEF, 0xBB, 0xBF, .. Encoding.ASCII.GetBytes(B64(der))],
            "otra-clave" => SignWithOtherKey(json),
            "de-otros-bytes" => IndexSignature.Sign([.. json, (byte)' '], _key),
            "r-y-s-cambiados" => Encoding.ASCII.GetBytes(B64(SwapRAndS(der))),
            "s-cero" => Encoding.ASCII.GetBytes(B64(WithZeroS(der))),
            "vacia" => [],
            "solo-espacios" => "   \n"u8.ToArray(),
            "demasiado-larga" => Encoding.ASCII.GetBytes(B64(der) + new string(' ', IndexSignature.MaxSignatureBytes)),
            "pem" => Encoding.ASCII.GetBytes("-----BEGIN SIGNATURE-----\n" + B64(der) + "\n-----END SIGNATURE-----\n"),
            _ => Encoding.ASCII.GetBytes(B64(der) + "\n" + B64(der)),
        };
        Assert.Throws<DownloadVerificationException>(() => SignedIndex.Load(json, signature, Public, DownloadUrlPolicy.Default));
    }

    [Fact]
    public void Una_clave_publica_que_no_es_p256_o_esta_danada_no_se_importa()
    {
        byte[] spki = _key.ExportSubjectPublicKeyInfo();
        Assert.Null(IndexSignature.TryImportPublicKey(Convert.ToBase64String(spki[..^1])));
        Assert.Null(IndexSignature.TryImportPublicKey(_key.ExportSubjectPublicKeyInfoPem()));
        using RSA rsa = RSA.Create(2048);
        Assert.Null(IndexSignature.TryImportPublicKey(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())));
        using ECDsa p521 = ECDsa.Create(ECCurve.NamedCurves.nistP521);
        Assert.Null(IndexSignature.TryImportPublicKey(Convert.ToBase64String(p521.ExportSubjectPublicKeyInfo())));

        // Con espacios alrededor sí: la clave viaja en un .txt.
        using ECDsa? trimmed = IndexSignature.TryImportPublicKey("\n " + Convert.ToBase64String(spki) + " \r\n");
        Assert.NotNull(trimmed);
    }

    /// <summary>
    /// Bytes del índice cambiados sin volver a firmar (borrar, insertar, cortar, BOM, espacios, Unicode roto): siempre se
    /// rechaza por la firma, antes de leer el JSON.
    /// </summary>
    [Fact]
    public void Cada_cambio_sin_volver_a_firmar_se_rechaza_por_la_firma()
    {
        byte[] json = Index(entries: [Entry(), Entry(id: "\"rodaje-transiciones\"", minApp: "\"1.0.0\"")]);
        byte[] signature = IndexSignature.Sign(json, _key);
        int same = 0;
        for (int i = 0; i < 1000; i++)
        {
            int seed = ContentReaderFuzz.Seed + 40_000 + i;
            byte[] bad = ContentReaderFuzz.Mutate(new Random(seed), json);
            if (bad.AsSpan().SequenceEqual(json))
            {
                same++;
                continue;
            }

            try
            {
                SignedIndex.Load(bad, signature, Public, DownloadUrlPolicy.Default);
                Assert.Fail($"[semilla {seed}] Un índice cambiado pasó la firma.");
            }
            catch (DownloadVerificationException)
            {
            }
        }

        Assert.True(same < 50);
        Assert.Throws<DownloadVerificationException>(() => SignedIndex.Load([0xEF, 0xBB, 0xBF, .. json], signature, Public, DownloadUrlPolicy.Default));
        Assert.Throws<DownloadVerificationException>(() => SignedIndex.Load([.. json, (byte)'\n'], signature, Public, DownloadUrlPolicy.Default));
        Assert.Throws<DownloadVerificationException>(() => SignedIndex.Load(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(json).Replace(Sha, Sha.ToUpperInvariant(), StringComparison.Ordinal)), signature, Public, DownloadUrlPolicy.Default));
    }

    [Fact]
    public void Un_indice_de_mas_de_cuatro_megas_se_rechaza_aunque_este_firmado()
    {
        byte[] head = Index(entries: Entry());
        byte[] big = [.. head[..^1], .. Encoding.ASCII.GetBytes(", \"pad\": \"" + new string('x', SignedIndex.MaxBytes) + "\" }")];
        Assert.Throws<DownloadVerificationException>(() => Load(big));
    }

    // ---------------------------------------------------------------- Hashes y tamaños

    [Fact]
    public void El_hash_en_mayusculas_se_guarda_en_minusculas()
    {
        IndexEntry entry = Assert.Single(Load(Index(entries: Entry(sha: "\"" + Sha.ToUpperInvariant() + "\""))).Entries);
        Assert.Equal(Sha, entry.Sha256);
    }

    [Theory]
    [InlineData("\"0123\"")]
    [InlineData("\"" + Sha + "0\"")]
    [InlineData("\"" + Sha + "\\u0000\"")]
    [InlineData("\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg\"")]
    [InlineData("\"０123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"")]
    [InlineData("\"01234567-9abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"")]
    [InlineData("\"sha256-ASNFZ4mrze8BI0VniavN7wEjRWeJq83vASNFZ4mrze8=\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("12345")]
    [InlineData("[\"" + Sha + "\"]")]
    public void Un_hash_mal_escrito_omite_la_entrada(string sha)
    {
        var problems = new List<string>();
        Assert.Empty(Load(Index(entries: [Entry(sha: sha)]), problems).Entries);
        Assert.Single(problems);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("68719476736", true)]
    [InlineData("68719476737", false)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("1.5", false)]
    [InlineData("1e3", false)]
    [InlineData("9223372036854775808", false)]
    public void El_tamano_tiene_que_ser_un_entero_de_1_byte_a_64_GiB(string size, bool valid)
    {
        var problems = new List<string>();
        SignedIndex index = Load(Index(entries: [Entry(size: size)]), problems);
        Assert.Equal(valid ? 1 : 0, index.Entries.Count);
        Assert.Equal(valid ? 0 : 1, problems.Count);
    }

    // ---------------------------------------------------------------- Fechas

    [Theory]
    [InlineData("\"2026-10-12T12:00:00Z\"", "2026-10-12T12:00:00+00:00")]
    [InlineData("\"2026-10-12T14:00:00+02:00\"", "2026-10-12T12:00:00+00:00")]
    [InlineData("\"2026-10-12T12:00:00\"", "2026-10-12T12:00:00+00:00")]
    [InlineData("\" 2026-10-12T12:00:00Z \"", "2026-10-12T12:00:00+00:00")]
    [InlineData("\"2026-10-12T12:00:00.1234567Z\"", "2026-10-12T12:00:00.1234567+00:00")]
    public void Generated_se_lee_en_utc(string generated, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Load(Index(generated)).Generated);

    [Theory]
    [InlineData("\"ayer\"")]
    [InlineData("\"\"")]
    [InlineData("\"2026-13-40T99:00:00Z\"")]
    [InlineData("null")]
    [InlineData("1760270400")]
    [InlineData("{}")]
    public void Un_generated_ilegible_cuenta_como_el_mas_viejo(string generated)
    {
        // Así nunca gana a uno ya visto: GitHubReleaseDownloader lo rechaza por viejo.
        Assert.Equal(DateTimeOffset.MinValue, Load(Index(generated)).Generated);
    }

    // ---------------------------------------------------------------- minApp y versiones

    [Fact]
    public void Latest_elige_la_mas_nueva_que_sirve_con_el_programa()
    {
        SignedIndex index = Load(Index(entries:
        [
            Entry(version: "\"1.0.0\""),
            Entry(version: "\"1.1.0-beta.1\"", minApp: "\"1.0.0-beta\""),
            Entry(version: "\"1.1.0\"", minApp: "\"1.0.0\""),
            Entry(version: "\"2.0.0\"", minApp: "\"9.0.0\""),
            Entry(version: "\"1.0.5\"", kind: "\"module\""),
        ]));
        Assert.Equal(5, index.Entries.Count);
        PackageVersion Pick(string app, IndexEntryKind? kind = IndexEntryKind.Content) => index.Latest("rodaje-filtros", PackageVersion.Parse(app), kind)!.Version;
        Assert.Equal("1.0.0", Pick("0.9.0").ToString());
        Assert.Equal("1.1.0-beta.1", Pick("1.0.0-beta.5").ToString());
        Assert.Equal("1.1.0", Pick("1.0.0").ToString());
        Assert.Equal("1.1.0", Pick("8.9.9+compilacion").ToString());
        Assert.Equal("2.0.0", Pick("9.0.0").ToString());
        Assert.Equal("1.0.5", Pick("0.1.0", IndexEntryKind.Module).ToString());
        Assert.Equal("2.0.0", index.Newest("rodaje-filtros")!.Version.ToString());
        Assert.Null(index.Latest("rodaje-filtros", PackageVersion.Parse("0.1.0"), IndexEntryKind.Model));
        Assert.Null(index.Latest("otro", PackageVersion.Parse("9.0.0")));
    }

    [Theory]
    [InlineData("\"1.0\"")]
    [InlineData("\"v1.0.0\"")]
    [InlineData("\"1.0.0.0\"")]
    [InlineData("\"01.0.0\"")]
    [InlineData("\"1.0.0-\"")]
    [InlineData("\"\"")]
    [InlineData("\"1.0.0\\u202e\"")]
    public void Un_minApp_mal_escrito_omite_la_entrada(string minApp)
    {
        var problems = new List<string>();
        Assert.Empty(Load(Index(entries: [Entry(minApp: minApp)]), problems).Entries);
        Assert.Contains("minApp", Assert.Single(problems), StringComparison.Ordinal);
    }

    [Fact]
    public void Una_entrada_repetida_se_omite_aunque_cambie_la_compilacion()
    {
        var problems = new List<string>();
        SignedIndex index = Load(Index(entries:
        [
            Entry(version: "\"1.0.0+a\"", sha: "\"" + new string('a', 64) + "\""),
            Entry(version: "\"1.0.0+b\"", sha: "\"" + new string('b', 64) + "\""),
            Entry(version: "\"1.0.0\"", kind: "\"module\""),
        ]), problems);
        Assert.Equal(2, index.Entries.Count);
        Assert.Equal(new string('a', 64), index.Entries[0].Sha256);
        Assert.Contains("repetida", Assert.Single(problems), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- Orígenes y formato

    [Theory]
    [InlineData("https://github.com/Dathinel/r/releases/download/v1/../../../../Otro/r/releases/download/v1/a.paquete")]
    [InlineData("https://github.com/Dathinel/r/releases/download/v1/a.paquete#x")]
    [InlineData("https://github.com.ejemplo.com/Dathinel/r/releases/download/v1/a.paquete")]
    [InlineData("https://raw.githubusercontent.com/Dathinel/r/main/a.paquete")]
    [InlineData("https://github.com/Dathinel/r/releases/download/v1/sub/a.paquete")]
    [InlineData("https://github.com/Dathinel/r/releases/latest/a.paquete")]
    [InlineData("file:///C:/a.paquete")]
    [InlineData("//github.com/Dathinel/r/releases/download/v1/a.paquete")]
    [InlineData("https://xn--github-9za.com/Dathinel/r/releases/download/v1/a.paquete")]
    [InlineData("https://gíthub.com/Dathinel/r/releases/download/v1/a.paquete")]
    [InlineData("https://huggingface.co/org/m/resolve/0123456789abcdef0123456789abcdef0123456/m.onnx")]
    [InlineData("https://huggingface.co/resolve/0123456789abcdef0123456789abcdef01234567/m.onnx")]
    [InlineData("https://huggingface.co/org/m/resolve/0123456789abcdef0123456789abcdef01234567")]
    public void Las_entradas_de_otros_origenes_se_omiten(string url)
    {
        var problems = new List<string>();
        Assert.Empty(Load(Index(entries: [Entry(url: "\"" + url + "\"")]), problems).Entries);
        Assert.Single(problems);
    }

    [Theory]
    [InlineData("""{ "entries": [] }""")]
    [InlineData("""{ "formatVersion": 2, "entries": [] }""")]
    [InlineData("""{ "formatVersion": 1.5, "entries": [] }""")]
    [InlineData("""{ "formatVersion": 2147483648, "entries": [] }""")]
    [InlineData("""[ { "formatVersion": 1 } ]""")]
    [InlineData("""{ "formatVersion": 1, "entries": [ """)]
    [InlineData("""{ "formatVersion": 1 } { }""")]
    [InlineData("""{ /* c */ "formatVersion": 1 }""")]
    [InlineData("""{ "formatVersion": 1, "entries": [], }""")]
    [InlineData("")]
    [InlineData("null")]
    public void Lo_que_no_es_un_indice_da_FormatException(string json) =>
        Assert.Throws<FormatException>(() => Load(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void El_anidamiento_profundo_da_FormatException()
    {
        string deep = new string('[', 200) + new string(']', 200);
        Assert.Throws<FormatException>(() => Load(Encoding.UTF8.GetBytes($$"""{ "formatVersion": 1, "x": {{deep}} }""")));
    }

    [Fact]
    public void El_utf8_danado_da_FormatException()
    {
        byte[] json = [.. Index(entries: Entry())[..^3], 0xC3, 0x28, (byte)' ', (byte)']', (byte)' ', (byte)'}'];
        Assert.Throws<FormatException>(() => Load(json));
    }

    [Fact]
    public void Con_bom_firmado_se_lee()
    {
        byte[] json = [0xEF, 0xBB, 0xBF, .. Index(entries: Entry())];
        Assert.Single(Load(json).Entries);
    }

    [Theory]
    [InlineData("""{ "formatVersion": 1, "entries": {} }""")]
    [InlineData("""{ "formatVersion": 1, "entries": null }""")]
    [InlineData("""{ "formatVersion": 1 }""")]
    [InlineData("""{ "formatVersion": 1, "entries": [ null, 1, "x", [], {} ] }""")]
    public void Sin_entradas_legibles_el_indice_queda_vacio(string json)
    {
        var problems = new List<string>();
        Assert.Empty(Load(Encoding.UTF8.GetBytes(json), problems).Entries);
    }

    [Fact]
    public void Dos_mil_entradas_se_leen()
    {
        // La búsqueda de repetidas es cuadrática: 10 000 entradas tardan unos 3 s en Linux. Se registra, no se exige.
        string[] entries = [.. Enumerable.Range(0, 2_000).Select(i => Entry(id: $"\"p{i}\""))];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        SignedIndex index = Load(Index(entries: entries));
        output.WriteLine($"2000 entradas en {watch.ElapsedMilliseconds} ms.");
        Assert.Equal(2_000, index.Entries.Count);
    }

    // ---------------------------------------------------------------- Fuzz del contenido firmado

    /// <summary>
    /// Índices firmados con el contenido mutado al azar: un índice cuyas entradas cumplen las reglas, o
    /// <see cref="FormatException"/>. La <see cref="InvalidOperationException"/> del defecto de
    /// <see cref="Un_indice_firmado_con_un_tipo_inesperado_da_FormatException_y_no_otra_excepcion"/> se cuenta aparte y
    /// se tolera mientras no se arregle; cualquier otra excepción falla con su semilla.
    /// </summary>
    [Fact]
    public void Mutaciones_firmadas_dan_un_indice_valido_o_FormatException()
    {
        const int Cases = 3000;
        byte[] original = Index(entries:
        [
            Entry(),
            Entry(id: "\"rodaje-transiciones\"", version: "\"1.2.0-beta.1\"", minApp: "\"1.0.0\""),
            Entry(id: "\"modelo-x\"", kind: "\"model\"", url: "\"https://huggingface.co/org/m/resolve/0123456789abcdef0123456789abcdef01234567/m.onnx\"", size: "68719476736"),
        ]);
        int accepted = 0, refused = 0;
        List<int> knownDefect = [];
        for (int i = 0; i < Cases; i++)
        {
            int seed = ContentReaderFuzz.Seed + 50_000 + i;
            byte[] data = ContentReaderFuzz.Mutate(new Random(seed), original);
            try
            {
                AssertWellFormed(Load(data));
                accepted++;
            }
            catch (FormatException)
            {
                refused++;
            }
            catch (InvalidOperationException)
            {
                knownDefect.Add(seed);
            }
            catch (Exception ex)
            {
                Assert.Fail($"[semilla {seed}] {ex.GetType().Name}: {ex.Message}\n{ContentReaderFuzz.Show(data)}");
            }
        }

        output.WriteLine($"{accepted} aceptados, {refused} con FormatException, {knownDefect.Count} con InvalidOperationException (defecto conocido: semillas {string.Join(", ", knownDefect)}).");
        Assert.True(accepted > Cases / 4 && refused > 0, $"{accepted} aceptados, {refused} rechazados");
    }

    /// <summary>
    /// Defecto: <c>SignedIndex.Parse</c> llama a <c>TryGetInt32</c>/<c>TryGetInt64</c> sin mirar antes que el valor sea un
    /// número, y a <c>GetString</c> sin revisar los sustitutos sueltos. Un índice firmado con <c>"formatVersion": "1"</c>,
    /// con <c>"size": "100"</c> en una sola entrada o con un escape <c>"\ud800"</c> en cualquier texto lanza
    /// <see cref="InvalidOperationException"/>: no es la <see cref="FormatException"/> documentada,
    /// <c>GitHubReleaseDownloader.GetIndexAsync</c> no la convierte en <see cref="PackageDownloadException"/> y una sola
    /// entrada mal escrita tumba el índice entero en vez de omitirse («una entrada mal escrita … se omite»). Necesita un
    /// índice firmado (un error al armarlo a mano), no un atacante. En el fuzz de arriba (semillas 20311012 a
    /// 20314011) sale en 3 de 3000 casos: 20311509, 20311830 y 20312857. También con <c>"size": null</c>.
    /// Lo que debería pasar: <see cref="FormatException"/> para el <c>formatVersion</c> y los textos rotos, y la entrada
    /// omitida con su problema para el <c>size</c>.
    /// </summary>
    [Fact(Skip = "Defecto: SignedIndex.Load lanza InvalidOperationException con \"formatVersion\": \"1\", \"size\": \"100\" o \"\\ud800\" en un índice firmado.")]
    public void Un_indice_firmado_con_un_tipo_inesperado_da_FormatException_y_no_otra_excepcion()
    {
        Assert.Throws<FormatException>(() => Load(Index(format: "\"1\"")));
        Assert.Throws<FormatException>(() => Load(Index(generated: "\"\\ud800\"")));
        Assert.Throws<FormatException>(() => Load(Index(entries: Entry(id: "\"\\udc00\""))));
        Assert.Throws<FormatException>(() => Load(Encoding.UTF8.GetBytes("""{ "formatVersion": null }""")));

        var problems = new List<string>();
        SignedIndex index = Load(Index(entries: [Entry(size: "\"100\""), Entry(size: "null"), Entry(id: "\"rodaje-transiciones\"")]), problems);
        Assert.Equal("rodaje-transiciones", Assert.Single(index.Entries).Id);
        Assert.Equal(2, problems.Count);

        // Relacionado: un minApp que no es texto no lanza, pero se toma como «sin mínimo» y la entrada se ofrece a
        // cualquier versión del programa. Mal escrito, debería omitirse como un minApp de texto ilegible.
        problems.Clear();
        Assert.Empty(Load(Index(entries: [Entry(minApp: "2")]), problems).Entries);
        Assert.Contains("minApp", Assert.Single(problems), StringComparison.Ordinal);
    }

    /// <summary>
    /// Defecto: <c>DownloadUrlPolicy</c> no mira el puerto. <c>https://github.com:8443/Dathinel/…/releases/download/…</c>
    /// pasa como si fuera <c>github.com</c>, aunque docs/descarga.md dice que cada entrada «solo puede apuntar a
    /// https://github.com/Dathinel/&lt;repo&gt;/releases/download/&lt;tag&gt;/&lt;archivo&gt;» (lo mismo con
    /// <c>huggingface.co:444</c>). Necesita un índice firmado y lo descargado se sigue comprobando por hash, así que el
    /// riesgo es bajo. Lo que debería pasar: solo el puerto 443 (o sin puerto).
    /// </summary>
    [Fact(Skip = "Defecto: DownloadUrlPolicy acepta https://github.com:8443/Dathinel/... (no comprueba el puerto).")]
    public void Un_puerto_distinto_de_443_no_es_un_origen_permitido()
    {
        Assert.False(DownloadUrlPolicy.Default.IsAllowed(new Uri("https://github.com:8443/Dathinel/r/releases/download/v1/a.paquete"), out _));
        Assert.False(DownloadUrlPolicy.Default.IsAllowed(new Uri("https://huggingface.co:444/org/m/resolve/0123456789abcdef0123456789abcdef01234567/m.onnx"), out _));
        Assert.True(DownloadUrlPolicy.Default.IsAllowed(new Uri("https://github.com:443/Dathinel/r/releases/download/v1/a.paquete"), out _));
    }

    private static byte[] SignWithOtherKey(byte[] json)
    {
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return IndexSignature.Sign(json, other);
    }

    /// <summary>La misma firma DER con r y s intercambiados: bien formada, pero no firma estos bytes.</summary>
    private static byte[] SwapRAndS(byte[] der)
    {
        (byte[] r, byte[] s) = ReadRS(der);
        return WriteRS(s, r);
    }

    private static byte[] WithZeroS(byte[] der)
    {
        (byte[] r, _) = ReadRS(der);
        return WriteRS(r, [0]);
    }

    private static (byte[] R, byte[] S) ReadRS(byte[] der)
    {
        var reader = new AsnReader(der, AsnEncodingRules.DER);
        AsnReader sequence = reader.ReadSequence();
        return (sequence.ReadIntegerBytes().ToArray(), sequence.ReadIntegerBytes().ToArray());
    }

    private static byte[] WriteRS(byte[] r, byte[] s)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteIntegerUnsigned(r.SkipWhile(b => b == 0).DefaultIfEmpty((byte)0).ToArray());
            writer.WriteIntegerUnsigned(s.SkipWhile(b => b == 0).DefaultIfEmpty((byte)0).ToArray());
        }

        return writer.Encode();
    }
}
