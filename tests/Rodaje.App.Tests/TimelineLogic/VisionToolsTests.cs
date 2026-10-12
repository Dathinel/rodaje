using System.Text.Json;
using Rodaje.App.Views.Analysis;
using Rodaje.App.Views.Vision;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Tests.TimelineLogic;

/// <summary>«Quitar fondo» y «Reencuadre» sin módulo: cuadros del clip, JSON del protocolo y el paso a la transformación.</summary>
public class VisionToolsTests
{
    [Fact]
    public void ElRecorteLlenaElCuadroVerticalYSuCentroQuedaEnMedio()
    {
        // 1920×1080 en un proyecto 1080×1920: el recorte máximo 9:16 mide 607,5×1080 de la fuente.
        TransformKey centered = VisionTools.ToTransform(new ReframeKey(1, 0.5, 0.5, 1), 1920, 1080, 1080, 1920);
        double fit = 1080 / 1920.0;
        Assert.Equal(1080 / (fit * 1080 * 9 / 16.0), centered.Scale, 4);
        Assert.Equal(0, centered.X, 6);
        Assert.Equal(Time.FromSeconds(1), centered.Time);

        // Recorte a la derecha (centro en 0,75): la capa se corre a la izquierda un cuarto de su ancho dibujado.
        TransformKey right = VisionTools.ToTransform(new ReframeKey(0, 0.75, 0.5, 1), 1920, 1080, 1080, 1920);
        double drawnWidth = 1920 * fit * right.Scale;
        Assert.Equal(-drawnWidth / 4, right.X, 1);

        // Con acercamiento 2 la escala se duplica.
        Assert.Equal(centered.Scale * 2, VisionTools.ToTransform(new ReframeKey(0, 0.5, 0.5, 2), 1920, 1080, 1080, 1920).Scale, 3);
    }

    [Fact]
    public void LosCuadrosDelClipSiguenLaTasaDelMedio()
    {
        var clip = new Clip { SourceIn = Time.FromSeconds(1), SourceOut = Time.FromSeconds(3) };
        Assert.Equal((25L, 50), VisionTools.FrameSpan(clip, new Rational(25, 1)));
    }

    [Fact]
    public void ElPedidoDeReencuadreYSuRespuestaSeLeen()
    {
        string request = VisionTools.PlanRequest(0.5625, 640, 360, [(1.5, [new DetectedBox(0.1, 0.2, 0.3, 0.4, 0.9)]), (1.7, [])], 0.5);
        using JsonDocument doc = JsonDocument.Parse(request);
        Assert.Equal(2, doc.RootElement.GetProperty("cuadros").GetArrayLength());
        Assert.Equal(0.3, doc.RootElement.GetProperty("cuadros")[0].GetProperty("cajas")[0].GetProperty("w").GetDouble());

        using JsonDocument detections = JsonDocument.Parse("""{"cuadros":[{"cajas":[{"x":0.1,"y":0.1,"w":0.2,"h":0.3,"p":0.8},{"x":0,"y":0,"w":-1,"h":1}]},{"cajas":[]}]}""");
        IReadOnlyList<IReadOnlyList<DetectedBox>> boxes = VisionTools.ReadDetections(detections.RootElement, 2);
        Assert.Single(boxes[0]);
        Assert.Empty(boxes[1]);
        Assert.Throws<ModuleProtocolException>(() => VisionTools.ReadDetections(detections.RootElement, 3));

        using JsonDocument plan = JsonDocument.Parse("""{"keyframes":[{"t":2,"x":1.4,"y":0.5,"scale":1.2},{"t":1,"x":0.3,"y":0.5,"scale":0.5}],"dentro":0.99}""");
        ReframePlan read = VisionTools.ReadPlan(plan.RootElement, 10);
        Assert.Equal([1.0, 2.0], read.Keys.Select(k => k.Seconds));
        Assert.Equal(1, read.Keys[1].X);
        Assert.Equal(1, read.Keys[0].Zoom);
        Assert.Equal(0.99, read.Inside);
    }

    [Fact]
    public void LaMascaraSeGuardaEnFfv1Gris()
    {
        IReadOnlyList<string> args = VisionTools.EncoderArguments(512, 288, new Rational(30000, 1001), "salida.mkv");
        Assert.Contains("ffv1", args);
        Assert.Equal("gray", args[args.ToList().IndexOf("-pix_fmt") + 1]);
        Assert.Contains("30000/1001", args);
    }

    [Fact]
    public void LasHerramientasDeVisionPidenVideoYModulo()
    {
        var project = new Project { Media = [new MediaRef { Id = "m", HasVideo = true, FrameRate = new Rational(25, 1), Width = 1920, Height = 1080 }] };
        var sequence = new Sequence { Id = "s", Width = 1920, Height = 1080 };
        var track = new Track { Id = "v1", Kind = TrackKind.Video };
        track.Clips.Add(new Clip { Id = "c", MediaId = "m", SourceOut = Time.FromSeconds(2) });
        sequence.VideoTracks.Add(track);

        Assert.False(QuickTools.State(QuickTools.RemoveBackground, project, sequence, [], false, true).Available);
        Assert.False(QuickTools.State(QuickTools.Reframe, project, sequence, ["c"], false, false).Available);
        Assert.True(QuickTools.State(QuickTools.Reframe, project, sequence, ["c"], false, true).Available);
    }
}
