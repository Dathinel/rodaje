using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Tests.Gpu;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests.Variants;

[Collection("Vulkan variants")]
public sealed class EffectComparisonTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    // Fuente en centros o fracciones exactas en ocho bits; LUT y curvas de tono arbitrarias figuran como excepciones.
    private static readonly HashSet<string> s_exact = new(StringComparer.Ordinal)
    {
        // solo-croma-centros va con (ii): en lavapipe el croma 4:2:0 se sigue leyendo en fracciones (3,4e-3; 4,9e-4 en la RTX 3050).
        "graded-ADVANCED-solo-ruedas-centros",
        "graded-ADVANCED-solo-calificador-centros", "graded-ADVANCED-solo-curvas-centros", "graded-ADVANCED-solo-croma-444",
        "muestreo-escala0.5", "downsample", "blur-h", "blur-v-medio-texel",
    };

    // El informe de fase 0 identifica filtrado amplificado, PQ y discontinuidades; también se acota su error.
    private static readonly Dictionary<string, string> s_warpExceptions = new(StringComparer.Ordinal)
    {
        ["transition-0-degradado2"] = "atan2/frac cambia de lado en la diagonal de centros de píxel",
        ["graded-COLOR+LUT+VIGNETTE+ADVANCED-hdr"] = "PQ y ADVANCED amplifican el filtrado y las trascendentes",
        ["graded-ADVANCED-vista-calificador"] = "el filtrado amplificado por la rampa del calificador supera p99,9 (README fase 0)",
        ["graded-COLOR+LUT+VIGNETTE-hdr"] = "PQ y filtrado arbitrario (README fase 0: máximo 4,9e-3)",
        ["transition-0-empuje"] = "desplazamiento a fracciones arbitrarias: truncamiento bilineal de WARP",
        ["transition-0-deslizar"] = "desplazamiento a fracciones arbitrarias: truncamiento bilineal de WARP",
        ["transition-0-glitch"] = "desplazamiento a fracciones arbitrarias: truncamiento bilineal de WARP",
        ["transition-BLEND-empuje-modo9"] = "fracciones arbitrarias amplificadas por la mezcla diferencia",
        ["muestreo-escala0.9"] = "sonda de fracciones arbitrarias: truncamiento bilineal de WARP (README fase 0)",
        ["fx-distorsion"] = "coordenadas arbitrarias: truncamiento bilineal de WARP (README fase 0)",
        ["fx-reencuadre360"] = "coordenadas arbitrarias y asin (README fase 0)",
        ["fx-reencuadre360-estereo"] = "coordenadas arbitrarias y asin (README fase 0)",
        ["transition-0-rebote"] = "el bilineal de WARP amplificado supera la media propuesta (fase 0: 3,2e-4)",
        ["graded-COLOR+LUT+VIGNETTE+ADVANCED-girado"] = "bilineal arbitrario amplificado por ADVANCED en la capa girada (fase 0: 2,5e-2)",
        ["graded-COLOR+LUT+VIGNETTE+ADVANCED-interpolado"] = "bilineal arbitrario amplificado por ADVANCED en la capa girada (fase 0: 8,2e-2)",
        ["sharpen-umbral"] = "el umbral de la nitidez es un escalón: en lavapipe unos pocos píxeles del borde pasan p99,9 (4,9e-3)",
        ["graded-ADVANCED-centros"] = "la fuente está centrada, pero curvas y LUT usan fracciones arbitrarias",
        ["graded-ADVANCED-solo-curvas-tono-centros"] = "la tabla de curvas usa fracciones arbitrarias aunque la fuente esté centrada",
    };

    private static readonly Dictionary<string, string> s_hardwareExceptions = new(StringComparer.Ordinal)
    {
        ["transition-0-degradado2"] = "discontinuidad de atan2/frac en centros (control del README)",
        ["graded-COLOR+LUT+VIGNETTE-hdr"] = "pow en PQ (control del README: 1,465e-3)",
        ["graded-COLOR+LUT+VIGNETTE+ADVANCED-hdr"] = "pow en PQ y ADVANCED (control del README: 5,9e-3)",
        ["fx-reencuadre360-estereo"] = "asin del controlador frente a fxc (control del README: 1,2e-3)",
    };

    [WarpGenerationFact]
    public void Generate_warp_references()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            throw new PlatformNotSupportedException();
        }

        string directory = ReferenceDirectory();
        Directory.CreateDirectory(directory);
        CaseSpec[] cases = ValidatedCases();
        using D3D11Gpu gpu = D3D11Gpu.Create(preferSoftware: true);
        using var renderer = new EffectCaseRenderer(gpu);
        foreach (CaseSpec spec in cases)
        {
            float[] pixels = renderer.Render(spec);
            Assert.All(pixels, static p => Assert.True(float.IsFinite(p)));
            File.WriteAllBytes(Path.Combine(directory, spec.Name + ".bin"), MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray());
        }

        File.WriteAllText(Path.Combine(directory, "casos.json"), JsonSerializer.Serialize(
            new ReferenceManifest(Cases.Size, Cases.Size, "float32-le", cases.Select(c => new ReferenceCase(c.Name, Cases.Size * Cases.Size * 4)).ToArray()), s_json));
        output.WriteLine($"WARP: {cases.Length} referencias de {Cases.Size}×{Cases.Size}, float32 LE; {directory}");
    }

    [WindowsComparisonFact]
    public void Vulkan_matches_warp_and_hardware()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            throw new PlatformNotSupportedException();
        }

        CaseSpec[] cases = ValidatedCases();
        using var vulkan = new EffectCaseRenderer(VulkanTestGpu.Gpu);
        Dictionary<string, float[]> actual = cases.ToDictionary(c => c.Name, vulkan.Render, StringComparer.Ordinal);
        var results = new List<ComparisonResult>();
        using (D3D11Gpu warp = D3D11Gpu.Create(preferSoftware: true))
        using (var renderer = new EffectCaseRenderer(warp))
        {
            results.AddRange(Compare(cases, actual, renderer.Render, "WARP", hardware: false));
        }

        CompareHardware(cases, actual, results);
        Finish(results);
    }

    [SupportedOSPlatform("windows7.0")]
    private void CompareHardware(CaseSpec[] cases, Dictionary<string, float[]> actual, List<ComparisonResult> results)
    {
        D3D11Gpu hardware;
        try
        {
            hardware = D3D11Gpu.Create();
        }
        catch (RenderException e)
        {
            output.WriteLine($"Control D3D11 hardware no disponible: {e.Message}");
            return;
        }

        using (hardware)
        {
            if (hardware.Caps.IsSoftware)
            {
                output.WriteLine("Control D3D11 hardware omitido: el adaptador es software.");
                return;
            }

            output.WriteLine($"Vulkan: {VulkanTestGpu.Gpu.Caps.Adapter}; D3D11: {hardware.Caps.Adapter}");
            using var renderer = new EffectCaseRenderer(hardware);
            results.AddRange(Compare(cases, actual, renderer.Render, "D3D11 hardware", hardware: true));
        }
    }

    [ReferenceComparisonFact]
    public void Vulkan_matches_saved_warp_references()
    {
        string directory = ReferenceDirectory();
        Assert.True(Directory.Exists(directory), $"No existe la carpeta de referencias: {directory}");
        ReferenceManifest manifest = JsonSerializer.Deserialize<ReferenceManifest>(File.ReadAllText(Path.Combine(directory, "casos.json")))
            ?? throw new InvalidDataException("casos.json vacío");
        CaseSpec[] cases = ValidatedCases();
        Assert.Equal(Cases.Size, manifest.Width);
        Assert.Equal(Cases.Size, manifest.Height);
        Assert.Equal("float32-le", manifest.Encoding);
        Assert.Equal(cases.Select(c => c.Name).Order(StringComparer.Ordinal), manifest.Cases.Select(c => c.Name).Order(StringComparer.Ordinal));
        Assert.All(manifest.Cases, static c => Assert.Equal(Cases.Size * Cases.Size * 4, c.Values));
        using var vulkan = new EffectCaseRenderer(VulkanTestGpu.Gpu);
        Dictionary<string, float[]> actual = cases.ToDictionary(c => c.Name, vulkan.Render, StringComparer.Ordinal);
        Finish(Compare(cases, actual, spec =>
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(directory, spec.Name + ".bin"));
            Assert.Equal(Cases.Size * Cases.Size * 4 * sizeof(float), bytes.Length);
            return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        }, "WARP archivos", hardware: false));
    }

    private static string ReferenceDirectory() => Environment.GetEnvironmentVariable("RODAJE_REFERENCIAS_WARP")
        ?? throw new InvalidOperationException("Falta RODAJE_REFERENCIAS_WARP");

    private static CaseSpec[] ValidatedCases()
    {
        CaseSpec[] cases = Cases.All();
        Assert.Equal(168, cases.Length);
        Assert.Equal(168, cases.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(77, cases.Select(c => c.Variant).Distinct().Count());
        Assert.Equal(77, CaseVariants.All.Length);
        Assert.All(cases, static c => { Assert.Equal(68, c.Quad.Length); Assert.Equal(104, c.Effects.Length); });
        return cases;
    }

    private static List<ComparisonResult> Compare(CaseSpec[] cases, Dictionary<string, float[]> actual,
        Func<CaseSpec, float[]> reference, string comparison, bool hardware)
    {
        var results = new List<ComparisonResult>();
        foreach (CaseSpec spec in cases)
        {
            float[] expected = reference(spec);
            float[] pixels = actual[spec.Name];
            Assert.Equal(expected.Length, pixels.Length);
            var errors = new double[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                Assert.True(float.IsFinite(expected[i]) && float.IsFinite(pixels[i]), $"{spec.Name}[{i}]: valor no finito");
                errors[i] = Math.Abs((double)pixels[i] - expected[i]);
            }

            Array.Sort(errors);
            double max = errors[^1], mean = errors.Average(), p999 = errors[(int)Math.Ceiling(errors.Length * 0.999) - 1];
            Dictionary<string, string> exceptions = hardware ? s_hardwareExceptions : s_warpExceptions;
            bool exception = exceptions.TryGetValue(spec.Name, out string? reason);
            string criterion = exception ? "excepción" : hardware ? "iii" : s_exact.Contains(spec.Name) ? "i" : "ii";
            bool passed = exception ? mean <= 5e-3 : criterion is "i" or "iii" ? max <= 1e-3 : mean <= 2e-4 && p999 <= 4e-3;
            results.Add(new ComparisonResult(comparison, spec.Name, criterion, max, mean, p999, passed, reason));
        }

        return results;
    }

    private void Finish(List<ComparisonResult> results)
    {
        output.WriteLine("Comparación | Casos | (i) cumplen/total | (ii) cumplen/total | (iii) cumplen/total | Excepciones | Peor máximo (caso; media; p99,9)");
        foreach (IGrouping<string, ComparisonResult> group in results.GroupBy(r => r.Comparison))
        {
            ComparisonResult worst = group.MaxBy(r => r.MaxError)!;
            string Counts(string criterion) => $"{group.Count(r => r.Criterion == criterion && r.Passed)}/{group.Count(r => r.Criterion == criterion)}";
            output.WriteLine($"{group.Key} | {group.Count()} | {Counts("i")} | {Counts("ii")} | {Counts("iii")} | {Counts("excepción")} | {worst.MaxError:G6} ({worst.Name}; {worst.MeanError:G6}; {worst.P999:G6})");
        }

        foreach (ComparisonResult result in results.Where(r => r.Criterion == "excepción"))
        {
            output.WriteLine($"EXCEPCIÓN {result.Comparison}: {result.Name}: máximo={result.MaxError:G6}, media={result.MeanError:G6}, p99,9={result.P999:G6}; {result.Reason}");
        }

        foreach (ComparisonResult result in results.Where(r => !r.Passed))
        {
            output.WriteLine($"FALLO {result.Comparison}: {result.Name} ({result.Criterion}): máximo={result.MaxError:G6}, media={result.MeanError:G6}, p99,9={result.P999:G6}");
        }

        string? report = Environment.GetEnvironmentVariable("RODAJE_VK_INFORME");
        if (!string.IsNullOrWhiteSpace(report))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            File.WriteAllText(report, JsonSerializer.Serialize(results, s_json));
        }

        Assert.True(results.All(r => r.Passed), $"{results.Count(r => !r.Passed)} casos fuera del criterio; véase la tabla.");
    }

    private sealed record ReferenceCase(string Name, int Values);
    private sealed record ReferenceManifest(int Width, int Height, string Encoding, ReferenceCase[] Cases);
    private sealed record ComparisonResult(string Comparison, string Name, string Criterion,
        double MaxError, double MeanError, double P999, bool Passed, string? Reason);
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class WarpGenerationFactAttribute : FactAttribute
{
    public WarpGenerationFactAttribute()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(7)) Skip = "Las referencias WARP se generan en Windows.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RODAJE_REFERENCIAS_WARP"))) Skip = "Falta RODAJE_REFERENCIAS_WARP para generar referencias.";
        else if (Environment.GetEnvironmentVariable("RODAJE_REFERENCIAS_LEER") == "1") Skip = "Modo de lectura de referencias; no se sobrescriben.";
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsComparisonFactAttribute : FactAttribute
{
    public WindowsComparisonFactAttribute()
    {
        Skip = !OperatingSystem.IsWindowsVersionAtLeast(7) ? "La comparación en proceso con D3D11 requiere Windows."
            : Environment.GetEnvironmentVariable("RODAJE_REFERENCIAS_LEER") == "1" ? "Modo de lectura de referencias WARP."
            : VulkanTestGpu.SkipReason;
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ReferenceComparisonFactAttribute : FactAttribute
{
    public ReferenceComparisonFactAttribute()
    {
        Skip = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RODAJE_REFERENCIAS_WARP")) ? "Falta RODAJE_REFERENCIAS_WARP para comparar referencias."
            : OperatingSystem.IsWindowsVersionAtLeast(7) && Environment.GetEnvironmentVariable("RODAJE_REFERENCIAS_LEER") != "1" ? "En Windows se compara con WARP en proceso; RODAJE_REFERENCIAS_LEER=1 fuerza la lectura."
            : VulkanTestGpu.SkipReason;
    }
}




