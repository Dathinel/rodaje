using System.Numerics;
using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Player;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Tests.TimelineLogic;

/// <summary>Las asas del título en el visor: geometría como el render, acierto y lo que hace cada arrastre.</summary>
public class TitleHandlesTests
{
    // Una secuencia 1920×1080 dibujada a la mitad en un panel con 10 de margen a la izquierda.
    private static readonly ImageFrame Half = new(10, 0, 0.5);

    private static TitleBox Box(double x = 0, double y = 0, double scale = 1, double rotation = 0) =>
        new("t", new LayerTransform(x, y, scale, rotation, 1), 400, 100);

    [Fact]
    public void LasEsquinasSiguenLaTransformacionYElMarco()
    {
        Vector2[] corners = TitleHandles.Corners(Box(x: 100, y: -50, scale: 2), 1920, 1080, Half)!;
        // Centro (960+100, 540-50) = (1060, 490); medio tamaño 400×100 a escala 2.
        Assert.Equal(new Vector2(10 + (660 * 0.5f), 390 * 0.5f), corners[0]);
        Assert.Equal(new Vector2(10 + (1460 * 0.5f), 590 * 0.5f), corners[2]);
        Assert.Null(TitleHandles.Corners(Box(scale: 0), 1920, 1080, Half));
    }

    [Fact]
    public void AciertaEsquinasSoloSiSePidenYElCuerpoAunqueEsteGirado()
    {
        Vector2[] corners = TitleHandles.Corners(Box(), 1920, 1080, Half)!;
        Assert.Equal(TitleGrip.TopLeft, TitleHandles.Hit(corners, corners[0].X + 3, corners[0].Y - 3, withCorners: true));
        Assert.Equal(TitleGrip.BottomRight, TitleHandles.Hit(corners, corners[2].X, corners[2].Y, withCorners: true));
        Assert.Equal(TitleGrip.None, TitleHandles.Hit(corners, corners[0].X - 3, corners[0].Y - 3, withCorners: false));
        Assert.Equal(TitleGrip.Body, TitleHandles.Hit(corners, 10 + 480, 270, withCorners: false));
        Assert.Equal(TitleGrip.None, TitleHandles.Hit(corners, 10 + 480, 100, withCorners: true));

        Vector2[] turned = TitleHandles.Corners(Box(rotation: 90), 1920, 1080, Half)!;
        Assert.Equal(TitleGrip.Body, TitleHandles.Hit(turned, 10 + 480, 270 + 80, withCorners: false));
        Assert.Equal(TitleGrip.None, TitleHandles.Hit(turned, 10 + 480 + 80, 270, withCorners: false));
    }

    [Fact]
    public void EscalarEsProporcionalALaDistanciaAlCentroYAcotado()
    {
        var center = new Vector2(960, 540);
        Assert.Equal(2, TitleHandles.Scale(1, center, new Vector2(1160, 540), new Vector2(1360, 540)), 6);
        Assert.Equal(0.75, TitleHandles.Scale(1.5, center, new Vector2(960, 640), new Vector2(960, 590)), 6);
        Assert.Equal(TitleHandles.MinScale, TitleHandles.Scale(1, center, new Vector2(1160, 540), center));
        Assert.Equal(1, TitleHandles.Scale(1, center, center, new Vector2(0, 0)));
    }

    [Fact]
    public void MoverYEscalarSonUnPasoCadaUnoYConKeyframesAjustanElDelCursor() => UiThread.Run(async () =>
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        SequenceEditCommand insert = Edits.InsertTitle(session.SequenceId, Time.FromSeconds(2), Time.FromSeconds(5), TitleStyles.NewTitle());
        Assert.True(session.Execute(insert));
        string id = insert.CreatedIds[0];
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(3) });
        var editor = new ClipPropertyEditor(session);
        int steps = session.History.Count;

        TitleTransformDrag move = TitleTransformDrag.Begin(session, editor, id, scale: false)!;
        Assert.Equal((0.0, 0.0, 1.0), (move.StartX, move.StartY, move.StartScale));
        for (int i = 1; i <= 20; i++)
        {
            Assert.True(move.Move(i * 10, -i * 5));
        }

        move.Complete();
        Assert.Equal(steps + 1, session.History.Count);
        Clip clip = session.Sequence.GetClip(id).Clip;
        Assert.Equal((200.0, -100.0), (editor.Read(clip, BuiltInEffects.Transform, T.X, 0).Value, editor.Read(clip, BuiltInEffects.Transform, T.Y, 0).Value));

        TitleTransformDrag grow = TitleTransformDrag.Begin(session, editor, id, scale: true)!;
        Assert.True(grow.Scale(1.5));
        Assert.True(grow.Scale(1.75));
        grow.Cancel();
        Assert.Equal(1, editor.Read(session.Sequence.GetClip(id).Clip, BuiltInEffects.Transform, T.Scale, 1).Value);
        Assert.Equal(steps + 1, session.History.Count);

        // Con keyframes en la posición, el arrastre crea el del cursor y no toca los otros.
        clip = session.Sequence.GetClip(id).Clip;
        Assert.True(editor.ToggleKeyframe(clip, BuiltInEffects.Transform, T.X, 0));
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(4) });
        move = TitleTransformDrag.Begin(session, editor, id, scale: false)!;
        Assert.True(move.Move(-300, -100));
        move.Complete();
        clip = session.Sequence.GetClip(id).Clip;
        Assert.Equal(-300, editor.Read(clip, BuiltInEffects.Transform, T.X, 0).Value);
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(3) });
        Assert.Equal(200, editor.Read(clip, BuiltInEffects.Transform, T.X, 0).Value);

        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(9) });
        Assert.Null(TitleTransformDrag.Begin(session, editor, id, scale: false));
        Assert.False(session.IsInGesture);
    });
}
