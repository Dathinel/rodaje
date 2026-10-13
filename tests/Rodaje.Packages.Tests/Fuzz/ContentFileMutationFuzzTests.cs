using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Rodaje.Packages.Content;
using Rodaje.Packages.Download;
using Rodaje.Tests.Fuzz;

namespace Rodaje.Packages.Tests.Fuzz;

/// <summary>
/// Fuzz de mutaciones sobre los archivos de los complementos que lee la app: <c>paquete.json</c> (en memoria y desde
/// disco), el catálogo, los elementos propios sueltos, <c>rodaje.exportpreset</c>, <c>rodaje.textstyle</c>,
/// <c>rodaje.safezones</c>, <c>.cube</c> y WAV. Bytes cambiados o truncados, tipos cambiados, números enormes, cadenas de
/// 10 MB y nombres raros. Cada lector falla solo con su error claro (o informa el problema); nunca otra excepción ni un
/// cuelgue. Cada caso tiene su semilla (ver <see cref="FuzzRunner"/>).
/// </summary>
public class ContentFileMutationFuzzTests
{
    private const int Seed = 94_200_000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    private const string ExportPreset = """
        { "format": "rodaje.exportpreset", "version": 1, "id": "hd-1080i25", "name": "HD 1080i25 EBU R128",
          "description": "ProRes 422 HQ entrelazado", "category": "broadcast", "container": "mov", "fastStart": false,
          "video": { "codec": "prores", "profile": "hq", "bitDepth": 10, "chroma": "422", "width": 1920, "height": 1080,
                     "frameRate": "30000/1001", "fieldOrder": "tff", "rateControl": "profile" },
          "audio": { "codec": "pcm", "sampleFormat": "s24", "rateControl": "lossless", "loudness": { "lufs": -23, "truePeak": -1 } } }
        """;

    private const string TextStyle = """
        { "format": "rodaje.textstyle", "version": 2, "name": "Neón",
          "font": { "family": "Inter", "weight": 800 }, "size": 120,
          "fill": { "color": "#FFFFFF", "gradient": { "kind": "linear", "angle": 90, "stops": [ { "position": 0, "color": "#ffd400" }, { "position": 1, "color": "#FF3D00" } ] } },
          "stroke": { "color": "#000000", "width": 4 },
          "extraStrokes": [ { "color": "#FFFFFF", "width": 12, "join": "miter" } ],
          "shadow": { "color": "#00000099", "dy": 6, "blur": 10 },
          "typography": { "kerning": false, "case": "smallCaps", "features": ["liga", "-calt", "ss01"], "fit": "shrink" },
          "path": { "kind": "arc", "bend": -120 },
          "highlight": { "color": "#FFD400", "mode": "sweep" },
          "in": { "durationSeconds": 1, "unit": "letter", "stagger": 0.6, "order": "random",
                  "curves": { "blur": [ { "t": 0, "value": 24, "interp": "easeOut" }, { "t": 1, "value": 0 } ] } } }
        """;

    private const string SafeZones = """
        { "format": "rodaje.safezones", "version": 1, "id": "reels", "name": "Reels", "aspect": { "width": 9, "height": 16 },
          "zones": [ { "label": "Botones", "rect": [0.86, 0.5, 0.14, 0.38] }, { "label": "Títulos", "kind": "safe", "rect": [0.05, 0.05, 0.9, 0.9] } ] }
        """;

    private const string Cube = "TITLE \"t\"\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\nLUT_3D_SIZE 2\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1\n";

    private const string Catalog = """
        { "formatVersion": 1, "apps": [{ "id": "rodaje", "name": "Rodaje" }],
          "entries": [ { "id": "modulo-ritmo", "kind": "module", "name": "Ritmo", "description": "d", "repository": "https://github.com/Dathinel/modulo-ritmo", "apps": ["rodaje"], "provides": ["ritmo"] } ] }
        """;

    private const string OwnItem = """{ "kind": "textstyle", "id": "propios.neon", "name": "Neón", "description": "mío" }""";

    private static string Manifest => Samples.Manifest(items: """
        [ { "id": "deslizar", "kind": "transition", "name": "Deslizar", "file": "transiciones/deslizar.json" },
          { "id": "inter", "kind": "font", "name": "Inter", "license": "OFL-1.1", "files": ["fuentes/Inter-Regular.ttf"] } ]
        """);

    private static readonly (string Name, Func<string> Text, Action<Stream> Read)[] Readers =
    [
        ("exportpreset", () => ExportPreset, s => ContentReaders.ReadExportPreset(s)),
        ("textstyle", () => TextStyle, s => ContentReaders.ReadTextStyle(s)),
        ("safezones", () => SafeZones, s => ContentReaders.ReadSafeZones(s)),
        ("cube", () => Cube, s => ContentReaders.ReadCube(s)),
    ];

    [Fact]
    public void Seeds_AreValid()
    {
        foreach ((string _, Func<string> text, Action<Stream> read) in Readers)
        {
            read(ContentReaderFuzz.Utf8(text()));
        }

        Assert.True(ManifestReader.Read(Manifest).IsValid, string.Join(" ", ManifestReader.Read(Manifest).Errors));
        Assert.Single(PackageCatalog.Parse(Catalog).Entries);
        _ = ContentReaders.ReadSound(new MemoryStream(Wave()));
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void ContentFiles_OnlyThrowPackageContentException()
    {
        FuzzRunner.Run("contenido", Seed, cases: 6000, Budget, minimumCases: 1500, (seed, rng) =>
        {
            (string name, Func<string> text, Action<Stream> read) = Readers[rng.Next(Readers.Length)];
            byte[] data = Mutate(Encoding.UTF8.GetBytes(text()), rng);
            ExpectContent(name, () => read(new MemoryStream(data)), data);
        });
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void Waves_OnlyThrowPackageContentException()
    {
        byte[] wave = Wave();
        FuzzRunner.Run("wav", Seed + 1_000_000, cases: 4000, Budget, minimumCases: 1000, (seed, rng) =>
        {
            byte[] data = MutateBinary(wave, rng);
            ExpectContent("wav", () => ContentReaders.ReadSound(new MemoryStream(data)), data);
            ExpectContent("wav (cabecera)", () => ContentReaders.ReadSoundInfo(new MemoryStream(data)), data);
        });
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void Manifests_NeverThrow()
    {
        string dir = Directory.CreateTempSubdirectory("rodaje-fuzz94-paquete-").FullName;
        try
        {
            string manifest = Manifest;
            FuzzRunner.Run("paquete.json", Seed + 2_000_000, cases: 4000, Budget, minimumCases: 1000, (seed, rng) =>
            {
                byte[] data = Mutate(Encoding.UTF8.GetBytes(manifest), rng);
                ManifestResult result;
                if (rng.Next(8) == 0)
                {
                    string path = Path.Combine(dir, PackageManifest.FileName);
                    File.WriteAllBytes(path, data);
                    result = ManifestReader.ReadFile(path);
                }
                else
                {
                    result = ManifestReader.Read(Encoding.UTF8.GetString(data), rng.Next(2) == 0 ? dir : null);
                }

                Assert.True(result.IsValid == (result.Manifest is not null) && (result.IsValid || result.Errors.Count > 0),
                    "Un manifiesto inválido sin errores: " + ContentReaderFuzz.Show(data));
                foreach (string error in result.Errors)
                {
                    Assert.False(string.IsNullOrWhiteSpace(error));
                }
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void Catalogs_OnlyThrowFormatException()
    {
        FuzzRunner.Run("catálogo", Seed + 3_000_000, cases: 4000, Budget, minimumCases: 1000, (seed, rng) =>
        {
            byte[] data = Mutate(Encoding.UTF8.GetBytes(Catalog), rng);
            try
            {
                _ = PackageCatalog.ParseUtf8(data, []);
            }
            catch (FormatException ex)
            {
                Assert.False(string.IsNullOrWhiteSpace(ex.Message));
            }
        });
    }

    private static readonly string ModuleManifest = $$"""
        { "id": "m", "module": { "entry": "m.exe", "models": [
          { "id": "bueno", "file": "modelos/a.onnx", "size": 10, "sha256": "{{new string('a', 64)}}", "license": "MIT", "url": "https://huggingface.co/o/r/resolve/{{new string('b', 40)}}/a.onnx" },
          { "id": "otro", "file": "modelos/b.onnx", "size": 2048, "sha256": "{{new string('c', 64)}}", "url": "https://huggingface.co/o/r/resolve/{{new string('d', 40)}}/b.onnx" } ] } }
        """;

    /// <summary><c>module.models</c> de un módulo instalado y dañado: <see cref="ModuleModels.Read"/> dice que nunca lanza.</summary>
    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void ModuleModels_NeverThrow()
    {
        string dir = Directory.CreateTempSubdirectory("rodaje-fuzz94-modelos-").FullName;
        try
        {
            string path = Path.Combine(dir, PackageManifest.FileName);
            File.WriteAllText(path, ModuleManifest);
            Assert.Equal(2, ModuleModels.Read(path).Count);
            FuzzRunner.Run("module.models", Seed + 5_000_000, cases: 2000, Budget, minimumCases: 500, (seed, rng) =>
            {
                File.WriteAllBytes(path, Mutate(Encoding.UTF8.GetBytes(ModuleManifest), rng));
                var problems = new List<string>();
                IReadOnlyList<ModuleModel> models = ModuleModels.Read(path, problems);
                Assert.True(models.Count <= 2);
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Un <c>size</c> que no es número (o un texto con un sustituto suelto escapado) hacía lanzar
    /// <see cref="InvalidOperationException"/> a <see cref="ModuleModels.Read"/>, que promete no lanzar; y un
    /// <c>formatVersion</c> que no es número, a <see cref="PackageCatalog.Parse"/>, que solo debe lanzar
    /// <see cref="FormatException"/> (defectos hallados por este fuzz).
    /// </summary>
    [Theory]
    [InlineData("\"size\": 10", "\"size\": \"10\"")]
    [InlineData("\"size\": 10", "\"size\": {}")]
    [InlineData("\"size\": 10", "\"size\": [10]")]
    [InlineData("\"license\": \"MIT\"", "\"license\": \"\\ud800\"")]
    [InlineData("\"entry\": \"m.exe\"", "\"\\udc00\": 1")]
    public void ModuleModels_WithWrongTypes_SkipInsteadOfThrowing(string from, string to)
    {
        string dir = Directory.CreateTempSubdirectory("rodaje-fuzz94-modelos-").FullName;
        try
        {
            string path = Path.Combine(dir, PackageManifest.FileName);
            string text = ModuleManifest.Replace(from, to, StringComparison.Ordinal);
            Assert.NotEqual(ModuleManifest, text);
            File.WriteAllText(path, text);
            var problems = new List<string>();
            Assert.True(ModuleModels.Read(path, problems).Count < 2);
            Assert.NotEmpty(problems);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("{ \"formatVersion\": {}, \"entries\": [] }")]
    [InlineData("{ \"formatVersion\": \"1\", \"entries\": [] }")]
    [InlineData("{ \"formatVersion\": [1], \"entries\": [] }")]
    [InlineData("{ \"formatVersion\": null, \"entries\": [] }")]
    [InlineData("{ \"formatVersion\": true, \"entries\": [] }")]
    public void Catalog_WithNonNumericVersion_ThrowsFormatException(string json) =>
        Assert.Throws<FormatException>(() => PackageCatalog.Parse(json));

    /// <summary>Elementos propios sueltos dañados y con nombres de archivo raros: la búsqueda los salta, nunca lanza.</summary>
    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void OwnItemsScan_NeverThrows()
    {
        string root = Directory.CreateTempSubdirectory("rodaje-fuzz94-propios-").FullName;
        try
        {
            PackageLocations locations = PackageLocations.Under(root);
            Directory.CreateDirectory(locations.Own);
            Directory.CreateDirectory(Path.Combine(locations.Packages, "roto"));
            FuzzRunner.Run("propios", Seed + 4_000_000, cases: 300, Budget, minimumCases: 100, (seed, rng) =>
            {
                foreach (string old in Directory.EnumerateFiles(locations.Own))
                {
                    File.Delete(old);
                }

                for (int k = 0; k < 3; k++)
                {
                    string name = OddFileNames[rng.Next(OddFileNames.Length)] + ".json";
                    File.WriteAllBytes(Path.Combine(locations.Own, name), Mutate(Encoding.UTF8.GetBytes(OwnItem), rng));
                }

                File.WriteAllBytes(Path.Combine(locations.Packages, "roto", PackageManifest.FileName), Mutate(Encoding.UTF8.GetBytes(Manifest), rng));
                PackageScan scan = PackageLibrary.Scan(locations, "rodaje");
                Assert.All(scan.Problems, p => Assert.False(string.IsNullOrWhiteSpace(p.Message)));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static readonly string[] OddFileNames =
    [
        "a", "Ñandú Ü", "😀🎬", "日本語", "con", "nul", "COM1", "-", "--", "_", "a b c", " lead", "x.y.z", "\u202Egnp", "a\u200Bb",
        "\u00A0", "%00", "~", new string('x', 120), "ÀÉÍÓÚ", "ǅ", "\uFB01", "Ⅻ",
    ];

    /// <summary>
    /// Un nombre de archivo de Windows puede traer un sustituto UTF-16 suelto (NTFS no lo impide) y
    /// <see cref="string.Normalize()"/> lanzaba <see cref="ArgumentException"/> al convertirlo en identificador, lo que
    /// rompía la búsqueda de elementos propios (defecto hallado por este fuzz).
    /// </summary>
    [Theory]
    [InlineData("a\uD800b", "a-b")]
    [InlineData("\uDC00", "elemento")]
    [InlineData("Ñandú\uD83D", "nandu")]
    [InlineData("é\uDE00x\uD83D\uDE00y", "e-x-y")]
    public void ToIdentifier_ToleratesLoneSurrogates(string text, string expected) =>
        Assert.Equal(expected, PackageLibrary.ToIdentifier(text));

    /// <summary>Cadenas de 10 MB y números enormes en los campos de cada lector.</summary>
    [Fact]
    public void HugeValues_GiveClearErrors()
    {
        string huge = new('ñ', 10 * 1024 * 1024);
        foreach ((string name, Func<string> text, Action<Stream> read) in Readers)
        {
            string seed = text();
            foreach (string replacement in new[] { $"\"{huge}\"", "1e400", "-1e400", "99999999999999999999999999", "[" + string.Join(',', Enumerable.Repeat("0", 1_000_000)) + "]" })
            {
                for (int occurrence = 0; occurrence < 6; occurrence++)
                {
                    string mutated = ReplaceValue(seed, occurrence, replacement);
                    ExpectContent($"{name} valor {occurrence}", () => read(ContentReaderFuzz.Utf8(mutated)), Encoding.UTF8.GetBytes(FuzzRunner.Truncate(mutated)));
                }
            }
        }

        ManifestResult big = ManifestReader.Read(Manifest.Replace("Transiciones de prueba", huge, StringComparison.Ordinal));
        Assert.True(big.Manifest is null || big.Manifest.Name.Length <= SafeText.MaxName);
        ExpectContent("cube 10 MB", () => ContentReaders.ReadCube(ContentReaderFuzz.Utf8("TITLE \"" + huge + "\"\n" + Cube)), []);
        ExpectContent("cube línea 10 MB", () => ContentReaders.ReadCube(ContentReaderFuzz.Utf8("LUT_3D_SIZE 2\n" + huge)), []);
        ExpectContent("cube tamaño enorme", () => ContentReaders.ReadCube(ContentReaderFuzz.Utf8("LUT_3D_SIZE 99999999999999999999\n0 0 0\n")), []);
        try
        {
            _ = PackageCatalog.Parse(Catalog.Replace("Ritmo", huge, StringComparison.Ordinal));
        }
        catch (FormatException)
        {
        }
    }

    // --- Comprobación ----------------------------------------------------------------------------------------------------

    private static void ExpectContent(string what, Action read, byte[] data)
    {
        try
        {
            read();
        }
        catch (PackageContentException ex)
        {
            Assert.False(string.IsNullOrWhiteSpace(ex.Message), what);
            Assert.True(ex.InnerException is null or JsonException,
                $"{what}: causa inesperada {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}\n{ContentReaderFuzz.Show(data)}");
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException($"{what}: excepción no controlada {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n{ContentReaderFuzz.Show(data)}");
        }
    }

    // --- Mutaciones ------------------------------------------------------------------------------------------------------

    /// <summary>Las de <see cref="ContentReaderFuzz.Mutate"/> y, a veces, una cadena larga o un valor de otro tipo en un campo.</summary>
    private static byte[] Mutate(byte[] original, Random rng)
    {
        if (rng.Next(6) == 0)
        {
            string text = Encoding.UTF8.GetString(original);
            string[] replacements = ["null", "true", "[]", "{}", "\"\"", "0", "-1", "1e400", "1.5", "\"x\"", "[null]", "{\"a\":1}",
                "\"" + new string('a', 1024 << rng.Next(8)) + "\"", "\"\\ud800\"", "99999999999999999999"];
            return Encoding.UTF8.GetBytes(ReplaceValue(text, rng.Next(40), replacements[rng.Next(replacements.Length)]));
        }

        return ContentReaderFuzz.Mutate(rng, original);
    }

    /// <summary>Cambia el valor de la propiedad número <paramref name="occurrence"/> (contando «": »), si la hay.</summary>
    private static string ReplaceValue(string json, int occurrence, string replacement)
    {
        int at = -1;
        for (int k = 0; k <= occurrence; k++)
        {
            at = json.IndexOf("\": ", at + 1, StringComparison.Ordinal);
            if (at < 0)
            {
                return json;
            }
        }

        int start = at + 3;
        int end = start;
        int depth = 0;
        bool inString = false;
        for (; end < json.Length; end++)
        {
            char c = json[end];
            if (inString)
            {
                if (c == '\\')
                {
                    end++;
                }
                else if (c == '"')
                {
                    inString = false;
                    if (depth == 0)
                    {
                        end++;
                        break;
                    }
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c is '{' or '[')
            {
                depth++;
            }
            else if (c is '}' or ']')
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
                if (depth == 0)
                {
                    end++;
                    break;
                }
            }
            else if (c == ',' && depth == 0)
            {
                break;
            }
        }

        return json[..start] + replacement + json[end..];
    }

    private static byte[] MutateBinary(byte[] original, Random rng)
    {
        byte[] data = [.. original];
        int edits = 1 + rng.Next(4);
        for (int k = 0; k < edits && data.Length > 0; k++)
        {
            int at = rng.Next(Math.Min(data.Length, 64));
            switch (rng.Next(5))
            {
                case 0:
                    data[at] = (byte)rng.Next(256);
                    break;
                case 1 when at + 4 <= data.Length:
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), (uint)rng.Next(5) switch { 0 => uint.MaxValue, 1 => 0u, 2 => 0x7FFFFFFFu, 3 => 1u, _ => (uint)rng.Next() });
                    break;
                case 2:
                    data = data[..rng.Next(data.Length + 1)];
                    break;
                case 3 when at + 2 <= data.Length:
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), (ushort)rng.Next(5) switch { 0 => ushort.MaxValue, 1 => 0, 2 => 0xFFFE, 3 => 3, _ => (ushort)rng.Next(65536) });
                    break;
                default:
                    data = [.. data, .. Enumerable.Range(0, rng.Next(64)).Select(_ => (byte)rng.Next(256))];
                    break;
            }
        }

        return data;
    }

    /// <summary>Un WAV PCM de 16 bits, estéreo, 48 kHz, con 64 cuadros y un bloque LIST que hay que saltar.</summary>
    private static byte[] Wave()
    {
        const int frames = 64;
        byte[] list = [.. "LIST"u8, 4, 0, 0, 0, .. "INFO"u8];
        int dataBytes = frames * 4;
        var bytes = new List<byte>();
        bytes.AddRange("RIFF"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(4 + 24 + list.Length + 8 + dataBytes));
        bytes.AddRange("WAVE"u8.ToArray());
        bytes.AddRange("fmt "u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(16));
        bytes.AddRange(BitConverter.GetBytes((short)1));
        bytes.AddRange(BitConverter.GetBytes((short)2));
        bytes.AddRange(BitConverter.GetBytes(48_000));
        bytes.AddRange(BitConverter.GetBytes(48_000 * 4));
        bytes.AddRange(BitConverter.GetBytes((short)4));
        bytes.AddRange(BitConverter.GetBytes((short)16));
        bytes.AddRange(list);
        bytes.AddRange("data"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(dataBytes));
        for (int i = 0; i < frames * 2; i++)
        {
            bytes.AddRange(BitConverter.GetBytes((short)(i * 97)));
        }

        return [.. bytes];
    }
}
