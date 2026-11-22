using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Probe;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Export.Tests.Running;

/// <summary>
/// Medios de la vida real exportados de punta a punta, tal como quedan al arrastrarlos a una secuencia 720p: el destello
/// y el pitido que empiezan juntos en el original siguen a menos de un cuadro en el MP4 exportado, y salen todos los
/// cuadros. MPEG-TS de cámara no exportaba nada («el cuadro 0 no se pudo decodificar») y una foto JPEG tampoco.
/// Necesita la GPU (D3D11): solo en Windows.
/// </summary>
[Collection(ExportGpuGroup.Name)]
public sealed class OddMediaExportTests(ITestOutputHelper output) : IDisposable
{
    private const string FlashAndBeep = "-f lavfi -i color=c=black:s=320x240:r=30:d=3,drawbox=c=white:t=fill:enable=between(t\\,1\\,1.2) "
        + "-f lavfi -i aevalsrc=if(between(t\\,1\\,1.2)\\,0.8*sin(2*PI*1000*t)\\,0):s=48000:d=3";

    private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-exportar raros ñ-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [OddExportTheory]
    [InlineData("camara.ts", "-c:v mpeg2video -bf 2 -g 300 -c:a mp2")]
    [InlineData("avchd 10 horas.m2ts", "-c:v mpeg2video -bf 2 -g 300 -c:a ac3 -f mpegts -mpegts_m2ts_mode 1 -output_ts_offset 36000")]
    [InlineData("recortado.mp4", "-c:v mpeg4 -c:a aac -output_ts_offset -0.5 -avoid_negative_ts disabled")]
    [InlineData("girado.mp4", "-c:v mpeg4 -c:a aac -metadata:s:v:0 rotate=90")]
    [InlineData("96k float.mov", "-c:v mpeg4 -c:a pcm_f32le -ar 96000")]
    public async Task Flash_and_beep_stay_within_a_frame_after_export(string name, string codecs)
    {
        string source = Make(name, FlashAndBeep + " " + codecs + " -shortest");
        (Sequence sequence, string path) = await ExportAsync(source, withAudio: true);

        long frames = sequence.Duration.ToFrames(sequence.FrameRate);
        long flash = -1;
        int count = FfmpegTool.Default!.DecodeGrayFrames(path, 32, 18, (i, gray) =>
        {
            if (flash < 0 && gray[(9 * 32) + 16] > 128)
            {
                flash = i;
            }
        });
        float[] audio = FfmpegTool.Default.DecodeAudio(path, 48000, 1);
        int beep = Array.FindIndex(audio, s => Math.Abs(s) > 0.25f);
        double offset = (beep / 48000.0) - Time.FromFrames(flash, sequence.FrameRate).ToSeconds();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name}: {count} de {frames} cuadros; destello en {flash}, pitido a {offset * 1000:0.0} ms de él"));
        Assert.InRange(count, frames, frames + 1);
        Assert.True(flash > 0 && beep > 0, $"destello {flash}, pitido {beep}");
        Assert.InRange(Math.Abs(offset), 0, 1.0 / sequence.FrameRate.ToDouble());
    }

    [OddExportFact]
    public async Task A_camera_photo_exports()
    {
        string source = Make("foto.jpg", "-f lavfi -i testsrc2=s=1921x1081,noise=alls=40:allf=t,scale=1920:1080 -frames:v 1 -update 1");
        (Sequence sequence, string path) = await ExportAsync(source, withAudio: false);
        int count = FfmpegTool.Default!.DecodeGrayFrames(path, 32, 18, static (_, _) => { });
        Assert.Equal(sequence.Duration.ToFrames(sequence.FrameRate), count);
    }

    private string Make(string name, string arguments)
    {
        string path = Path.Combine(_folder, name);
        FfmpegTool.Default!.Run(["-hide_banner", "-loglevel", "error", "-y", .. arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), path]);
        return path;
    }

    private async Task<(Sequence Sequence, string Path)> ExportAsync(string source, bool withAudio)
    {
        var media = new MediaRef { Id = "m", Path = source, Name = Path.GetFileName(source) };
        MediaInfo info = MediaProbe.Probe(source);
        info.ApplyTo(media);
        Time duration = info.IsStillImage ? Time.FromSeconds(2) : media.Duration;
        var sequence = new Sequence { Id = "sec", Name = "Raros", FrameRate = Rational.Fps30, Width = 1280, Height = 720 };
        var project = new Project { Id = "p", Name = "Raros", Sequences = [sequence] };
        project.Media.Add(media);
        var video = new Track { Id = "v0", Kind = TrackKind.Video, Name = "Video 1" };
        video.Clips.Add(new Clip { Id = "cv", MediaId = "m", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = duration, LinkId = withAudio ? "l" : null });
        sequence.VideoTracks.Add(video);
        if (withAudio)
        {
            var audio = new Track { Id = "a0", Kind = TrackKind.Audio, Name = "Audio 1" };
            audio.Clips.Add(new Clip { Id = "ca", MediaId = "m", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = duration, LinkId = "l" });
            sequence.AudioTracks.Add(audio);
        }

        Assert.Empty(ModelValidator.Validate(project));
        IReadOnlyList<EncoderSpec> encoders = await new CatalogEncoderSource(Skip.ProxyExecutable!).GetAsync();
        string path = Path.Combine(_folder, Path.GetFileNameWithoutExtension(source) + " exportado.mp4");
        var settings = new ExportSettings
        {
            Video = new VideoExportSettings { Codec = ExportCodec.H264 },
            Audio = withAudio ? new AudioExportSettings { Codec = ExportCodec.Aac } : null,
        };
        var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = Skip.ProxyExecutable! });
        using ExportJob job = runner.Start(new ExportRequest(project, "sec", path, settings), encoders);
        await job.Completion.WaitAsync(TimeSpan.FromMinutes(3));
        Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());
        return (sequence, path);
    }
}

/// <summary>Exportar de verdad necesita la GPU de las pruebas de exportación y el ffmpeg.exe de los proxies.</summary>
public sealed class OddExportTheoryAttribute : TheoryAttribute
{
    public OddExportTheoryAttribute() => Skip = Rodaje.TestMedia.Skip.First(Rodaje.TestMedia.Skip.Proxies, Rodaje.TestMedia.Skip.GpuTextures);
}

/// <summary>Como <see cref="OddExportTheoryAttribute"/>, para un solo caso.</summary>
public sealed class OddExportFactAttribute : FactAttribute
{
    public OddExportFactAttribute() => Skip = Rodaje.TestMedia.Skip.First(Rodaje.TestMedia.Skip.Proxies, Rodaje.TestMedia.Skip.GpuTextures);
}
