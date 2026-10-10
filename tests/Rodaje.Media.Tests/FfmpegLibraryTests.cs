using Rodaje.Media.Tests.Support;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests;

public sealed class FfmpegLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Rodaje-locator-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Candidates_follow_the_documented_order()
    {
        string app = Path.Combine(_root, "app");
        var env = new Dictionary<string, string?>
        {
            ["RODAJE_FFMPEG"] = Path.Combine(_root, "env-ffmpeg"),
            ["RODAJE_ENTORNO"] = Path.Combine(_root, "entorno"),
        };

        IReadOnlyList<string> candidates = FfmpegLocator.Candidates(Path.Combine(_root, "explicit"), app, k => env.GetValueOrDefault(k));

        Assert.Equal(Path.Combine(_root, "explicit"), candidates[0]);
        Assert.Equal(Path.Combine(_root, "env-ffmpeg"), candidates[1]);
        Assert.Equal(Path.Combine(app, "ffmpeg"), candidates[2]);
        Assert.Equal(Path.Combine(_root, "entorno", "ffmpeg", "bin"), candidates[3]);
        Assert.Equal(Path.Combine(app, ".entorno", "ffmpeg", "bin"), candidates[4]);
        Assert.Contains(Path.Combine(_root, ".entorno", "ffmpeg", "bin"), candidates);
    }

    [Fact]
    public void Candidates_skip_missing_variables_and_duplicates()
    {
        string app = Path.Combine(_root, "app");
        IReadOnlyList<string> candidates = FfmpegLocator.Candidates(null, app, _ => null);
        Assert.Equal(Path.Combine(app, "ffmpeg"), candidates[0]);
        Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void A_worktree_falls_back_to_the_main_checkout()
    {
        string main = Path.Combine(_root, "principal");
        string worktree = Path.Combine(_root, "otro sitio", "agente con espacios");
        Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "agente"));
        Directory.CreateDirectory(Path.Combine(worktree, "tests", "bin"));
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {Path.Combine(main, ".git", "worktrees", "agente").Replace('\\', '/')}\n");

        Assert.Equal(main, FfmpegLocator.MainCheckoutOfWorktree(worktree));
        IReadOnlyList<string> candidates = FfmpegLocator.Candidates(null, Path.Combine(worktree, "tests", "bin"), _ => null);
        Assert.Contains(Path.Combine(main, ".entorno", "ffmpeg", "bin"), candidates);
        Assert.True(
            candidates.ToList().IndexOf(Path.Combine(worktree, ".entorno", "ffmpeg", "bin"))
            < candidates.ToList().IndexOf(Path.Combine(main, ".entorno", "ffmpeg", "bin")),
            "El .entorno propio del worktree va antes que el del checkout principal.");
    }

    [Fact]
    public void A_normal_repository_is_not_a_worktree()
    {
        Directory.CreateDirectory(Path.Combine(_root, "repo", ".git"));
        Assert.Null(FfmpegLocator.MainCheckoutOfWorktree(Path.Combine(_root, "repo")));
        File.WriteAllText(Path.Combine(_root, ".git"), "basura");
        Assert.Null(FfmpegLocator.MainCheckoutOfWorktree(_root));
    }

    [Fact]
    public void Find_requires_every_library()
    {
        string partial = Path.Combine(_root, "parcial");
        string complete = Path.Combine(_root, "completa");
        Directory.CreateDirectory(partial);
        Directory.CreateDirectory(complete);
        foreach (string name in FfmpegLocator.RequiredLibraries)
        {
            File.WriteAllBytes(Path.Combine(complete, name + ".dll"), []);
        }

        File.WriteAllBytes(Path.Combine(partial, "avcodec-63.dll"), []);

        Assert.Equal(complete, FfmpegLocator.Find([Path.Combine(_root, "no-existe"), partial, complete]));
        Assert.Null(FfmpegLocator.Find([partial]));
    }

    [Fact]
    public void A_bundled_ffmpeg_folder_ignores_RODAJE_FFMPEG()
    {
        string app = Path.Combine(_root, "dist");
        string elsewhere = Path.Combine(_root, "gpl");
        var env = new Dictionary<string, string?> { ["RODAJE_FFMPEG"] = elsewhere };

        Assert.Equal(elsewhere, FfmpegLocator.Candidates(null, app, k => env.GetValueOrDefault(k))[0]);

        Directory.CreateDirectory(Path.Combine(app, "ffmpeg"));
        IReadOnlyList<string> candidates = FfmpegLocator.Candidates(null, app, k => env.GetValueOrDefault(k));
        Assert.Equal(Path.Combine(app, "ffmpeg"), candidates[0]);
        Assert.DoesNotContain(elsewhere, candidates);
    }

    [Fact]
    public void License_check_refuses_gpl_and_nonfree_builds()
    {
        const string Lgpl = "--prefix=/ffbuild/prefix --enable-version3 --enable-shared --disable-libx264 --enable-libopenh264";
        (string, string?)[] Configurations(string avcodec) =>
            [("avutil", Lgpl), ("avcodec", avcodec), ("avformat", Lgpl), ("swresample", Lgpl), ("swscale", Lgpl)];

        Assert.Null(FfmpegLibrary.LicenseProblem("LGPL version 3 or later", Configurations(Lgpl)));
        Assert.Null(FfmpegLibrary.LicenseProblem("LGPL version 2.1 or later", Configurations(Lgpl + " --disable-gpl")));

        string? gpl = FfmpegLibrary.LicenseProblem("LGPL version 3 or later", Configurations(Lgpl + " --enable-gpl --enable-libx264"));
        Assert.NotNull(gpl);
        Assert.Contains("--enable-gpl", gpl, StringComparison.Ordinal);
        Assert.Contains("avcodec", gpl, StringComparison.Ordinal);

        Assert.Contains("--enable-nonfree",
            FfmpegLibrary.LicenseProblem("LGPL version 3 or later", Configurations("--enable-nonfree --enable-libfdk-aac")),
            StringComparison.Ordinal);
        Assert.Contains("GPL version 3 or later",
            FfmpegLibrary.LicenseProblem("GPL version 3 or later", Configurations(Lgpl)),
            StringComparison.Ordinal);
        Assert.NotNull(FfmpegLibrary.LicenseProblem("nonfree and unredistributable", Configurations(Lgpl)));
        Assert.NotNull(FfmpegLibrary.LicenseProblem(null, Configurations(Lgpl)));
    }

    [FfmpegLibraryFact]
    public void The_loaded_ffmpeg_is_lgpl()
    {
        Assert.True(FfmpegLibrary.IsInitialized);
        Assert.StartsWith("LGPL", FFmpeg.AutoGen.Abstractions.ffmpeg.avcodec_license(), StringComparison.Ordinal);
        Assert.Null(FfmpegLibrary.LicenseProblem(
            FFmpeg.AutoGen.Abstractions.ffmpeg.avcodec_license(),
            [("avcodec", FFmpeg.AutoGen.Abstractions.ffmpeg.avcodec_configuration())]));
    }

    [FfmpegLibraryFact]
    public void Loads_ffmpeg_9_by_absolute_path()
    {
        Assert.True(FfmpegLibrary.IsInitialized);
        Assert.True(FfmpegLocator.HasAllLibraries(FfmpegLibrary.Directory!));
        Assert.StartsWith("n9.", FfmpegLibrary.VersionInfo, StringComparison.Ordinal);
        FfmpegLibrary.EnsureInitialized();
    }
}
