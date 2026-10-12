using System.Globalization;
using Rodaje.Core.Timing;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Xunit.Abstractions;
using static Rodaje.Export.Tests.Acceptance.AcceptanceSupport;

namespace Rodaje.Export.Tests.Acceptance;

/// <summary>
/// Calidad de los preajustes con VMAF (libvmaf del ffmpeg LGPL, modelo v0.6.1) contra el maestro FFV1 de los mismos
/// 30 s de la escena: YouTube 1080p ≥93 (≥85 en el 1 % peor), Archivo pequeño ≤3 Mbps con ≥85, Máxima calidad ≥97, y
/// AV1 por CPU ≤60 % del tamaño de NVENC H.264 a VMAF ±1.
/// </summary>
/// <remarks>La escena es sintética (degradado con ruido que se desplaza, recuadro que rebota y números): más dura de comprimir que un video real.</remarks>
[Collection(ExportAcceptanceGroup.Name)]
public sealed class ExportQualityAcceptanceTests(ITestOutputHelper output)
{
    private static readonly ExportRange Range = new(Time.Zero, Time.FromSeconds(30));

    [ExportAcceptanceFact]
    public async Task Presets_reach_their_vmaf()
    {
        AcceptanceScene scene = Scene(1);
        string folder = Folder("vmaf");
        string reference = Path.Combine(folder, "referencia.mkv");
        await ExportAsync(output, scene.Project, reference, new ExportSettings
        {
            Container = ExportContainer.Mkv,
            Video = new VideoExportSettings { Codec = ExportCodec.Ffv1, RateControl = RateControl.Lossless },
        }, Range);

        var results = new List<string>();
        async Task<(double Mean, double P1, double Mbps)> Measure(string name, ExportSettings settings)
        {
            string path = Path.Combine(folder, name);
            await ExportAsync(output, scene.Project, path, settings with { Audio = null, Performance = ExportPerformance.Fastest }, Range);
            (double mean, double p1, double psnr) = Vmaf(reference, path, folder);
            double mbps = new FileInfo(path).Length * 8 / Range.Duration.ToSeconds() / 1e6;
            string line = string.Create(CultureInfo.InvariantCulture, $"{name}: VMAF {mean:0.00} (1 % peor {p1:0.00}), PSNR-Y {psnr:0.00} dB, {mbps:0.00} Mbps; peores {Worst}");
            output.WriteLine(line);
            results.Add(line);
            return (mean, p1, mbps);
        }

        (double youtube, double youtubeP1, _) = await Measure("youtube-1080p.mp4", ExportPresets.YouTube1080p.Settings);
        (double small, _, double smallMbps) = await Measure("archivo-pequeno.mkv", ExportPresets.SmallFile.Settings with { Container = ExportContainer.Mkv });
        (double best, _, _) = await Measure("maxima-calidad.mp4", ExportPresets.BestQuality.Settings);

        // AV1 contra NVENC H.264 a la misma calidad: NVENC a CQ 23 y la CRF de AV1 que más se acerque (búsqueda binaria).
        (double nvenc, _, double nvencMbps) = await Measure("nvenc-cq23.mp4", new ExportSettings
        {
            Video = new VideoExportSettings { Codec = ExportCodec.H264, EncoderId = "h264_nvenc", RateControl = RateControl.ConstantQuality(23), Profile = "high" },
        });
        int lo = 20;
        int hi = 63;
        (double Vmaf, double Mbps, int Crf)? match = null;
        while (lo <= hi)
        {
            int crf = (lo + hi) / 2;
            (double v, _, double mbps) = await Measure($"av1-crf{crf}.mkv", new ExportSettings
            {
                Container = ExportContainer.Mkv,
                Video = new VideoExportSettings { Codec = ExportCodec.Av1, RateControl = RateControl.ConstantQuality(crf) },
            });
            if (Math.Abs(v - nvenc) <= 1)
            {
                match = (v, mbps, crf);
                break;
            }

            if (v > nvenc)
            {
                lo = crf + 1;
            }
            else
            {
                hi = crf - 1;
            }
        }

        double ratio = match is { } m ? m.Mbps / nvencMbps : double.NaN;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"AV1 a VMAF ±1 de NVENC ({nvenc:0.00}): {(match is { } mm ? $"CRF {mm.Crf}, {mm.Vmaf:0.00}, {mm.Mbps:0.00} Mbps" : "sin coincidencia")} = {ratio:P0} del tamaño de NVENC."));
        Delete(folder);

        Assert.True(youtube >= 93 && youtubeP1 >= 85, $"YouTube 1080p: {youtube:0.00} / {youtubeP1:0.00}");
        Assert.True(smallMbps <= 3 && small >= 85, $"Archivo pequeño: {smallMbps:0.00} Mbps, {small:0.00}");
        Assert.True(best >= 97, $"Máxima calidad: {best:0.00}");
        Assert.True(ratio <= 0.6, $"AV1/NVENC {ratio:P0}");
    }
}
