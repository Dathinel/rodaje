using System.Text;
using Rodaje.Core.Interchange;
using Rodaje.Core.Model;

namespace Rodaje.Core.Tests.Interchange;

/// <summary>Corpus hostil del intercambio para EDL, FCPXML y OpenTimelineIO (fase F4t de D-062).</summary>
public sealed class HostileInterchangeTests
{
    // =========================================================================
    // XML y FCPXML
    // =========================================================================

    [Fact]
    public void Fcpxml_billion_laughs_entity_expansion_throws_interchange_exception()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE fcpxml [
              <!ENTITY lol "lol">
              <!ENTITY lol1 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
              <!ENTITY lol2 "&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;">
              <!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">
              <!ENTITY lol4 "&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;">
            ]>
            <fcpxml version="1.10">
              <library><event name="&lol4;"/></library>
            </fcpxml>
            """;

        Assert.Throws<InterchangeException>(() =>
            InterchangeFiles.ReadDraft(xml, InterchangeFormat.Fcpxml, null, new InterchangeOptions(), new InterchangeReport()));
    }

    [Fact]
    public void Fcpxml_external_entity_system_xxe_throws_interchange_exception()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE fcpxml [
              <!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">
            ]>
            <fcpxml version="1.10">
              <library><event name="&xxe;"/></library>
            </fcpxml>
            """;

        Assert.Throws<InterchangeException>(() =>
            InterchangeFiles.ReadDraft(xml, InterchangeFormat.Fcpxml, null, new InterchangeOptions(), new InterchangeReport()));
    }

    [Fact]
    public void Fcpxml_ten_thousand_nesting_levels_throws_interchange_exception()
    {
        string open = string.Concat(Enumerable.Repeat("<clip>", 10_000));
        string close = string.Concat(Enumerable.Repeat("</clip>", 10_000));
        string xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE fcpxml><fcpxml version=\"1.10\"><library><event><project><sequence><spine>{open}{close}</spine></sequence></project></event></library></fcpxml>";

        Assert.Throws<InterchangeException>(() =>
            InterchangeFiles.ReadDraft(xml, InterchangeFormat.Fcpxml, null, new InterchangeOptions(), new InterchangeReport()));
    }

    [Fact]
    public void Fcpxml_duration_division_by_zero_throws_interchange_exception()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE fcpxml>
            <fcpxml version="1.10">
              <resources>
                <format id="r1" frameDuration="1/25s" width="1920" height="1080"/>
              </resources>
              <library>
                <event name="Evento">
                  <project name="Proyecto">
                    <sequence format="r1" duration="1/0s">
                      <spine>
                        <gap name="Hueco" offset="0s" duration="1/0s"/>
                      </spine>
                    </sequence>
                  </project>
                </event>
              </library>
            </fcpxml>
            """;

        Assert.Throws<InterchangeException>(() =>
            InterchangeFiles.ReadDraft(xml, InterchangeFormat.Fcpxml, null, new InterchangeOptions(), new InterchangeReport()));
    }

    // =========================================================================
    // OpenTimelineIO
    // =========================================================================

    [Fact(Skip = "Pendiente del lector de OTIO (F4b)")]
    public void Otio_ten_thousand_nested_stacks_throws_interchange_exception()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 10_000; i++)
        {
            sb.Append("{\"OTIO_SCHEMA\":\"Stack.1\",\"name\":\"s\",\"children\":[");
        }
        sb.Append("{\"OTIO_SCHEMA\":\"Gap.1\",\"name\":\"g\"}");
        for (int i = 0; i < 10_000; i++)
        {
            sb.Append("]}");
        }

        string json = $"{{\"OTIO_SCHEMA\":\"Timeline.1\",\"name\":\"deep\",\"tracks\":{sb}}}";

        Assert.Throws<InterchangeException>(() =>
            InterchangeFiles.ReadDraft(json, InterchangeFormat.Otio, null, new InterchangeOptions(), new InterchangeReport()));
    }

    [Fact(Skip = "Pendiente del lector de OTIO (F4b)")]
    public void Otio_rate_zero_throws_interchange_exception()
    {
        string json = """
            {
              "OTIO_SCHEMA": "Timeline.1",
              "name": "RateZero",
              "tracks": {
                "OTIO_SCHEMA": "Stack.1",
                "name": "tracks",
                "children": [
                  {
                    "OTIO_SCHEMA": "Track.1",
                    "name": "V1",
                    "kind": "Video",
                    "children": [
                      {
                        "OTIO_SCHEMA": "Gap.1",
                        "name": "Hueco",
                        "source_range": {
                          "OTIO_SCHEMA": "TimeRange.1",
                          "start_time": { "OTIO_SCHEMA": "RationalTime.1", "value": 0, "rate": 0.0 },
                          "duration": { "OTIO_SCHEMA": "RationalTime.1", "value": 10, "rate": 0.0 }
                        }
                      }
                    ]
                  }
                ]
              }
            }
            """;

        Assert.Throws<InterchangeException>(() =>
            InterchangeFiles.ReadDraft(json, InterchangeFormat.Otio, null, new InterchangeOptions(), new InterchangeReport()));
    }

    [Theory(Skip = "Pendiente del lector de OTIO (F4b)")]
    [InlineData("NaN")]
    [InlineData("1e308")]
    public void Otio_value_nan_or_overflow_throws_interchange_exception(string value)
    {
        string json = $$"""
            {
              "OTIO_SCHEMA": "Timeline.1",
              "name": "InvalidValue",
              "tracks": {
                "OTIO_SCHEMA": "Stack.1",
                "name": "tracks",
                "children": [
                  {
                    "OTIO_SCHEMA": "Track.1",
                    "name": "V1",
                    "kind": "Video",
                    "children": [
                      {
                        "OTIO_SCHEMA": "Gap.1",
                        "name": "Hueco",
                        "source_range": {
                          "OTIO_SCHEMA": "TimeRange.1",
                          "start_time": { "OTIO_SCHEMA": "RationalTime.1", "value": {{value}}, "rate": 24.0 },
                          "duration": { "OTIO_SCHEMA": "RationalTime.1", "value": 10, "rate": 24.0 }
                        }
                      }
                    ]
                  }
                ]
              }
            }
            """;

        Assert.Throws<InterchangeException>(() =>
            InterchangeFiles.ReadDraft(json, InterchangeFormat.Otio, null, new InterchangeOptions(), new InterchangeReport()));
    }

    // =========================================================================
    // Código de tiempo inválido 99:99:99:99
    // =========================================================================

    [Fact]
    public void Edl_invalid_timecode_99_99_99_99_throws_or_reports_warning()
    {
        string edl = """
            TITLE: Hostil
            FCM: NON-DROP FRAME
            001  TOMA01   V     C     99:99:99:99 99:99:99:99 01:00:00:00 01:00:05:00
            * FROM CLIP NAME: Toma01
            """;
        var report = new InterchangeReport();
        try
        {
            InterchangeFiles.ReadDraft(edl, InterchangeFormat.Edl, null, new InterchangeOptions(), report);
            Assert.False(report.IsEmpty);
        }
        catch (InterchangeException)
        {
            // Rechazo directo válido
        }
    }

    [Fact]
    public void Fcpxml_invalid_timecode_99_99_99_99_throws_or_reports_warning()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE fcpxml>
            <fcpxml version="1.10">
              <resources>
                <format id="r1" frameDuration="1/25s" width="1920" height="1080"/>
              </resources>
              <library>
                <event name="Evento">
                  <project name="Proyecto">
                    <sequence format="r1" duration="25s" tcStart="99:99:99:99" tcFormat="NDF">
                      <spine/>
                    </sequence>
                  </project>
                </event>
              </library>
            </fcpxml>
            """;
        var report = new InterchangeReport();
        try
        {
            InterchangeFiles.ReadDraft(xml, InterchangeFormat.Fcpxml, null, new InterchangeOptions(), report);
            Assert.False(report.IsEmpty);
        }
        catch (InterchangeException)
        {
            // Rechazo directo válido
        }
    }

    [Fact(Skip = "Pendiente del lector de OTIO (F4b)")]
    public void Otio_invalid_timecode_throws_or_reports_warning()
    {
        string json = """
            {
              "OTIO_SCHEMA": "Timeline.1",
              "name": "HostilTC",
              "metadata": { "timecode": "99:99:99:99" },
              "tracks": {
                "OTIO_SCHEMA": "Stack.1",
                "name": "tracks",
                "children": []
              }
            }
            """;
        var report = new InterchangeReport();
        try
        {
            InterchangeFiles.ReadDraft(json, InterchangeFormat.Otio, null, new InterchangeOptions(), report);
        }
        catch (InterchangeException)
        {
            // Rechazo directo válido
        }
    }

    // =========================================================================
    // Rutas .., UNC y de 40 000 caracteres
    // =========================================================================

    [Fact]
    public void Normalize_hostile_paths_leaves_them_unresolved_or_safe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // UNC debe quedar como nulo (sin sondear medios de red)
        Assert.Null(SequenceBuilder.NormalizePath(@"\\servidor\share\clip.mov", @"C:\base"));
        Assert.Null(SequenceBuilder.NormalizePath("file://servidor/share/clip.mov", @"C:\base"));

        // Ruta de 40 000 caracteres no debe colgar ni desbordar
        string superLong = @"C:\medios\" + new string('a', 40_000) + ".mov";
        Assert.Null(SequenceBuilder.NormalizePath(superLong, @"C:\base"));

        // Ruta con .. se normaliza con Path.GetFullPath sin escapar
        string dotDot = @"C:\base\sub\..\clip.mov";
        Assert.Equal(@"C:\base\clip.mov", SequenceBuilder.NormalizePath(dotDot, @"C:\base"));
    }

    [Fact]
    public void Fcpxml_hostile_paths_are_rejected_or_reported()
    {
        string longName = new string('x', 40_000);
        string xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE fcpxml>
            <fcpxml version="1.10">
              <resources>
                <format id="r1" frameDuration="1/25s" width="1920" height="1080"/>
                <asset id="r2" name="unc" format="r1" duration="10s"><media-rep src="file://servidor/share/clip.mov"/></asset>
                <asset id="r3" name="dotdot" format="r1" duration="10s"><media-rep src="file:///C:/base/../../etc/passwd"/></asset>
                <asset id="r4" name="long" format="r1" duration="10s"><media-rep src="file:///C:/{longName}.mov"/></asset>
              </resources>
              <library><event name="e"><project name="p"><sequence format="r1"><spine/></sequence></project></event></library>
            </fcpxml>
            """;
        var report = new InterchangeReport();
        try
        {
            TimelineDraft draft = InterchangeFiles.ReadDraft(xml, InterchangeFormat.Fcpxml, null, new InterchangeOptions(), report);
            // Si no lanzó excepción, las rutas hostiles deben quedar sin resolver o avisadas
            Assert.True(draft.Project.Media.All(m => m.Path.Length == 0 || !m.Path.StartsWith(@"\\", StringComparison.Ordinal)));
        }
        catch (InterchangeException)
        {
            // Rechazo directo válido
        }
    }

    [Fact(Skip = "Pendiente del lector de OTIO (F4b)")]
    public void Otio_hostile_paths_are_rejected_or_reported()
    {
        string longName = new string('x', 40_000);
        string json = $$"""
            {
              "OTIO_SCHEMA": "Timeline.1",
              "name": "HostilePaths",
              "tracks": {
                "OTIO_SCHEMA": "Stack.1",
                "name": "tracks",
                "children": [
                  {
                    "OTIO_SCHEMA": "Track.1",
                    "name": "V1",
                    "kind": "Video",
                    "children": [
                      {
                        "OTIO_SCHEMA": "Clip.1",
                        "name": "c1",
                        "source_range": { "OTIO_SCHEMA": "TimeRange.1", "start_time": { "OTIO_SCHEMA": "RationalTime.1", "value": 0, "rate": 25 }, "duration": { "OTIO_SCHEMA": "RationalTime.1", "value": 25, "rate": 25 } },
                        "media_reference": { "OTIO_SCHEMA": "ExternalReference.1", "target_url": "file://servidor/share/clip.mov" }
                      },
                      {
                        "OTIO_SCHEMA": "Clip.1",
                        "name": "c2",
                        "source_range": { "OTIO_SCHEMA": "TimeRange.1", "start_time": { "OTIO_SCHEMA": "RationalTime.1", "value": 0, "rate": 25 }, "duration": { "OTIO_SCHEMA": "RationalTime.1", "value": 25, "rate": 25 } },
                        "media_reference": { "OTIO_SCHEMA": "ExternalReference.1", "target_url": "file:///C:/{{longName}}.mov" }
                      }
                    ]
                  }
                ]
              }
            }
            """;
        var report = new InterchangeReport();
        try
        {
            TimelineDraft draft = InterchangeFiles.ReadDraft(json, InterchangeFormat.Otio, null, new InterchangeOptions(), report);
            Assert.True(draft.Project.Media.All(m => m.Path.Length == 0 || !m.Path.StartsWith(@"\\", StringComparison.Ordinal)));
        }
        catch (InterchangeException)
        {
            // Rechazo directo válido
        }
    }

    [Fact]
    public void Edl_hostile_paths_are_rejected_or_reported()
    {
        string longName = new string('x', 40_000);
        string edl = $"""
            TITLE: Hostil
            001  TOMA01   V     C     00:00:00:00 00:00:01:00 01:00:00:00 01:00:01:00
            * SOURCE FILE: \\servidor\share\clip.mov
            002  TOMA02   V     C     00:00:00:00 00:00:01:00 01:00:01:00 01:00:02:00
            * SOURCE FILE: C:\{longName}.mov
            """;
        var report = new InterchangeReport();
        try
        {
            TimelineDraft draft = InterchangeFiles.ReadDraft(edl, InterchangeFormat.Edl, null, new InterchangeOptions(), report);
            Assert.True(draft.Project.Media.All(m => m.Path.Length == 0 || !m.Path.StartsWith(@"\\", StringComparison.Ordinal)));
        }
        catch (InterchangeException)
        {
            // Rechazo directo válido
        }
    }

    // =========================================================================
    // Archivo de 300 MB (límite de 256 MB): este va SIN Skip
    // =========================================================================

    [Fact]
    public void File_exceeding_max_file_bytes_300mb_is_rejected_without_reading()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"rodaje_hostile_300mb_{Guid.NewGuid():N}.fcpxml");
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                fs.SetLength(300L * 1024 * 1024);
            }

            var options = new InterchangeOptions();
            var target = new Project { Id = "p", Name = "P" };
            var ids = new SequentialIdGenerator();

            var ex = Assert.Throws<InterchangeException>(() =>
                InterchangeFiles.Read(tempPath, target, null, options, ids));

            Assert.Contains("256 MB", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
