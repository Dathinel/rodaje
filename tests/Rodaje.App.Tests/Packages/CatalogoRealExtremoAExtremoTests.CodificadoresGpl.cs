using System.Diagnostics;
using System.Text;
using Rodaje.App.Services;
using Rodaje.App.Services.Modules;
using Rodaje.App.Services.Packages;
using Rodaje.App.Tests.Matrix;
using Rodaje.App.Tests.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Export;
using Rodaje.Media.Export;
using Rodaje.Media.Proxies;
using Rodaje.Packages;
using Rodaje.Packages.Download;
using Rodaje.TestMedia;

namespace Rodaje.App.Tests.Packages;

/// <summary>
/// El complemento <c>rodaje-codificadores-gpl</c> desde el catálogo publicado: se instala con sus licencias y la fuente
/// correspondiente publicada donde dice su LEEME; un proyecto se exporta con x264 y x265 (8 y 10 bits) y audio Opus y
/// MP3 por su <c>ffmpeg.exe</c> GPL, cada archivo se comprueba con el ffprobe LGPL del editor, y se desinstala.
/// </summary>
public sealed partial class CatalogoRealExtremoAExtremoTests
{
    private static readonly MatrixCase TomaCpu = new("catalogo-gpl-vp9-opus", "WebM", "VP9 1280×720 30 fps", "Opus estéreo", ".webm")
    {
        Seconds = 3,
        Video = ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-crf", "34", "-b:v", "0", "-g", "30", "-pix_fmt", "yuv420p"],
        Audio = ["-c:a", "libopus", "-b:a", "96k", "-ac", "2"], Requires = "libvpx-vp9",
    };

    [RedFact]
    public void Los_codificadores_gpl_se_instalan_del_catalogo_exportan_x264_y_x265_y_se_desinstalan() => UiThread.Run(async () =>
    {
        PackageService service = Service();
        var downloader = Assert.IsType<GitHubReleaseDownloader>(service.Downloader);
        SignedIndex index = await downloader.GetIndexAsync();
        IReadOnlyDictionary<string, PackageVersion> available = await service.AvailableVersionsAsync();
        Assert.Equal("0.2.0", available[GplEncoderPack.Id].ToString());
        Assert.All(service.Catalog.Entries, e => Assert.True(available.ContainsKey(e.Id), $"{e.Id} no está publicado."));
        Note($"Índice {index.Generated:O}: {index.Entries.Count} entradas.");

        var watch = Stopwatch.StartNew();
        InstallResult installed = await service.InstallFromCatalogAsync(service.Catalog.Find(GplEncoderPack.Id)!);
        Assert.Equal(InstallOutcome.Installed, installed.Outcome);
        Note($"{GplEncoderPack.Id} {installed.Package.Manifest.Version}: {Size(Latest(index, GplEncoderPack.Id).Size)} en {watch.Elapsed.TotalSeconds:0.0} s.");

        // Licencias dentro del paquete y la fuente correspondiente donde dice el LEEME, con la misma huella.
        string folder = installed.Package.Directory;
        string[] notices = [.. Directory.GetFiles(Path.Combine(folder, "licencias")).Select(f => Path.GetFileName(f))];
        foreach (string expected in new[] { "x264-COPYING.txt", "x265-COPYING.txt", "libopus-COPYING.txt", "lame-COPYING.txt", "libiconv-COPYING.LIB.txt",
                     "mingw-w64-runtime-COPYING.txt", "winpthreads-COPYING.txt", "gcc-COPYING.RUNTIME.txt", "FFmpeg-LICENSE.md" })
        {
            Assert.Contains(expected, notices);
        }

        Assert.True(File.Exists(Path.Combine(folder, "COPYING.GPLv3.txt")));
        Assert.Contains("--enable-gpl", File.ReadAllText(Path.Combine(folder, "configure.txt")), StringComparison.Ordinal);
        string readme = File.ReadAllText(Path.Combine(folder, "LEEME.md"));
        Assert.Contains("Patentes", readme, StringComparison.Ordinal);
        string sourceUrl = readme.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("https://", StringComparison.Ordinal) && l.EndsWith("-fuente.tar", StringComparison.Ordinal));
        using (var http = new HttpClient())
        {
            string declared = (await http.GetStringAsync(sourceUrl + ".sha256")).Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];
            Assert.Contains(declared, readme, StringComparison.Ordinal);
            using HttpResponseMessage head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, sourceUrl));
            Assert.True(head.IsSuccessStatusCode, $"{sourceUrl}: {(int)head.StatusCode}");
            Note($"Fuente correspondiente publicada: {Size(head.Content.Headers.ContentLength ?? 0)}, SHA-256 {declared[..12]}…; {notices.Length} avisos en licencias/.");
        }

        FfmpegExecutable gpl = GplEncoderPack.TryOpen([new ModuleRoot(Locations.Modules, ModuleOrigin.Installed)], out string? problem)
            ?? throw new Xunit.Sdk.XunitException(problem);
        Assert.True(gpl.IsGplAddOn);
        Note(gpl.Version);

        (string? media, string? mediaProblem) = MatrixMedia.GetOrCreate(FfmpegTool.Default!, TomaCpu);
        Assert.True(media is not null, mediaProblem);
        var session = new EditorSession(new FakePlayback(), new FfmpegMediaProber(), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([media!]);
        Assert.True(session.AppendToTimeline(session.Project.Media[0].Id));
        // La exportación incluye el cuadro en el que termina el último clip (ExportPlan.CeilFrame).
        Rodaje.Core.Timing.Time end = session.Sequence.Duration;
        long frames = end.ToFrames(session.Sequence.FrameRate);
        frames += Rodaje.Core.Timing.Time.FromFrames(frames, session.Sequence.FrameRate) < end ? 1 : 0;
        Note($"Proyecto: {session.Sequence.Width}×{session.Sequence.Height}, {session.Sequence.FrameRate} fps, {end.ToSeconds():0.000} s, {frames} cuadros.");

        var runner = new AppExportRunner(null, null) { FindGplFfmpeg = () => GplEncoderPack.TryOpen([new ModuleRoot(Locations.Modules, ModuleOrigin.Installed)], out _) };
        IReadOnlyList<EncoderSpec> encoders = await runner.GetEncodersAsync();
        Assert.Contains(encoders, e => e.Id == "libx264" && e.License == EncoderLicense.GplAddOn);
        Assert.Contains(encoders, e => e.Id == "libx265" && e.License == EncoderLicense.GplAddOn);

        foreach ((ExportCodec codec, string video, int bits, ExportCodec audioCodec, string audio, string probeVideo, string probeAudio, string signature) in new[]
                 {
                     (ExportCodec.H264, "libx264", 8, ExportCodec.Opus, "libopus", "h264", "opus", "x264 - core"),
                     (ExportCodec.H264, "libx264", 10, ExportCodec.Mp3, "libmp3lame", "h264", "mp3", "x264 - core"),
                     (ExportCodec.Hevc, "libx265", 8, ExportCodec.Mp3, "libmp3lame", "hevc", "mp3", "x265 (build"),
                     (ExportCodec.Hevc, "libx265", 10, ExportCodec.Opus, "libopus", "hevc", "opus", "x265 (build"),
                 })
        {
            var settings = new ExportSettings
            {
                Container = ExportContainer.Mp4,
                Video = new VideoExportSettings { Codec = codec, EncoderId = video, BitDepth = bits },
                Audio = new AudioExportSettings { Codec = audioCodec, EncoderId = audio },
            };
            string path = Path.Combine(_folder.Path, $"gpl {video} {bits} bits {audio}.mp4");
            double seconds = await ExportAsync(runner, session.Project.Clone(), session.SequenceId, path, settings);
            string probe = await ProbeAsync("-count_frames", "-show_entries", "stream=codec_type,codec_name,pix_fmt,width,height,nb_read_frames,sample_rate,channels", "-of", "compact", path);
            string[] lines = probe.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string videoLine = Assert.Single(lines, l => l.Contains("codec_type=video", StringComparison.Ordinal));
            string audioLine = Assert.Single(lines, l => l.Contains("codec_type=audio", StringComparison.Ordinal));
            foreach (string token in new[] { $"codec_name={probeVideo}", "width=1280", "height=720", $"pix_fmt={(bits == 10 ? "yuv420p10le" : "yuv420p")}", $"nb_read_frames={frames}" })
            {
                Assert.True(videoLine.Contains(token, StringComparison.Ordinal), $"{token}: {videoLine}");
            }

            foreach (string token in new[] { $"codec_name={probeAudio}", "sample_rate=48000", "channels=2" })
            {
                Assert.True(audioLine.Contains(token, StringComparison.Ordinal), $"{token}: {audioLine}");
            }

            Assert.Contains(signature, Encoding.Latin1.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
            Note($"{video} {bits} bits + {audio}: {Size(new FileInfo(path).Length)} en {seconds:0.0} s ({videoLine}; {audioLine}).");
        }

        await service.UninstallAsync(GplEncoderPack.Id);
        Assert.Null(service.Installed.Find(GplEncoderPack.Id));
        Assert.Null(GplEncoderPack.FindExecutable([new ModuleRoot(Locations.Modules, ModuleOrigin.Installed)]));
        Assert.DoesNotContain(await new AppExportRunner(null, null) { FindGplFfmpeg = () => GplEncoderPack.TryOpen([new ModuleRoot(Locations.Modules, ModuleOrigin.Installed)], out _) }.GetEncodersAsync(),
            e => e.License == EncoderLicense.GplAddOn);
        Note($"{GplEncoderPack.Id} desinstalado: Exportar ya no ofrece x264 ni x265.");
    }, TimeSpan.FromMinutes(15));
}
