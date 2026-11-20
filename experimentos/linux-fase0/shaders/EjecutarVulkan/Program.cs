using System.Globalization;
using System.Runtime.InteropServices;
using Fase0.Shaders;

// ejecutar-vulkan [carpeta spirv] [carpeta ref]: también por FASE0_SPIRV y FASE0_REF; RODAJE_VK_GPU elige la GPU;
// FASE0_RESULTADO, dónde escribir resultado.json; FASE0_VOLCADO, carpeta para los píxeles de Vulkan.
const double Threshold = 1e-3;
Console.OutputEncoding = System.Text.Encoding.UTF8;
string root = FindRoot();
string spirvDir = Path.GetFullPath(Environment.GetEnvironmentVariable("FASE0_SPIRV") ?? (args.Length > 0 ? args[0] : Path.Combine(root, "salida", "spirv")));
string refDir = Path.GetFullPath(Environment.GetEnvironmentVariable("FASE0_REF") ?? (args.Length > 1 ? args[1] : Path.Combine(root, "salida", "ref")));
string resultPath = Environment.GetEnvironmentVariable("FASE0_RESULTADO") ?? Path.Combine(Path.GetDirectoryName(refDir)!, "resultado.json");
// FASE0_VOLCADO: carpeta donde dejar lo que dibuja Vulkan (mismo formato que los .bin de referencia), para diagnosticar.
string? dumpDir = Environment.GetEnvironmentVariable("FASE0_VOLCADO") is { Length: > 0 } d ? Directory.CreateDirectory(d).FullName : null;

Manifest manifest = Json.Read(Path.Combine(spirvDir, "manifiesto.json"), Json.Default.Manifest);
CaseFile cases = Json.Read(Path.Combine(refDir, "casos.json"), Json.Default.CaseFile);
var variants = manifest.Variants.ToDictionary(v => v.Index);

using var gpu = new VulkanRunner(cases.Width, cases.Height, Environment.GetEnvironmentVariable("RODAJE_VK_GPU"));
Console.WriteLine($"Dispositivo: {gpu.DeviceName} ({gpu.DriverInfo})");
gpu.LoadVertex(File.ReadAllBytes(Path.Combine(spirvDir, manifest.Vertex.File)), manifest.Vertex.Resources);
foreach (TextureSpec t in cases.TextureSet)
{
    gpu.AddTexture(t);
}

var results = new List<CaseResult>();
foreach (CaseSpec c in cases.Cases)
{
    results.Add(RunCase(c));
}

int compared = results.Count(r => r.Status is "ok" or "supera");
int passed = results.Count(r => r.Status == "ok");
int covered = results.Where(r => r.Status == "ok").Select(r => r.Variant).Distinct().Count();
int allPassed = results.GroupBy(r => r.Variant).Count(g => g.All(r => r.Status == "ok"));
Console.WriteLine();
Console.WriteLine($"{"caso",-48} {"var",3} {"error máx",10} {"error medio",11} {"> umbral",8}  estado");
foreach (CaseResult r in results)
{
    string worstAt = r.Status == "supera" && r.WorstPixel is { } p
        ? string.Create(CultureInfo.InvariantCulture, $" (x={p[0]}, y={p[1]}, canal {p[2]}: ref {r.WorstReference![p[2]]:0.#####}, Vulkan {r.WorstVulkan![p[2]]:0.#####})")
        : "";
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{r.Name,-48} {r.Variant,3} {r.MaxError,10:0.000E+0} {r.MeanError,11:0.000E+0} {r.ValuesOver,8}  {r.Status}{worstAt}{(r.Detail is null ? "" : " " + r.Detail)}"));
}

Console.WriteLine();
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"{passed} de {results.Count} casos con error máximo ≤ {Threshold:E0} ({compared} comparados); variantes con todos sus casos ≤ umbral: {allPassed} de {manifest.Variants.Length}, con alguno: {covered}."));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"Peor caso: {results.MaxBy(r => r.MaxError)?.Name} con {results.Max(r => r.MaxError):E3}."));
Json.Write(resultPath, new RunResult
{
    Device = gpu.DeviceName,
    Driver = gpu.DriverInfo,
    Threshold = Threshold,
    Compared = compared,
    Passed = passed,
    VariantsCovered = covered,
    VariantsAllPassed = allPassed,
    Cases = [.. results],
}, Json.Default.RunResult);
Console.WriteLine($"Resultado en {resultPath}");
return passed == results.Count ? 0 : 1;

CaseResult RunCase(CaseSpec c)
{
    if (!variants.TryGetValue(c.Variant, out ShaderEntry? v) || !v.Status.StartsWith("ok", StringComparison.Ordinal))
    {
        return new CaseResult { Name = c.Name, Variant = c.Variant, Status = "sin-spirv", MaxError = double.PositiveInfinity, Detail = v?.Error };
    }

    string refFile = Path.Combine(refDir, c.Name + ".bin");
    if (!File.Exists(refFile))
    {
        return new CaseResult { Name = c.Name, Variant = c.Variant, Status = "sin-referencia", MaxError = double.PositiveInfinity };
    }

    float[] expected = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(refFile)).ToArray();
    float[] actual;
    try
    {
        actual = gpu.Draw(c, v, File.ReadAllBytes(Path.Combine(spirvDir, v.File)));
    }
    catch (Exception ex) when (ex is VulkanException or InvalidDataException)
    {
        return new CaseResult { Name = c.Name, Variant = c.Variant, Status = "error", MaxError = double.PositiveInfinity, Detail = ex.Message };
    }

    if (dumpDir is not null)
    {
        File.WriteAllBytes(Path.Combine(dumpDir, c.Name + ".bin"), MemoryMarshal.AsBytes(actual.AsSpan()).ToArray());
    }

    double max = 0, sum = 0;
    int worst = 0, over = 0;
    for (int i = 0; i < expected.Length; i++)
    {
        double d = Math.Abs((double)actual[i] - expected[i]);
        if (double.IsNaN(d))
        {
            d = float.IsNaN(actual[i]) && float.IsNaN(expected[i]) ? 0 : double.PositiveInfinity;
        }

        sum += d;
        over += d > Threshold ? 1 : 0;
        if (d > max)
        {
            max = d;
            worst = i;
        }
    }

    int px = worst / 4;
    return new CaseResult
    {
        Name = c.Name,
        Variant = c.Variant,
        Status = max <= Threshold ? "ok" : "supera",
        MaxError = max,
        MeanError = sum / expected.Length,
        ValuesOver = over,
        WorstPixel = max > 0 ? [px % cases.Width, px / cases.Width, worst % 4] : null,
        WorstReference = max > 0 ? expected.AsSpan(px * 4, 4).ToArray() : null,
        WorstVulkan = max > 0 ? actual.AsSpan(px * 4, 4).ToArray() : null,
    };
}

static string FindRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "ci.sh")) && Directory.Exists(Path.Combine(dir.FullName, "EjecutarVulkan")))
        {
            return dir.FullName;
        }
    }

    return Directory.GetCurrentDirectory();
}
