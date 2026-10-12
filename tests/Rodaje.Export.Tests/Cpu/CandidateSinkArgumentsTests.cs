using Rodaje.Core.Timing;
using Rodaje.Export.Encoders;
using Rodaje.Media.Export;

namespace Rodaje.Export.Tests.Cpu;

/// <summary>
/// Sin <c>ffmpeg.exe</c>: la línea de comandos completa del sink con los codificadores que usa la app
/// (<see cref="ExportEncoderCandidates.All"/>, en <c>AppExportRunner</c>), no los del catálogo. Así los argumentos fijos
/// del catálogo (el <c>-g 1</c> de FFV1, sobre todo) se comprueban donde de verdad se exporta.
/// </summary>
public sealed class CandidateSinkArgumentsTests
{
    [Fact]
    public void Ffv1_from_the_app_candidates_reaches_the_sink_with_one_frame_gop()
    {
        EncoderSpec ffv1 = Candidate("ffv1");
        EncoderSpec flac = Candidate("flac");
        const ExportPixelFormat pixels = ExportPixelFormat.Yuv420P;
        var settings = new ExportSettings
        {
            Container = ExportContainer.Mkv,
            Video = new VideoExportSettings
            {
                Codec = ffv1.Codec, EncoderId = ffv1.Id, RateControl = new RateControl { Mode = ffv1.RateControls[0] },
                BitDepth = pixels.BitDepth(), Chroma = pixels.Chroma(), Alpha = pixels.HasAlpha(),
            },
            Audio = new AudioExportSettings
            {
                Codec = flac.Codec, EncoderId = flac.Id, RateControl = new RateControl { Mode = flac.RateControls[0] },
            },
        };
        Assert.Empty(ExportSettingsValidator.Validate(settings, ffv1, flac));
        EncoderArguments arguments = FfmpegArguments.Build(settings, ffv1, pixels, flac, Rational.Fps30, TimeSpan.FromSeconds(2));

        var format = new ExportVideoFormat(320, 180, Rational.Fps30, pixels, ExportColor.For(320, 180, pixels));
        var request = new ExportSinkRequest("salida.mkv", ExportContainer.Mkv, format, ffv1, arguments.Video,
            new ExportAudioFormat(48_000, 2), "mezcla.wav", flac, arguments.Audio, 60);
        List<string> line = [.. FfmpegPipeArguments.Build(request, "salida.tmp.mkv", null)];

        int codec = line.IndexOf("-c:v:0");
        Assert.True(codec >= 0 && line[codec + 1] == "ffv1", string.Join(' ', line));
        // Un solo «-g», el de FFV1, y vale 1: cada cuadro es clave.
        Assert.Single(line, a => a == "-g");
        Assert.Equal("1", line[line.IndexOf("-g") + 1]);
        Assert.Equal("3", line[line.IndexOf("-level") + 1]);
        Assert.Equal("1", line[line.IndexOf("-slicecrc") + 1]);
    }

    [Fact]
    public void Every_candidate_in_the_catalog_carries_its_fixed_arguments()
    {
        foreach (EncoderSpec candidate in ExportEncoderCandidates.All)
        {
            if (ExportEncoderCatalog.Find(candidate.Id) is { } catalog)
            {
                Assert.True(catalog.BaseArguments.SequenceEqual(candidate.BaseArguments),
                    $"{candidate.Id}: candidato «{string.Join(' ', candidate.BaseArguments)}», catálogo «{string.Join(' ', catalog.BaseArguments)}».");
            }
        }
    }

    private static EncoderSpec Candidate(string id) =>
        ExportEncoderCandidates.All.Single(e => string.Equals(e.Id, id, StringComparison.Ordinal));
}
