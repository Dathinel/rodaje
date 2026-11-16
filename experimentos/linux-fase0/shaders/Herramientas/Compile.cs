using System.Diagnostics;
using System.Text;
using Rodaje.Render;
using Rodaje.Render.Effects;
using Rodaje.Render.Shaders;

namespace Fase0.Shaders;

/// <summary>compilar: cada variante de <c>EffectShaders.All</c> y el vertex shader con DXC a SPIR-V, más el manifiesto.</summary>
internal static class Compile
{
    // Mapa fijo de registros HLSL a bindings (set 0): b0–b15 → 0–15, t0–t15 → 16–31, s0–s15 → 32–47, u0–u15 → 48–63.
    public const int BShift = 0, TShift = 16, SShift = 32, UShift = 48;

    public static readonly string[] CommonArguments =
    [
        "-spirv", "-fspv-target-env=vulkan1.1", "-O3",
        "-fvk-b-shift", $"{BShift}", "0", "-fvk-t-shift", $"{TShift}", "0",
        "-fvk-s-shift", $"{SShift}", "0", "-fvk-u-shift", $"{UShift}", "0",
    ];

    public static string FileName(int index, ShaderVariant v) =>
        $"{index:00}-{v.EntryPoint}-{(v.Defines.Length == 0 ? "0" : string.Join('+', v.Defines))}.spv";

    public static int Run(string outDir)
    {
        string dxc = Environment.GetEnvironmentVariable("RODAJE_DXC") ?? Path.Combine(AppContext.BaseDirectory, "dxc", "dxc.exe");
        string spirvDir = Path.Combine(outDir, "spirv");
        string hlslDir = Path.Combine(outDir, "hlsl");
        Directory.CreateDirectory(spirvDir);
        Directory.CreateDirectory(hlslDir);
        foreach (string old in Directory.EnumerateFiles(spirvDir, "*.spv"))
        {
            File.Delete(old);
        }

        string effects = Path.Combine(hlslDir, "rodaje-efectos.hlsl");
        string render = Path.Combine(hlslDir, "rodaje-render.hlsl");
        File.WriteAllText(effects, EffectShaders.Source);
        File.WriteAllText(render, RenderShaders.Source);

        string version = Version(dxc);
        ShaderEntry vertex = CompileOne(dxc, render, spirvDir, -1, "vs-VsQuad.spv", "VsQuad", "vs_6_0", [], ["-fvk-invert-y"], true);
        var variants = new ShaderEntry[EffectShaders.All.Length];
        Parallel.For(0, variants.Length, i =>
        {
            ShaderVariant v = EffectShaders.All[i];
            variants[i] = CompileOne(dxc, effects, spirvDir, i, FileName(i, v), v.EntryPoint, "ps_6_0", v.Defines, [], true);
        });

        // Aparte: el parche mínimo (README) sobre una copia, con el HLSL 2021 de DXC y con fxc. Sus .spv van a spirv-parche2021.
        string patchedDir = Path.Combine(outDir, "spirv-parche2021");
        Directory.CreateDirectory(patchedDir);
        string patchedSource = Hlsl2021Patch.Apply(EffectShaders.Source);
        string patched = Path.Combine(hlslDir, "rodaje-efectos-parche2021.hlsl");
        File.WriteAllText(patched, patchedSource);
        File.Copy(Path.Combine(spirvDir, vertex.File), Path.Combine(patchedDir, vertex.File), true);
        var patchedVariants = new ShaderEntry[variants.Length];
        Parallel.For(0, variants.Length, i =>
        {
            ShaderVariant v = EffectShaders.All[i];
            ShaderEntry p = CompileOne(dxc, patched, patchedDir, i, FileName(i, v), v.EntryPoint, "ps_6_0", v.Defines, [], false);
            patchedVariants[i] = p with { Status = p.Status == "ok" ? "ok-parche" : p.Status };
            string fxc = "ok";
            try
            {
                ShaderCompiler.Compile((v with { Source = patchedSource }).FullSource(), v.EntryPoint, v.Profile, v.SourceName);
            }
            catch (RenderException ex)
            {
                fxc = ex.Message;
            }

            variants[i] = variants[i] with { Hlsl2021Patch = p.Status == "ok" ? "ok" : p.Error, FxcWithPatch = fxc };
        });

        string[] arguments = [.. CommonArguments, "-T", "ps_6_0|vs_6_0", "(vs: -fvk-invert-y)"];
        Json.Write(Path.Combine(spirvDir, "manifiesto.json"),
            new Manifest { Compiler = version, Arguments = arguments, Vertex = vertex, Variants = variants }, Json.Default.Manifest);
        Json.Write(Path.Combine(patchedDir, "manifiesto.json"),
            new Manifest { Compiler = version, Arguments = [.. arguments, "(HLSL 2021 con el parche)"], Vertex = vertex, Variants = patchedVariants },
            Json.Default.Manifest);

        int ok = variants.Count(v => v.Status == "ok");
        int hv = variants.Count(v => v.Status == "ok-hv2018");
        Console.WriteLine(version);
        Console.WriteLine($"Vertex shader VsQuad: {vertex.Status}");
        Console.WriteLine($"Compilan con DXC a SPIR-V tal cual: {ok} de {variants.Length}; solo con -HV 2018: {hv}; fallan: {variants.Length - ok - hv}.");
        Console.WriteLine($"Con el parche mínimo y HLSL 2021: {variants.Count(v => v.Hlsl2021Patch == "ok")} de {variants.Length}; " +
            $"el parche con fxc (ps_5_0): {variants.Count(v => v.FxcWithPatch == "ok")} de {variants.Length}.");
        foreach (var group in variants.Where(v => v.Hlsl2021Patch != "ok").GroupBy(v => v.Hlsl2021Patch))
        {
            Console.WriteLine($"Error con el parche ({string.Join(", ", group.Select(v => v.Index))}): {group.Key}");
        }

        foreach (var group in variants.Where(v => v.FxcWithPatch != "ok").GroupBy(v => v.FxcWithPatch))
        {
            Console.WriteLine($"Error de fxc con el parche ({string.Join(", ", group.Select(v => v.Index))}): {group.Key}");
        }

        foreach (var group in variants.Where(v => v.Error != null).GroupBy(v => v.Error))
        {
            Console.WriteLine();
            Console.WriteLine($"Error con el HLSL 2021 por defecto ({group.Count()} variantes: {string.Join(", ", group.Select(v => v.Index))}):");
            Console.WriteLine(group.Key);
        }

        foreach (var group in variants.Where(v => v.Warnings != null).GroupBy(v => v.Warnings))
        {
            Console.WriteLine();
            Console.WriteLine($"Avisos ({group.Count()} variantes: {string.Join(", ", group.Select(v => v.Index))}):");
            Console.WriteLine(group.Key);
        }

        return vertex.Status.StartsWith("ok", StringComparison.Ordinal) && variants.All(v => v.Status.StartsWith("ok", StringComparison.Ordinal)) ? 0 : 1;
    }

    private static ShaderEntry CompileOne(string dxc, string source, string dir, int index, string file, string entry, string profile,
        string[] defines, string[] extra, bool fallback2018)
    {
        string output = Path.Combine(dir, file);
        var args = new List<string>(CommonArguments) { "-T", profile, "-E", entry };
        args.AddRange(extra);
        foreach (string d in defines)
        {
            args.Add("-D");
            args.Add($"{d}=1");
        }

        (int code, string log) = Run(dxc, [.. args, "-Fo", output, source]);
        string status = "ok";
        string? error = null;
        if (code != 0)
        {
            error = Clean(log, source);
            status = "error";
            if (fallback2018)
            {
                (code, log) = Run(dxc, [.. args, "-HV", "2018", "-Fo", output, source]);
                status = code == 0 ? "ok-hv2018" : "error";
            }
        }

        string? warnings = code == 0 && log.Length > 0 ? Clean(log, source) : null;
        ResourceBinding[] resources = code == 0
            ? [.. SpirvReflect.Read(File.ReadAllBytes(output)).Select(r => new ResourceBinding
            {
                Register = Register(r),
                Name = r.Name,
                Set = r.Set,
                Binding = r.Binding,
                Kind = r.Kind,
                Dimension = r.Dimension,
                Size = r.Size,
            })]
            : [];
        if (code != 0 && fallback2018)
        {
            error += "\n--- con -HV 2018 ---\n" + Clean(log, source);
        }

        return new ShaderEntry
        {
            Index = index,
            File = file,
            Entry = entry,
            Profile = profile,
            Defines = defines,
            Status = status,
            Error = error,
            Warnings = warnings,
            Resources = resources,
        };
    }

    private static string Register(SpirvReflect.Resource r)
    {
        (char letter, int shift) = r.Binding switch
        {
            < TShift => ('b', BShift),
            < SShift => ('t', TShift),
            < UShift => ('s', SShift),
            _ => ('u', UShift),
        };
        return $"{letter}{r.Binding - shift}";
    }

    private static string Clean(string log, string source) => log.Replace(source, Path.GetFileName(source), StringComparison.OrdinalIgnoreCase).Trim();

    private static string Version(string dxc)
    {
        (_, string log) = Run(dxc, ["--version"]);
        return log.Trim();
    }

    private static (int Code, string Log) Run(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"No se pudo arrancar {exe}.");
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, (stdout.Result + stderr).Trim());
    }
}
