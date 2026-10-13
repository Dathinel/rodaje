using System.Globalization;
using System.Text;
using System.Text.Json;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>
/// Ida y vuelta del <c>.rodaje</c>: guardar → abrir → guardar da el mismo archivo byte a byte y el mismo modelo, en
/// cualquier cultura y con los valores en los extremos; lo que no se puede guardar sin perderlo se rechaza al guardar.
/// </summary>
public sealed class ProjectRoundTripPropertiesTests : IDisposable
{
    /// <summary>Culturas con coma decimal, «İ/ı» y dígitos y calendario árabes; la invariante como referencia.</summary>
    public static TheoryData<string> Cultures => ["", "es-ES", "de-DE", "tr-TR", "ar-SA"];

    private const int Seeds = 40;

    private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-ida-vuelta-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [MemberData(nameof(Cultures))]
    public void RandomProjects_SaveOpenSave_SameBytesAndSameModel(string culture)
    {
        WithCulture(culture, () =>
        {
            for (int seed = 1; seed <= Seeds; seed++)
            {
                Project original = RoundTripProjects.Generate(seed);
                string context = $"semilla {seed}, cultura «{culture}»";

                string first = Path.Combine(_folder, $"a{seed}.rodaje");
                string second = Path.Combine(_folder, $"b{seed}.rodaje");
                ProjectSerializer.Save(original, first);
                byte[] firstBytes = File.ReadAllBytes(first);
                SchemaCheck.AssertValid(Encoding.UTF8.GetString(firstBytes), context);

                Project reopened = ProjectSerializer.Load(first);
                ProjectSerializer.Save(reopened, second);

                Assert.True(firstBytes.AsSpan().SequenceEqual(File.ReadAllBytes(second)), $"El segundo guardado no da el mismo archivo ({context}).");
                AssertSameModel(original, reopened, context);
            }
        });
    }

    /// <summary>El texto no depende de la cultura en que se guarda: la invariante y las demás escriben lo mismo.</summary>
    [Fact]
    public void Serialize_IsTheSameInEveryCulture()
    {
        for (int seed = 1; seed <= Seeds; seed++)
        {
            Project project = RoundTripProjects.Generate(seed);
            string reference = "";
            WithCulture("", () => reference = ProjectSerializer.Serialize(project));
            foreach (string culture in new[] { "es-ES", "de-DE", "tr-TR", "ar-SA" })
            {
                WithCulture(culture, () => Assert.Equal(reference, ProjectSerializer.Serialize(project)));
            }
        }
    }

    /// <summary>
    /// Regresión con semilla (1 a 40): en un proyecto al azar, un NaN, un infinito o un sustituto suelto en un sitio al
    /// azar se rechaza al guardar con su ruta exacta, el archivo anterior queda como estaba y no queda temporal.
    /// </summary>
    [Fact]
    public void RandomProjects_WithAnUnsavableValue_AreRejectedWithItsPath()
    {
        for (int seed = 1; seed <= Seeds; seed++)
        {
            Project project = RoundTripProjects.Generate(seed);
            string expected = RoundTripProjects.Break(project, new Random(seed));
            string file = Path.Combine(_folder, $"roto{seed}.rodaje");
            File.WriteAllText(file, "anterior");

            ProjectFormatException ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Save(project, file));

            Assert.True(ex.Message.Contains($"«{expected}»", StringComparison.Ordinal), $"Semilla {seed}: se esperaba «{expected}» en «{ex.Message}».");
            Assert.Equal("anterior", File.ReadAllText(file));
            Assert.False(File.Exists(file + ProjectSerializer.TempSuffix));
        }
    }

    /// <summary>
    /// Regresión: un NaN o un infinito en cualquier número del modelo hacía fallar el guardado con una
    /// <see cref="ArgumentException"/> en inglés y sin ruta, que el guardado del editor no atrapaba (solo atrapa
    /// <see cref="IOException"/>, <see cref="UnauthorizedAccessException"/> y <see cref="ProjectFormatException"/>).
    /// Ahora es un <see cref="ProjectFormatException"/> en español que dice dónde está, y no toca el archivo anterior.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteNumbers_AreRejectedOnSave_WithTheirPath(double bad)
    {
        var cases = new (string Path, Action<Project> Break)[]
        {
            ("$.sequences[0].masterGainDb", p => p.Sequences[0].MasterGainDb = bad),
            ("$.sequences[0].videoTracks[0].clips[0].effects[0].parameters.opacity.value",
                p => p.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters["opacity"].Value = bad),
            ("$.sequences[0].videoTracks[0].clips[0].effects[0].parameters.positionX.keyframes[2].value",
                p => SetKey(p, 2, k => k with { Value = bad })),
            ("$.sequences[0].videoTracks[0].clips[0].effects[0].parameters.positionX.keyframes[1].spatialOut",
                p => SetKey(p, 1, k => k with { SpatialOut = bad })),
            ("$.sequences[0].videoTracks[0].clips[0].effects[0].parameters.positionX.keyframes[3].ease.y2",
                p => SetKey(p, 3, k => k with { Ease = new BezierEase(0.1, 0.2, 0.3, bad) })),
            ("$.sequences[0].audioTracks[1].effects[0].parameters.gainDb.keyframes[1].value",
                p => p.Sequences[0].AudioTracks[1].Effects[0].Parameters["gainDb"].Keyframes[1] = new Keyframe(Time.FromSeconds(4), bad)),
        };

        foreach ((string path, Action<Project> breakIt) in cases)
        {
            Project project = SerializationSamples.Rich();
            string file = Path.Combine(_folder, "proyecto.rodaje");
            File.WriteAllText(file, "anterior");
            breakIt(project);

            ProjectFormatException ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Save(project, file));

            Assert.Contains($"«{path}»", ex.Message, StringComparison.Ordinal);
            Assert.Contains("no es un número finito", ex.Message, StringComparison.Ordinal);
            Assert.Equal("anterior", File.ReadAllText(file));
            Assert.False(File.Exists(file + ProjectSerializer.TempSuffix));
        }
    }

    /// <summary>
    /// Regresión: un texto con medio carácter (un sustituto UTF-16 suelto, por ejemplo de un nombre de
    /// archivo de NTFS o de un texto cortado a mitad de un emoji) se guardaba cambiado en silencio por U+FFFD.
    /// </summary>
    [Fact]
    public void LoneSurrogates_AreRejectedOnSave_InsteadOfSilentlyReplaced()
    {
        string high = "a" + (char)0xD83C + "b";
        string low = "a" + (char)0xDFAC;
        var cases = new (string Path, Action<Project> Break)[]
        {
            ("$.name", p => p.Name = high),
            ("$.media[0].path", p => p.Media[0].Path = "material/" + low + ".mp4"),
            ("$.sequences[0].markers[0].note", p => p.Sequences[0].Markers.Add(new Marker { Id = "m", Note = low })),
            ("$.sequences[0].videoTracks[0].clips[0].effects[1].options.nota", p => p.Sequences[0].VideoTracks[0].Clips[0].Effects[1].Options["nota"] = high),
            ("$.sequences[0].videoTracks[0].clips[0].effects[1].options (el nombre de una clave)",
                p => p.Sequences[0].VideoTracks[0].Clips[0].Effects[1].Options[low] = "x"),
        };

        foreach ((string path, Action<Project> breakIt) in cases)
        {
            Project project = SerializationSamples.Rich();
            breakIt(project);

            ProjectFormatException ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Serialize(project));

            Assert.Contains($"«{path}»", ex.Message, StringComparison.Ordinal);
            Assert.Contains("sustituto UTF-16 suelto", ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Regresión: dos claves que solo se distinguían por un sustituto suelto («a» + U+D800 y «a» + U+DC00)
    /// se guardaban las dos como «a�»: el archivo tenía la clave repetida y ya no se podía abrir.
    /// </summary>
    [Fact]
    public void KeysThatDifferOnlyInLoneSurrogates_NeverProduceAnUnopenableFile()
    {
        Project project = SerializationSamples.Rich();
        Dictionary<string, string> options = project.Sequences[0].VideoTracks[0].Clips[0].Effects[1].Options;
        options["a" + (char)0xD800] = "1";
        options["a" + (char)0xDC00] = "2";
        string file = Path.Combine(_folder, "claves.rodaje");

        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Save(project, file));
        Assert.False(File.Exists(file));
    }

    /// <summary>Lo que sí se puede guardar llega intacto: ±0 con su signo, subnormales, los extremos y los tiempos enormes.</summary>
    [Fact]
    public void ExtremeButRepresentableValues_SurviveBitForBit()
    {
        double[] values = [-0.0, 0.0, double.Epsilon, -double.Epsilon, double.MaxValue, double.MinValue, 0.1 + 0.2, 2.2250738585072009E-308];
        foreach (double value in values)
        {
            Project project = SerializationSamples.Rich();
            Parameter opacity = project.Sequences[0].VideoTracks[0].Clips[0].Effects[1].Parameters["x"] = new Parameter
            {
                Value = value,
                Keyframes = [new Keyframe(Time.FromTicks(-1), value) { SpatialIn = value, SpatialOut = -value }],
            };
            project.Sequences[0].VideoTracks[0].Clips[0].Effects[1].Type = "complemento.desconocido";
            project.Sequences[0].Markers.Add(new Marker { Id = "fin", Time = Time.FromTicks(long.MaxValue), Name = "nulo\0" + (char)0xFFFF });

            Project reopened = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

            Parameter back = reopened.Sequences[0].VideoTracks[0].Clips[0].Effects[1].Parameters["x"];
            Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(back.Value));
            Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(back.Keyframes[0].SpatialIn!.Value));
            Assert.Equal(BitConverter.DoubleToInt64Bits(-value), BitConverter.DoubleToInt64Bits(back.Keyframes[0].SpatialOut!.Value));
            Assert.Equal(Time.FromTicks(-1), back.Keyframes[0].Time);
            Assert.Equal(long.MaxValue, reopened.Sequences[0].Markers[0].Time.Ticks);
            Assert.Equal("nulo\0" + (char)0xFFFF, reopened.Sequences[0].Markers[0].Name);
        }
    }

    /// <summary>
    /// Los tiempos negativos o enormes se escriben exactos (son enteros de ticks); el validador, no el formato, decide si
    /// el proyecto se puede abrir, y lo dice con un mensaje claro.
    /// </summary>
    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    public void NegativeTimes_AreWrittenExactly_AndRejectedClearlyOnOpen(long ticks)
    {
        Project project = SerializationSamples.Rich();
        project.Sequences[0].Markers.Add(new Marker { Id = "antes", Time = Time.FromTicks(ticks) });
        string json = ProjectSerializer.Serialize(project);

        Project raw = ProjectSerializer.Deserialize(json, _ => []);
        Assert.Equal(ticks, raw.Sequences[0].Markers[0].Time.Ticks);
        Assert.Equal(json, ProjectSerializer.Serialize(raw));

        ProjectFormatException ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(json));
        Assert.Contains("El marcador «antes» está antes del inicio", ex.Message, StringComparison.Ordinal);
    }

    private static void SetKey(Project project, int index, Func<Keyframe, Keyframe> change)
    {
        List<Keyframe> keys = project.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters["positionX"].Keyframes;
        keys[index] = change(keys[index]);
    }

    private static void WithCulture(string name, Action action)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    /// <summary>El mismo modelo, campo por campo y con los números comparados bit a bit (salvo ±0 donde el 0 no se escribe).</summary>
    private static void AssertSameModel(Project expected, Project actual, string context)
    {
        SerializationSamples.AssertDeepEqual(expected, actual);
        for (int s = 0; s < expected.Sequences.Count; s++)
        {
            Sequence e = expected.Sequences[s];
            Sequence a = actual.Sequences[s];
            Assert.True(e.MasterGainDb == a.MasterGainDb, $"Ganancia maestra distinta ({context}).");
            Assert.Equal(e.Markers.Select(m => (m.Id, m.Time, m.Name, m.Color, m.Note)), a.Markers.Select(m => (m.Id, m.Time, m.Name, m.Color, m.Note)));
            Assert.Equal(e.Regions.Select(r => (r.Id, r.Start, r.End, r.Name, r.Color, r.Note)), a.Regions.Select(r => (r.Id, r.Start, r.End, r.Name, r.Color, r.Note)));
            foreach ((Track et, Track at) in e.AllTracks.Zip(a.AllTracks))
            {
                Assert.Equal(et.Height, at.Height);
                AssertEffectsBitwise(et.Effects, at.Effects, context);
                foreach ((Clip ec, Clip ac) in et.Clips.Zip(at.Clips))
                {
                    Assert.Equal(ec.Color, ac.Color);
                    Assert.Equal(ec.Generator is null, ac.Generator is null);
                    if (ec.Generator is not null)
                    {
                        Assert.True(ec.Generator.ContentEquals(ac.Generator!), $"El título «{ec.Id}» cambió ({context}).");
                    }

                    AssertEffectsBitwise(ec.Effects, ac.Effects, context);
                }
            }
        }
    }

    private static void AssertEffectsBitwise(List<EffectInstance> expected, List<EffectInstance> actual, string context)
    {
        foreach ((EffectInstance ee, EffectInstance ae) in expected.Zip(actual))
        {
            Assert.Equal(ee.Parameters.Keys, ae.Parameters.Keys);
            foreach ((string name, Parameter ep) in ee.Parameters)
            {
                Parameter ap = ae.Parameters[name];
                Assert.True(Bits(ep.Value) == Bits(ap.Value), $"«{name}» de «{ee.Id}»: {ep.Value:R} → {ap.Value:R} ({context}).");
                Assert.Equal(ep.Keyframes.Count, ap.Keyframes.Count);
                foreach ((Keyframe ek, Keyframe ak) in ep.Keyframes.Zip(ap.Keyframes))
                {
                    Assert.Equal((ek.Time, ek.Interpolation, Bits(ek.Value), Bits(ek.SpatialIn), Bits(ek.SpatialOut)), (ak.Time, ak.Interpolation, Bits(ak.Value), Bits(ak.SpatialIn), Bits(ak.SpatialOut)));
                    Assert.Equal(
                        ek.Ease is { } x ? (Bits(x.X1), Bits(x.Y1), Bits(x.X2), Bits(x.Y2)) : default,
                        ak.Ease is { } y ? (Bits(y.X1), Bits(y.Y1), Bits(y.X2), Bits(y.Y2)) : default);
                }
            }
        }
    }

    private static long? Bits(double? value) => value is { } v ? BitConverter.DoubleToInt64Bits(v) : null;

    /// <summary>
    /// Validador mínimo del esquema JSON (draft 2020-12) con lo que usa <c>schema/</c>: type, properties, required,
    /// items, additionalProperties, enum, const, minimum, maximum, anyOf y $ref locales. Además exige que todo campo
    /// escrito esté declarado, para que el esquema no se quede atrás del formato.
    /// </summary>
    private static class SchemaCheck
    {
        private static readonly Lazy<JsonDocument> Schema = new(() =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "schema", $"rodaje-proyecto.v{Project.CurrentSchemaVersion}.schema.json"))));

        public static void AssertValid(string json, string context)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            var problems = new List<string>();
            Check(doc.RootElement, Schema.Value.RootElement, "$", problems);
            Assert.True(problems.Count == 0, $"El archivo no cumple el esquema ({context}):\n" + string.Join("\n", problems.Take(10)));
        }

        private static void Check(JsonElement value, JsonElement schema, string path, List<string> problems)
        {
            if (schema.TryGetProperty("$ref", out JsonElement reference))
            {
                schema = Resolve(reference.GetString()!);
            }

            if (schema.TryGetProperty("anyOf", out JsonElement anyOf))
            {
                if (!anyOf.EnumerateArray().Any(option =>
                {
                    var inner = new List<string>();
                    Check(value, option, path, inner);
                    return inner.Count == 0;
                }))
                {
                    problems.Add($"{path}: no cumple ninguna opción de anyOf.");
                }

                return;
            }

            if (schema.TryGetProperty("type", out JsonElement type) && !TypeMatches(value, type))
            {
                problems.Add($"{path}: se esperaba {type.GetRawText()} y hay {value.ValueKind}.");
                return;
            }

            if (schema.TryGetProperty("enum", out JsonElement options) && !options.EnumerateArray().Any(o => JsonElement.DeepEquals(o, value)))
            {
                problems.Add($"{path}: «{value.GetRawText()}» no está en enum.");
            }

            if (schema.TryGetProperty("const", out JsonElement constant) && !JsonElement.DeepEquals(constant, value))
            {
                problems.Add($"{path}: debe valer {constant.GetRawText()}.");
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                if (schema.TryGetProperty("minimum", out JsonElement min) && value.GetDouble() < min.GetDouble())
                {
                    problems.Add($"{path}: menor que el mínimo {min.GetRawText()}.");
                }

                if (schema.TryGetProperty("maximum", out JsonElement max) && value.GetDouble() > max.GetDouble())
                {
                    problems.Add($"{path}: mayor que el máximo {max.GetRawText()}.");
                }
            }

            if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement items))
            {
                int i = 0;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    Check(item, items, $"{path}[{i++}]", problems);
                }
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                schema.TryGetProperty("properties", out JsonElement properties);
                bool hasAdditional = schema.TryGetProperty("additionalProperties", out JsonElement additional);
                if (schema.TryGetProperty("required", out JsonElement required))
                {
                    foreach (JsonElement name in required.EnumerateArray())
                    {
                        if (!value.TryGetProperty(name.GetString()!, out _))
                        {
                            problems.Add($"{path}: falta «{name.GetString()}».");
                        }
                    }
                }

                foreach (JsonProperty property in value.EnumerateObject())
                {
                    string child = $"{path}.{property.Name}";
                    if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out JsonElement propertySchema))
                    {
                        Check(property.Value, propertySchema, child, problems);
                    }
                    else if (hasAdditional && additional.ValueKind == JsonValueKind.Object)
                    {
                        Check(property.Value, additional, child, problems);
                    }
                    else if (!hasAdditional || additional.ValueKind == JsonValueKind.False)
                    {
                        problems.Add($"{child}: campo que el esquema no declara.");
                    }
                }
            }
        }

        private static bool TypeMatches(JsonElement value, JsonElement type) => type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(t => TypeMatches(value, t))
            : type.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "null" => value.ValueKind == JsonValueKind.Null,
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                _ => false,
            };

        private static JsonElement Resolve(string pointer)
        {
            Assert.StartsWith("#/", pointer, StringComparison.Ordinal);
            JsonElement node = Schema.Value.RootElement;
            foreach (string part in pointer[2..].Split('/'))
            {
                node = node.GetProperty(part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
            }

            return node;
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "schema")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                {
                    return dir.FullName;
                }
            }

            throw new DirectoryNotFoundException("No se encontró la raíz del repositorio (con schema/ y src/).");
        }
    }
}
