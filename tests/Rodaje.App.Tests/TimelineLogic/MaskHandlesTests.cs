using System.Numerics;
using Rodaje.App.Views.Player;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Tests.TimelineLogic;

/// <summary>Las asas de una máscara en el visor: la caja sobre la imagen colocada, el acierto y lo que hace cada arrastre.</summary>
public class MaskHandlesTests
{
    private static MaskBox Box(MaskSpec mask, LayerTransform? transform = null)
    {
        LayerPlacement layer = LayerLayout.Place(1920, 1080, 1920, 1080, transform ?? LayerTransform.Identity)!.Value;
        return new MaskBox("c", "m", layer, mask, []);
    }

    [Fact]
    public void LaCajaSigueALaImagenColocada()
    {
        var mask = new MaskSpec(MaskShape.Rectangle, 0.5, 0.5, 0.5, 0.5, 0, 0, 1, false);
        Vector2[] corners = MaskHandles.Corners(Box(mask));
        Assert.Equal(new Vector2(480, 270), corners[0]);
        Assert.Equal(new Vector2(1440, 810), corners[2]);

        // La capa al 50 % y movida 100 px: la caja se achica con ella.
        Vector2[] moved = MaskHandles.Corners(Box(mask, new LayerTransform(100, 0, 0.5, 0, 1)));
        Assert.Equal(new Vector2(820, 405), moved[0]);
        Assert.Equal(new Vector2(1300, 675), moved[2]);
    }

    [Fact]
    public void DeLaSecuenciaALaImagenYDeVuelta()
    {
        MaskBox box = Box(MaskSpec.Default, new LayerTransform(-40, 25, 0.75, 30, 1));
        var image = new Vector2(300, 700);
        Vector2 back = MaskHandles.ToImage(box.Layer, MaskHandles.ToSequence(box, image));
        Assert.True(Vector2.Distance(image, back) < 0.01f);
    }

    [Fact]
    public void MoverYAgrandarCambianCentroYTamano()
    {
        MaskBox box = Box(new MaskSpec(MaskShape.Ellipse, 0.5, 0.5, 0.2, 0.2, 90, 0, 1, false));
        (double x, double y) = MaskHandles.Moved(box, new Vector2(960, 540), new Vector2(1152, 432));
        Assert.Equal(0.6, x, 6);
        Assert.Equal(0.4, y, 6);

        // Girada 90°: lo que la esquina se aleja a la derecha cuenta en el alto de la caja, y lo de abajo en el ancho.
        (double width, double height) = MaskHandles.Resized(box, new Vector2(960 + 192, 540 + 108));
        Assert.Equal(2 * 108 / 1920.0, width, 6);
        Assert.Equal(192 * 2 / 1080.0, height, 6);
    }

    [Fact]
    public void AciertaEsquinasYCuerpo()
    {
        Vector2[] corners = MaskHandles.Corners(Box(MaskSpec.Default));
        Assert.Equal(MaskGrip.TopRight, MaskHandles.Hit(corners, corners[1] + new Vector2(3, 3)));
        Assert.Equal(MaskGrip.Body, MaskHandles.Hit(corners, new Vector2(960, 540)));
        Assert.Equal(MaskGrip.None, MaskHandles.Hit(corners, new Vector2(10, 10)));
    }

    [Fact]
    public void LaMascaraSeEvaluaEnElCursorConSusKeyframes()
    {
        var project = new Project { Media = [new MediaRef { Id = "m1", Width = 1920, Height = 1080, HasVideo = true }] };
        var clip = new Clip { Id = "c", MediaId = "m1", SourceIn = Time.Zero, SourceOut = Time.FromSeconds(4), Start = Time.FromSeconds(10) };
        var transform = new EffectInstance { Id = "t", Type = BuiltInEffects.Transform };
        transform.Parameters[T.Scale] = new Parameter { Value = 0.5 };
        var mask = new EffectInstance { Id = "k", Type = BuiltInEffects.Mask };
        mask.Parameters[M.X] = new Parameter { Keyframes = [new(Time.Zero, 0.2), new(Time.FromSeconds(4), 0.6)] };
        clip.Effects.AddRange([transform, mask]);
        var sequence = new Sequence { Width = 1920, Height = 1080 };

        MaskBox box = MaskHandles.Find(project, sequence, clip, mask, Time.FromSeconds(12))!.Value;
        Assert.Equal(0.4, box.Mask.X, 6);
        Assert.Equal(1920, box.ImageWidth);
        Assert.Null(MaskHandles.Find(project, sequence, clip, mask, Time.FromSeconds(9)));
    }
}
