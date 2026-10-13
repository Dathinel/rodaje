using Rodaje.Media.Probe;

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
}
