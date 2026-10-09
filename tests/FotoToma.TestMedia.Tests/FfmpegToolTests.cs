namespace FotoToma.TestMedia.Tests;

public sealed class FfmpegToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fototoma ffmpeg ñ {Guid.NewGuid():N}");

    public FfmpegToolTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string FakeExe(params string[] parts)
    {
        string path = Path.Combine([_root, .. parts, "ffmpeg.exe"]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Locate_PrefersEnvironmentVariable_FileOrDirectory()
    {
        string exe = FakeExe("propio");
        FakeExe("en path");

        (FfmpegTool? byFile, _) = FfmpegTool.Locate(Env(("FOTOTOMA_FFMPEG", exe), ("PATH", Path.Combine(_root, "en path"))), _root);
        (FfmpegTool? byDir, _) = FfmpegTool.Locate(Env(("FOTOTOMA_FFMPEG", Path.GetDirectoryName(exe)!)), _root);

        Assert.Equal(exe, byFile!.ExecutablePath);
        Assert.Equal(FfmpegTool.EnvironmentVariable, byFile.Source);
        Assert.Equal(exe, byDir!.ExecutablePath);
    }

    [Fact]
    public void Locate_ReportsBrokenEnvironmentVariable_WithoutFallingBack()
    {
        FakeExe("en path");
        string missing = Path.Combine(_root, "no existe", "ffmpeg.exe");

        (FfmpegTool? tool, string? reason) = FfmpegTool.Locate(Env(("FOTOTOMA_FFMPEG", missing), ("PATH", Path.Combine(_root, "en path"))), _root);

        Assert.Null(tool);
        Assert.Contains(missing, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Locate_FindsEntornoOfAnyAncestorRepository()
    {
        File.WriteAllText(Path.Combine(_root, "FotoToma.slnx"), "<Solution />");
        string exe = FakeExe(".entorno", "ffmpeg", "bin");
        string deep = Path.Combine(_root, ".claude", "worktrees", "agente", "tests", "bin");
        Directory.CreateDirectory(deep);

        (FfmpegTool? tool, _) = FfmpegTool.Locate(Env(("PATH", Path.Combine(_root, "nada"))), deep);

        Assert.Equal(exe, tool!.ExecutablePath);
        Assert.Equal(".entorno", tool.Source);
    }

    [Fact]
    public void Locate_UsesFotoTomaEntornoVariable()
    {
        string exe = FakeExe("otro entorno", "ffmpeg", "bin");

        (FfmpegTool? tool, _) = FfmpegTool.Locate(Env(("FOTOTOMA_ENTORNO", Path.Combine(_root, "otro entorno"))), _root);

        Assert.Equal(exe, tool!.ExecutablePath);
    }

    [Fact]
    public void Locate_FallsBackToPath_ThenExplainsAbsence()
    {
        string exe = FakeExe("en path");
        string path = string.Join(Path.PathSeparator, Path.Combine(_root, "vacía"), "\"" + Path.Combine(_root, "en path") + "\"");

        (FfmpegTool? found, _) = FfmpegTool.Locate(Env(("PATH", path)), _root);
        (FfmpegTool? none, string? reason) = FfmpegTool.Locate(Env(), _root);

        Assert.Equal(exe, found!.ExecutablePath);
        Assert.Equal("PATH", found.Source);
        Assert.Null(none);
        Assert.Contains(FfmpegTool.EnvironmentVariable, reason, StringComparison.Ordinal);
    }

    [FfmpegFact]
    public void ProbeEncoder_ExplainsUnknownEncoder()
    {
        string? reason = FfmpegTool.Default!.ProbeEncoder("codificador_que_no_existe");

        Assert.Contains("codificador_que_no_existe", reason, StringComparison.Ordinal);
        Assert.False(FfmpegTool.Default.IsEncoderUsable("codificador_que_no_existe"));
        Assert.StartsWith("ffmpeg version", FfmpegTool.Default.Version, StringComparison.Ordinal);
    }

    [FfmpegFact]
    public void Run_FailureCarriesStderr()
    {
        var ex = Assert.Throws<FfmpegException>(() => FfmpegTool.Default!.Run(["-hide_banner", "-i", Path.Combine(_root, "no hay.mp4"), "-f", "null", "-"]));

        Assert.Contains("no hay.mp4", ex.Message, StringComparison.Ordinal);
    }
}
