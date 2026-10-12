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
    public void ElAsaDeGirarSobresaleArribaYGiraConLaCaja()
    {
        Vector2[] corners = TitleHandles.Corners(Box(), 1920, 1080, Half)!;
        Vector2 grip = TitleHandles.RotateGrip(corners);
        Assert.Equal((corners[0].X + corners[1].X) / 2, grip.X, 3);
        Assert.Equal(corners[0].Y - (float)TitleHandles.RotateDistance, grip.Y, 3);
        Assert.Equal(TitleGrip.Rotate, TitleHandles.Hit(corners, grip.X, grip.Y, withCorners: true));
        Assert.NotEqual(TitleGrip.Rotate, TitleHandles.Hit(corners, grip.X, grip.Y, withCorners: false));

        Vector2[] turned = TitleHandles.Corners(Box(rotation: 90), 1920, 1080, Half)!;
        Vector2 side = TitleHandles.RotateGrip(turned);
        Vector2 center = (turned[0] + turned[2]) / 2;
        Assert.True(side.X > center.X + 20, "Girada 90°, el asa queda a la derecha.");
    }

    [Fact]
    public void ElGiroSigueAlPunteroAlrededorDelCentroYConMayusVaDe15En15()
    {
        var center = new Vector2(960, 540);
        Assert.Equal(90, TitleHandles.Rotation(0, center, new Vector2(1060, 540), new Vector2(960, 640), snap: false), 6);
        Assert.Equal(10 - 90, TitleHandles.Rotation(10, center, new Vector2(1060, 540), new Vector2(960, 440), snap: false), 6);
        Assert.Equal(15, TitleHandles.Rotation(0, center, new Vector2(1060, 540), new Vector2(1060, 560), snap: true), 6);
        Assert.Equal(5, TitleHandles.Rotation(5, center, center, new Vector2(0, 0), snap: false));
    }

    [Fact]
    public void ElCuadroDeTextoCubreLaCajaYDetectaLaDireccion()
    {
        (double left, double top, double width, double height) = TitleHandles.Bounds(TitleHandles.Corners(Box(rotation: 90), 1920, 1080, Half)!);
        Assert.Equal(50, width, 3);
        Assert.Equal(200, height, 3);
        Assert.Equal(10 + 480 - 25, left, 3);
        Assert.Equal(270 - 100, top, 3);
        Assert.True(TitleHandles.IsRightToLeft("«مرحبا» world"));
        Assert.True(TitleHandles.IsRightToLeft("שלום"));
        Assert.False(TitleHandles.IsRightToLeft("123 Hola مرحبا"));
        Assert.False(TitleHandles.IsRightToLeft(""));
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

        TitleTransformDrag move = TitleTransformDrag.Begin(session, editor, id, TitleGesture.Move)!;
        Assert.Equal((0.0, 0.0, 1.0), (move.StartX, move.StartY, move.StartScale));
        for (int i = 1; i <= 20; i++)
        {
            Assert.True(move.Move(i * 10, -i * 5));
        }

        move.Complete();
        Assert.Equal(steps + 1, session.History.Count);
        Clip clip = session.Sequence.GetClip(id).Clip;
        Assert.Equal((200.0, -100.0), (editor.Read(clip, BuiltInEffects.Transform, T.X, 0).Value, editor.Read(clip, BuiltInEffects.Transform, T.Y, 0).Value));

        TitleTransformDrag grow = TitleTransformDrag.Begin(session, editor, id, TitleGesture.Scale)!;
        Assert.True(grow.Scale(1.5));
        Assert.True(grow.Scale(1.75));
        grow.Cancel();
        Assert.Equal(1, editor.Read(session.Sequence.GetClip(id).Clip, BuiltInEffects.Transform, T.Scale, 1).Value);
        Assert.Equal(steps + 1, session.History.Count);

        // Con keyframes en la posición, el arrastre crea el del cursor y no toca los otros.
        clip = session.Sequence.GetClip(id).Clip;
        Assert.True(editor.ToggleKeyframe(clip, BuiltInEffects.Transform, T.X, 0));
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(4) });
        move = TitleTransformDrag.Begin(session, editor, id, TitleGesture.Move)!;
        Assert.True(move.Move(-300, -100));
        move.Complete();
        clip = session.Sequence.GetClip(id).Clip;
        Assert.Equal(-300, editor.Read(clip, BuiltInEffects.Transform, T.X, 0).Value);
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(3) });
        Assert.Equal(200, editor.Read(clip, BuiltInEffects.Transform, T.X, 0).Value);

        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(9) });
        Assert.Null(TitleTransformDrag.Begin(session, editor, id, TitleGesture.Move));
        Assert.False(session.IsInGesture);
    });

    /// <summary>
    /// En plena entrada, con el clip a escala 2 y girado 90°, el título se ve desplazado por el delta de la animación (que
    /// gira y se escala con el clip). Mover lleva lo que se ve justo con el puntero y escalar no lo saca de su sitio.
    /// </summary>
    [Fact]
    public void EnPlenaEntradaMoverYEscalarSiguenLoQueSeVe() => UiThread.Run(async () =>
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        SequenceEditCommand insert = Edits.InsertTitle(session.SequenceId, Time.FromSeconds(2), Time.FromSeconds(5), TitleStyles.NewTitle());
        Assert.True(session.Execute(insert));
        string id = insert.CreatedIds[0];
        Assert.True(session.Execute(Edits.EnsureEffect(session.SequenceId, id, BuiltInEffects.Transform)));
        string fx = session.Sequence.GetClip(id).Clip.Effects[0].Id;
        Assert.True(session.Execute(Edits.SetParameter(session.SequenceId, id, fx, T.Scale, 2, Time.Zero)));
        Assert.True(session.Execute(Edits.SetParameter(session.SequenceId, id, fx, T.Rotation, 90, Time.Zero)));
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(2.2) });
        var editor = new ClipPropertyEditor(session);

        // A mitad de una entrada que sube desde 40 px con salida suave, el delta es de 5 px hacia abajo en la capa.
        const double Delta = 5;
        LayerTransform Shown()
        {
            Clip clip = session.Sequence.GetClip(id).Clip;
            double Read(string name, double neutral) => editor.Read(clip, BuiltInEffects.Transform, name, neutral).Value;
            (double x, double y, double s, double r, double o) = TitleMotion.Compose(
                Read(T.X, 0), Read(T.Y, 0), Read(T.Scale, 1), Read(T.Rotation, 0), 1, 0, Delta, 1, 0, 1);
            return new LayerTransform(x, y, s, r, o);
        }

        LayerTransform before = Shown();
        Assert.Equal(-10, before.X, 6);
        TitleTransformDrag grow = TitleTransformDrag.Begin(session, editor, id, TitleGesture.Scale, before)!;
        Assert.True(grow.Scale(3));
        grow.Complete();
        LayerTransform scaled = Shown();
        Assert.Equal(3, scaled.Scale, 6);
        Assert.Equal(before.X, scaled.X, 1);
        Assert.Equal(before.Y, scaled.Y, 1);

        TitleTransformDrag move = TitleTransformDrag.Begin(session, editor, id, TitleGesture.Move, scaled)!;
        Assert.True(move.Move(move.StartX + 120, move.StartY - 30));
        move.Complete();
        LayerTransform moved = Shown();
        Assert.Equal(scaled.X + 120, moved.X, 1);
        Assert.Equal(scaled.Y - 30, moved.Y, 1);

        int steps = session.History.Count;
        TitleTransformDrag turn = TitleTransformDrag.Begin(session, editor, id, TitleGesture.Rotate, moved)!;
        Assert.Equal(90, turn.StartRotation);
        Assert.True(turn.Rotate(150));
        Assert.True(turn.Rotate(135));
        turn.Complete();
        Assert.Equal(steps + 1, session.History.Count);
        LayerTransform turned = Shown();
        Assert.Equal(135, turned.RotationDegrees, 6);
        Assert.Equal(moved.X, turned.X, 1);
        Assert.Equal(moved.Y, turned.Y, 1);
    });
}
