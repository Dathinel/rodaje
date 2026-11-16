using Rodaje.Core.Model;
using Rodaje.Media.Probe;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests;

/// <summary>La fecha de grabación y la cámara que traen las etiquetas de QuickTime/MP4.</summary>
public sealed class MediaTagsTests
{
    [Fact]
    public void Lee_la_fecha_y_la_camara_de_apple_con_preferencia_por_la_fecha_local()
    {
        MediaTags tags = MediaTags.Parse(new Dictionary<string, string>
        {
            ["creation_time"] = "2026-10-03T17:20:11.000000Z",
            ["com.apple.quicktime.creationdate"] = "2026-10-03T12:20:11-0500",
            ["com.apple.quicktime.make"] = "Apple",
            ["com.apple.quicktime.model"] = "iPhone 15 Pro",
        });

        Assert.Equal(new DateTimeOffset(2026, 10, 3, 12, 20, 11, TimeSpan.FromHours(-5)), tags.Created);
        Assert.Equal("Apple iPhone 15 Pro", tags.Camera);
    }

    [Fact]
    public void Un_modelo_que_ya_lleva_la_marca_no_la_repite_y_lo_que_falta_queda_nulo()
    {
        Assert.Equal("Canon EOS R6", MediaTags.Parse(new Dictionary<string, string> { ["MAKE"] = "Canon", ["model"] = "Canon EOS R6" }).Camera);
        Assert.Equal("Pixel 8", MediaTags.Parse(new Dictionary<string, string> { ["com.android.model"] = "Pixel 8" }).Camera);
        Assert.Equal(MediaTags.None, MediaTags.Parse(new Dictionary<string, string> { ["creation_time"] = "1970-01-01T00:00:00Z", ["encoder"] = "Lavf" }));
        Assert.Null(MediaTags.Parse(new Dictionary<string, string> { ["creation_time"] = "ayer" }).Created);
    }

    [Fact]
    public void Lee_el_codigo_de_tiempo_y_el_carrete()
    {
        MediaTags tags = MediaTags.Parse(new Dictionary<string, string> { ["timecode"] = "01:00:00;00", ["reel_name"] = "A001" });

        Assert.Equal(("01:00:00;00", "A001"), (tags.Timecode, tags.Reel));
        Assert.Equal((null, null), (MediaTags.None.Timecode, MediaTags.None.Reel));
    }

    /// <summary>Un MOV con pista tmcd, como los de cámara: el TC llega al medio (D-062).</summary>
    [FfmpegLibraryFact]
    public void Un_mov_con_timecode_lo_lleva_al_medio()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rodaje-tc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "toma.mov");
            FfmpegTool.Default!.Run(["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30000/1001", "-t", "1", "-c:v", "mpeg4",
                "-timecode", "01:00:00;00", "-metadata:s:v:0", "reel_name=A001", path]);

            var media = new MediaRef { Path = path };
            MediaProbe.Probe(path).ApplyTo(media);

            Assert.Equal("01:00:00;00", media.Timecode);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
