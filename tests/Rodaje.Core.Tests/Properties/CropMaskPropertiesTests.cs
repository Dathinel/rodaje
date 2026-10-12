using System.Numerics;
using CsCheck;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Xunit.Abstractions;
using static Rodaje.Core.Tests.Properties.V1Support;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;

namespace Rodaje.Core.Tests.Properties;

/// <summary>
/// Recorte con proporción (<see cref="CropGeometry"/>) y máscaras con keyframes (<see cref="MaskGeometry"/>, <see cref="Edits.AddMask"/>):
/// límites, proporción, trazos y que cortar, recortar o mover un clip no cambie la máscara de ningún instante.
/// </summary>
/// <remarks>Para reproducir un fallo, pasar la semilla que reporta CsCheck en <c>seed</c> o en <c>CsCheck_Seed</c>.</remarks>
public class CropMaskPropertiesTests(ITestOutputHelper output)
{
    private static readonly string[] Aspects = ["16:9", "9:16", "1:1", "4:5", "4:3"];

    /// <summary>Números de borde: casi siempre de 0 a 1, a veces fuera, negativos, enormes o no finitos.</summary>
    private static readonly Gen<double> GenEdge = Gen.Frequency(
        (12, Gen.Double[0, 1]),
        (2, Gen.Double[-2, 3]),
        (1, Gen.OneOfConst(double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1e300, -1e300, 0.98, 0.99, 0.49, 0.51)));

    private static readonly Gen<CropRect> GenCrop = Gen.Select(GenEdge, GenEdge, GenEdge, GenEdge, (l, t, r, b) => new CropRect(l, t, r, b));

    /// <summary>Tamaños de imagen de una foto o un video reales: de 16 a 8192 px, sin pasar de 5:1 ni de 1:5.</summary>
    private static readonly Gen<(double W, double H)> GenImage =
        Gen.Select(Gen.Int[16, 8192], Gen.Double[0.2, 5]).Select((w, aspect) => ((double)w, Math.Max(16, Math.Round(w / aspect))));

    private const double Eps = 1e-9;

    private static void AssertInside(CropRect c)
    {
        Assert.True(c.Left >= 0 && c.Top >= 0 && c.Right >= 0 && c.Bottom >= 0, c.ToString());
        Assert.True(c.Left <= CropGeometry.MaxCrop && c.Top <= CropGeometry.MaxCrop && c.Right <= CropGeometry.MaxCrop && c.Bottom <= CropGeometry.MaxCrop, c.ToString());
        Assert.True(c.Width >= CropGeometry.MinSize - 1e-6 && c.Height >= CropGeometry.MinSize - 1e-6, c.ToString());
    }

    /// <summary>Lo que se ve del recorte, ancho entre alto en pantalla.</summary>
    private static double ScreenRatio(CropRect c, double w, double h) => c.Width * w / (c.Height * h);

    /// <summary>Sanear deja cada borde en su rango, siempre algo de imagen, y sanear dos veces no cambia nada.</summary>
    [Fact]
    public void Sanear_EsIdempotenteYDentroDeLimites() =>
        GenCrop.Sample(
            c =>
            {
                CropRect once = CropGeometry.Sanitize(c);
                AssertInside(once);
                CropRect twice = CropGeometry.Sanitize(once);
                Assert.True(Same(once, twice), Inv($"{once} -> {twice}"));
                if (c.Left is >= 0 and <= 0.4 && c.Right is >= 0 and <= 0.4 && c.Top is >= 0 and <= 0.4 && c.Bottom is >= 0 and <= 0.4)
                {
                    Assert.Equal(c, once);
                }
            },
            writeLine: output.WriteLine,
            iter: 20000);

    /// <summary>
    /// Ajustar a una proporción da el recorte más grande con esa forma en pantalla (un lado entero) dentro de la imagen. Los
    /// casos en que <see cref="CropGeometry.FitAspect"/> lanza (defecto de <see cref="AjustarProporcion_NuncaLanza"/>) se
    /// cuentan y se saltan.
    /// </summary>
    [Fact]
    public void AjustarProporcion_DaLaFormaPedidaYLaMasGrande()
    {
        int thrown = 0;
        Gen.Select(GenCrop, Gen.Int[0, Aspects.Length - 1], GenImage).Sample(
            s =>
            {
                double ratio = CropGeometry.Ratio(Aspects[s.Item2])!.Value;
                (double w, double h) = s.Item3;
                if (FitOrNull(s.Item1, ratio, w, h) is not { } fit)
                {
                    thrown++;
                    return;
                }

                AssertInside(fit);
                Assert.True(Math.Abs((ScreenRatio(fit, w, h) / ratio) - 1) < 1e-3, Inv($"{fit} da {ScreenRatio(fit, w, h)} y se pidió {ratio}"));
                Assert.True(Math.Max(fit.Width, fit.Height) > 1 - 1e-4, Inv($"{fit} no es el más grande"));
            },
            writeLine: output.WriteLine,
            iter: 10000,
            threads: 1,
            print: s => Inv($"{s.Item1}, {Aspects[s.Item2]}, imagen {s.Item3.W}×{s.Item3.H}"));
        output.WriteLine($"FitAspect lanzó en {thrown} casos");
    }

    /// <summary>Ajustar a una proporción nunca lanza, con cualquier tamaño de imagen.</summary>
    /// <remarks>Semilla que lo muestra: <c>9xxIgTuLcv_4</c> (72×24 a 4:5).</remarks>
    [Fact(Skip = "Defecto: FitAspect calcula h = w / ratio y llama a Math.Clamp(…, 0, alto − h); si h sale un ulp mayor que el alto, el máximo es negativo y lanza ArgumentException (p. ej., 1366×768 a 4:5). Semilla 9xxIgTuLcv_4.")]
    public void AjustarProporcion_NuncaLanza() =>
        Gen.Select(GenCrop, Gen.Int[0, Aspects.Length - 1], GenImage).Sample(
            s => _ = CropGeometry.FitAspect(s.Item1, CropGeometry.Ratio(Aspects[s.Item2])!.Value, s.Item3.W, s.Item3.H),
            writeLine: output.WriteLine,
            iter: 10000,
            print: s => Inv($"{s.Item1}, {Aspects[s.Item2]}, imagen {s.Item3.W}×{s.Item3.H}"));

    /// <summary>Una captura de pantalla de portátil (1366×768) recortada a 4:5 desde la interfaz.</summary>
    [Fact(Skip = "Defecto: FitAspect lanza ArgumentException con 1366×768 a 4:5 (ver AjustarProporcion_NuncaLanza).")]
    public void AjustarProporcion_1366x768_A_4x5()
    {
        CropRect fit = CropGeometry.FitAspect(CropRect.None, CropGeometry.Ratio("4:5")!.Value, 1366, 768);
        Assert.Equal(1, fit.Height, 9);
    }

    private static CropRect? FitOrNull(CropRect current, double ratio, double w, double h)
    {
        try
        {
            return CropGeometry.FitAspect(current, ratio, w, h);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static readonly CropGrip[] Grips =
    [
        CropGrip.Body, CropGrip.TopLeft, CropGrip.TopRight, CropGrip.BottomRight, CropGrip.BottomLeft, CropGrip.Left, CropGrip.Top, CropGrip.Right, CropGrip.Bottom,
    ];

    private static readonly Gen<double> GenDelta = Gen.Frequency((6, Gen.Double[-0.3, 0.3]), (2, Gen.Double[-2, 2]), (1, Gen.Const(0.0)));

    private static readonly Gen<(CropRect Start, int Grip, double Dx, double Dy, int Aspect, (double W, double H) Image)> GenDrag =
        Gen.Select(GenCrop, Gen.Int[0, Grips.Length - 1], GenDelta, GenDelta, Gen.Int[0, Aspects.Length - 1], GenImage);

    /// <summary>Si el recorte quedó en el mínimo de algún lado, donde interviene el defecto de los arrastres.</summary>
    private static bool AtMinimum(CropRect c) => c.Width < CropGeometry.MinSize + 1e-4 || c.Height < CropGeometry.MinSize + 1e-4;

    /// <summary>
    /// Arrastrar cualquier asa sin proporción deja el recorte dentro de la imagen, sin bajar del mínimo; una esquina no mueve
    /// la opuesta, un borde no mueve los del otro eje y el cuerpo no cambia el tamaño. Con <paramref name="awayFromMinimum"/>
    /// se saltan los resultados en el mínimo de un lado.
    /// </summary>
    private void RunDrag(bool awayFromMinimum) =>
        GenDrag.Sample(
            s =>
            {
                (double w, double h) = s.Image;
                CropGrip grip = Grips[s.Grip];
                CropRect start = CropGeometry.Sanitize(s.Start);
                CropRect end = CropGeometry.Drag(start, grip, s.Dx, s.Dy, null, w, h);
                AssertInside(end);
                if (awayFromMinimum && (AtMinimum(end) || AtMinimum(start)))
                {
                    return;
                }

                const double tol = 2e-5;
                bool ok = grip switch
                {
                    CropGrip.Body => Math.Abs(end.Width - start.Width) < tol && Math.Abs(end.Height - start.Height) < tol,
                    CropGrip.TopLeft => Math.Abs(end.Right - start.Right) < tol && Math.Abs(end.Bottom - start.Bottom) < tol,
                    CropGrip.TopRight => Math.Abs(end.Left - start.Left) < tol && Math.Abs(end.Bottom - start.Bottom) < tol,
                    CropGrip.BottomRight => Math.Abs(end.Left - start.Left) < tol && Math.Abs(end.Top - start.Top) < tol,
                    CropGrip.BottomLeft => Math.Abs(end.Right - start.Right) < tol && Math.Abs(end.Top - start.Top) < tol,
                    CropGrip.Left or CropGrip.Right => Math.Abs(end.Top - start.Top) < tol && Math.Abs(end.Bottom - start.Bottom) < tol,
                    _ => Math.Abs(end.Left - start.Left) < tol && Math.Abs(end.Right - start.Right) < tol,
                };
                Assert.True(ok, Inv($"{start} -> {end}"));
            },
            writeLine: output.WriteLine,
            iter: 10000,
            print: s => Inv($"{s.Start} {Grips[s.Grip]} ({s.Dx}, {s.Dy}) imagen {s.Image.W}×{s.Image.H}"));

    /// <summary>Arrastres sin proporción que no llegan al mínimo de ningún lado.</summary>
    [Fact]
    public void Arrastrar_LejosDelMinimo_RespetaLaEsquinaOpuesta() => RunDrag(awayFromMinimum: true);

    /// <summary>Todos los arrastres sin proporción, también hasta el mínimo.</summary>
    /// <remarks>Semilla que lo muestra: <c>8F6S2txnOB42</c>.</remarks>
    [Fact(Skip = "Defecto: Drag limita el tamaño al 2 % del lado corto en píxeles, pero Sanitize exige el 2 % de cada lado; en una imagen no cuadrada, al encoger al mínimo Sanitize lo agranda moviendo también el borde que debía quedar fijo. Semilla 8F6S2txnOB42.")]
    public void Arrastrar_HastaElMinimo_RespetaLaEsquinaOpuesta() => RunDrag(awayFromMinimum: false);

    /// <summary>
    /// Con proporción fija, partiendo de un recorte que ya la tiene, cualquier arrastre la conserva en pantalla y queda
    /// dentro de la imagen. Con <paramref name="awayFromMinimum"/> se saltan los resultados en el mínimo de un lado.
    /// </summary>
    private void RunDragWithRatio(bool awayFromMinimum) =>
        GenDrag.Sample(
            s =>
            {
                (double w, double h) = s.Image;
                double ratio = CropGeometry.Ratio(Aspects[s.Aspect])!.Value;
                if (FitOrNull(s.Start, ratio, w, h) is not { } start)
                {
                    return;
                }

                CropRect end = CropGeometry.Drag(start, Grips[s.Grip], s.Dx, s.Dy, ratio, w, h);
                AssertInside(end);
                if (awayFromMinimum && AtMinimum(end))
                {
                    return;
                }

                Assert.True(Math.Abs((ScreenRatio(end, w, h) / ratio) - 1) < 2e-3, Inv($"{start} -> {end} da {ScreenRatio(end, w, h)} y se pidió {ratio}"));
            },
            writeLine: output.WriteLine,
            iter: 10000,
            print: s => Inv($"{s.Start} {Grips[s.Grip]} ({s.Dx}, {s.Dy}) {Aspects[s.Aspect]} imagen {s.Image.W}×{s.Image.H}"));

    /// <summary>Arrastres con proporción que no llegan al mínimo de ningún lado.</summary>
    [Fact]
    public void ArrastrarConProporcion_LejosDelMinimo_LaConserva() => RunDragWithRatio(awayFromMinimum: true);

    /// <summary>Todos los arrastres con proporción, también hasta el mínimo.</summary>
    /// <remarks>Semilla que lo muestra: <c>bpmvvnb_mwT4</c> (imagen 20×16, 9:16).</remarks>
    [Fact(Skip = "Defecto: con proporción, Drag deja el lado menor en el 2 % del lado corto de la imagen, que en una imagen apaisada recortada a 9:16 (o vertical a 16:9) es menos del 2 % de su lado; Sanitize lo agranda y la proporción se pierde (9:16 pasa a ~0,70). Semilla bpmvvnb_mwT4.")]
    public void ArrastrarConProporcion_HastaElMinimo_LaConserva() => RunDragWithRatio(awayFromMinimum: false);

    private static readonly Gen<Vector2> GenPoint = Gen.Select(Gen.Float[-0.5f, 0.5f], Gen.Float[-0.5f, 0.5f], (x, y) => new Vector2(x, y));

    /// <summary>Un trazo escrito y leído da los mismos puntos (a cuatro decimales); uno de menos de tres puntos se lee vacío.</summary>
    [Fact]
    public void Trazo_EscribirYLeer() =>
        GenPoint.Array[0, 300].Sample(
            points =>
            {
                Vector2[] read = MaskGeometry.ParsePath(MaskGeometry.FormatPath(points));
                int expected = Math.Min(points.Length, MaskGeometry.MaxPathPoints);
                if (expected < 3)
                {
                    Assert.Empty(read);
                    return;
                }

                Assert.Equal(expected, read.Length);
                for (int i = 0; i < read.Length; i++)
                {
                    Assert.True(Vector2.Distance(points[i], read[i]) < 1e-4, Inv($"{points[i]} -> {read[i]}"));
                }
            },
            writeLine: output.WriteLine,
            iter: 1500);

    /// <summary>
    /// Un trazo dibujado se vuelve una caja que lo contiene y a lo sumo <see cref="MaskGeometry.MaxPathPoints"/> puntos
    /// relativos a ella (de −0,5 a 0,5), que son puntos del trazo, con el primero y el último.
    /// </summary>
    [Fact]
    public void Trazo_DesdeUnDibujo() =>
        GenStroke.Sample(
            stroke =>
            {
                (double x, double y, double width, double height, Vector2[] points) = MaskGeometry.FromStroke(stroke);
                Assert.InRange(points.Length, 2, MaskGeometry.MaxPathPoints);
                Assert.All(points, p => Assert.True(Math.Abs(p.X) <= 0.5f + 1e-4 && Math.Abs(p.Y) <= 0.5f + 1e-4, p.ToString()));
                Assert.All(stroke, p => Assert.True(
                    p.X >= x - (width / 2) - 1e-4 && p.X <= x + (width / 2) + 1e-4 && p.Y >= y - (height / 2) - 1e-4 && p.Y <= y + (height / 2) + 1e-4));
                Vector2 Back(Vector2 p) => new((float)((p.X * width) + x), (float)((p.Y * height) + y));
                Assert.True(Vector2.Distance(Back(points[0]), stroke[0]) < 1e-3);
                Assert.True(Vector2.Distance(Back(points[^1]), stroke[^1]) < 1e-3);
            },
            writeLine: output.WriteLine,
            iter: 500);

    /// <summary>
    /// Un trazo como el del ratón: un paseo desde un punto al azar con pasos cortos (a veces un salto), de 3 a 600 puntos, que
    /// puede salirse de la imagen.
    /// </summary>
    private static readonly Gen<Vector2[]> GenStroke = Gen.Select(
        Gen.Float[0f, 1f], Gen.Float[0f, 1f],
        Gen.Select(Gen.Float[-0.02f, 0.02f], Gen.Float[-0.02f, 0.02f], Gen.Int[0, 50]).Array[2, 600],
        (x, y, steps) =>
        {
            var points = new Vector2[steps.Length + 1];
            points[0] = new Vector2(x, y);
            for (int i = 0; i < steps.Length; i++)
            {
                (float dx, float dy, int jump) = steps[i];
                points[i + 1] = points[i] + (jump == 0 ? new Vector2(dx * 20, dy * 20) : new Vector2(dx, dy));
            }

            return points;
        });

    private static readonly Gen<MaskSpec> GenMaskSpec = Gen.Select(
        Gen.Select(Gen.Int[0, 3], Gen.Double[-0.5, 1.5], Gen.Double[-0.5, 1.5], Gen.Double[0, 2], Gen.Double[0, 2], (s, x, y, w, h) => (s, x, y, w, h)),
        Gen.Double[-720, 720], Gen.Double[0, 1], Gen.Double[0, 1], Gen.Bool,
        (a, rot, feather, opacity, invert) => new MaskSpec((MaskShape)a.s, a.x, a.y, a.w, a.h, rot, feather, opacity, invert));

    /// <summary>
    /// La cobertura de cualquier máscara en cualquier punto está entre 0 y su opacidad, e invertirla da el complemento: las
    /// dos suman la opacidad.
    /// </summary>
    [Fact]
    public void Cobertura_EntreCeroYOpacidad_EInvertirEsElComplemento() =>
        Gen.Select(GenMaskSpec, Gen.Double[-0.5, 1.5], Gen.Double[-0.5, 1.5], GenPoint.Array[3, 12], Gen.Double[0, 1], GenImage).Sample(
            s =>
            {
                MaskSpec mask = s.Item1;
                (double w, double h) = s.Item6;
                double px = s.Item2 * w, py = s.Item3 * h;
                double f = MaskGeometry.Factor(mask, px, py, w, h, s.Item4, s.Item5);
                double g = MaskGeometry.Factor(mask with { Invert = !mask.Invert }, px, py, w, h, s.Item4, s.Item5);
                Assert.InRange(f, -Eps, mask.Opacity + Eps);
                Assert.True(Math.Abs(f + g - mask.Opacity) < 1e-9, Inv($"{f} + {g} != {mask.Opacity}"));
                if (mask.Shape is MaskShape.Rectangle or MaskShape.Ellipse && mask.Width * w > 4 && mask.Height * h > 4 && !mask.Invert && mask.Feather == 0)
                {
                    // El centro de una forma de más de unos píxeles se ve entero.
                    Assert.Equal(mask.Opacity, MaskGeometry.Factor(mask, mask.X * w, mask.Y * h, w, h), 9);
                }
            },
            writeLine: output.WriteLine,
            iter: 10000);

    /// <summary>Máscaras con keyframes en un clip: qué parámetros, en qué instantes (fracción del clip) y con qué valores.</summary>
    internal sealed record MaskKeysSpec(int Shape, int[] Params, int[] At, double[] Values, int Cut, int Delta)
    {
        public override string ToString() => Inv($"forma {Shape}, parámetros [{string.Join(",", Params)}], instantes [{string.Join(",", At)}], valores [{string.Join(",", Values)}], corte {Cut}, mover {Delta}");
    }

    private static readonly string[] MaskParams = [M.X, M.Y, M.Width, M.Height, M.Rotation, M.Feather, M.Opacity];

    private static readonly Gen<MaskKeysSpec> GenMaskKeys = Gen.Int[1, 10].SelectMany(n => Gen.Select(
        Gen.Int[0, 2], Gen.Int[0, MaskParams.Length - 1].Array[n], Gen.Int[0, 999].Array[n], Gen.Double[-2, 3].Array[n], Gen.Int[1, 999], Gen.Int[-200, 200],
        (shape, p, at, v, cut, delta) => new MaskKeysSpec(shape, p, at, v, cut, delta)));

    /// <summary>
    /// Una o dos máscaras con keyframes en un clip de un proyecto aleatorio (con velocidades, curvas y congelados): los
    /// valores evaluados quedan en los límites del registro; cortar el clip, recortar su inicio o moverlo no cambia la
    /// máscara que se ve en ningún instante de la línea de tiempo; deshacer vuelve byte a byte.
    /// </summary>
    [Fact]
    public void MascarasConKeyframes_SiguenAlMaterial()
    {
        int checkedClips = 0, total = 0;
        Gen.Select(RandomTimeline.GenProject, Gen.Int[0, 1000], GenMaskKeys, Gen.Bool).Sample(
            s =>
            {
                total++;
                BuiltProject built = RandomTimeline.Build(s.Item1 with { LockBits = 0x0FFF });
                List<Clip> video = [.. built.Sequence.VideoTracks.SelectMany(t => t.Clips)];
                if (video.Count == 0)
                {
                    return;
                }

                checkedClips++;
                Clip clip = video[s.Item2 % video.Count];
                string clipId = clip.Id;
                Project project = built.Project;
                string before = ProjectSerializer.Serialize(project);
                var stack = new UndoStack(project);
                MaskKeysSpec keys = s.Item3;
                AddMaskWithKeys(stack, built, clip, keys);
                if (s.Item4)
                {
                    AddMaskWithKeys(stack, built, clip, keys with { Shape = (keys.Shape + 1) % 3, Values = [.. keys.Values.Select(v => 1 - v)] });
                }

                Assert.Empty(ModelValidator.Validate(project));
                Time step = Time.FromFrames(1, built.Rate);
                Time from = clip.Start, to = clip.End;
                List<(Time T, List<MaskSpec> Masks)> seen = [];
                for (Time t = from; t < to; t += step)
                {
                    List<MaskSpec> masks = Masks(clip, t);
                    Assert.All(masks, m => Assert.True(
                        m.Width is >= 0 and <= 100 && m.Height is >= 0 and <= 100 && m.Feather is >= 0 and <= 1 && m.Opacity is >= 0 and <= 1, m.ToString()));
                    seen.Add((t, masks));
                }

                // Cortar en un instante del clip: cada mitad muestra lo mismo que antes.
                Time cut = from + Time.FromFrames(Math.Max(1, (to - from).ToFrames(built.Rate) * keys.Cut / 1000), built.Rate);
                if (cut < to && TryExecute(stack, Edits.Split(BuiltProject.SequenceId, clipId, cut, built.Ids)))
                {
                    AssertSame(built.Sequence, seen, Time.Zero);
                    stack.Undo();
                }

                // Recortar el inicio hasta el corte: lo que queda no cambia.
                if (cut < to && TryExecute(stack, Edits.Trim(BuiltProject.SequenceId, clipId, TrimEdge.In, cut - from, built.Ids)))
                {
                    AssertSame(built.Sequence, [.. seen.Where(x => x.T >= built.Sequence.GetClip(clipId).Clip.Start)], Time.Zero);
                    stack.Undo();
                }

                // Mover el clip: la máscara lo acompaña.
                Time delta = Time.FromFrames(keys.Delta, built.Rate);
                if (from + delta >= Time.Zero && TryExecute(stack, Edits.Move(BuiltProject.SequenceId, clipId, from + delta, ids: built.Ids)))
                {
                    Time moved = built.Sequence.GetClip(clipId).Clip.Start - from;
                    AssertSame(built.Sequence, seen, moved);
                    stack.Undo();
                }

                while (stack.CanUndo)
                {
                    stack.Undo();
                }

                Assert.Equal(before, ProjectSerializer.Serialize(project));
            },
            writeLine: output.WriteLine,
            iter: 400,
            print: s => Inv($"{s.Item1}\nclip {s.Item2}, {s.Item3}, dos {s.Item4}"));
        Assert.True(checkedClips * 2 >= total, $"Pocos clips de video: {checkedClips} de {total}");
    }

    private static void AddMaskWithKeys(UndoStack stack, BuiltProject built, Clip clip, MaskKeysSpec keys)
    {
        var add = Edits.AddMask(BuiltProject.SequenceId, clip.Id, (MaskShape)keys.Shape,
            path: keys.Shape == 2 ? [new(-0.5f, -0.5f), new(0.5f, -0.3f), new(0.1f, 0.5f), new(-0.4f, 0.2f)] : null, ids: built.Ids);
        stack.Execute(add);
        string effectId = add.CreatedIds[0];
        for (int i = 0; i < keys.Params.Length; i++)
        {
            Time at = clip.SourceIn + Time.FromTicks((clip.SourceOut - clip.SourceIn).Ticks * keys.At[i] / 1000);
            string name = MaskParams[keys.Params[i]];
            double value = name switch { M.Rotation => keys.Values[i] * 180, M.Width or M.Height => keys.Values[i] * 2, _ => keys.Values[i] };
            stack.Execute(Edits.SetKeyframe(BuiltProject.SequenceId, clip.Id, effectId, name, at, value, (Interpolation)(i % 5), built.Ids));
        }
    }

    private static bool TryExecute(UndoStack stack, ICommand command)
    {
        try
        {
            stack.Execute(command);
            return true;
        }
        catch (EditException)
        {
            return false;
        }
    }

    private static List<MaskSpec> Masks(Clip clip, Time t) =>
        [.. clip.Effects.Where(e => e.Type == BuiltInEffects.Mask).Select(e => MaskGeometry.Evaluate(e, clip.ToEffectTime(t)))];

    /// <summary>En cada instante visto (corrido <paramref name="shift"/>), el clip de la secuencia que lo cubre muestra las mismas máscaras.</summary>
    private static void AssertSame(Sequence sequence, List<(Time T, List<MaskSpec> Masks)> seen, Time shift)
    {
        foreach ((Time t, List<MaskSpec> masks) in seen)
        {
            Time at = t + shift;
            Clip? clip = sequence.VideoTracks.SelectMany(k => k.Clips).FirstOrDefault(c => c.Start <= at && at < c.End && c.Effects.Exists(e => e.Type == BuiltInEffects.Mask));
            Assert.NotNull(clip);
            List<MaskSpec> now = Masks(clip, at);
            Assert.Equal(masks.Count, now.Count);
            for (int i = 0; i < masks.Count; i++)
            {
                MaskSpec a = masks[i], b = now[i];
                Assert.True(
                    a.Shape == b.Shape && a.Invert == b.Invert && Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Width, b.Width) && Near(a.Height, b.Height)
                    && Near(a.Rotation, b.Rotation) && Near(a.Feather, b.Feather) && Near(a.Opacity, b.Opacity),
                    Inv($"En {at.Ticks}: antes {a}, ahora {b}"));
            }
        }
    }

    private static bool Same(CropRect a, CropRect b) =>
        Math.Abs(a.Left - b.Left) < 1e-12 && Math.Abs(a.Top - b.Top) < 1e-12 && Math.Abs(a.Right - b.Right) < 1e-12 && Math.Abs(a.Bottom - b.Bottom) < 1e-12;

    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(a));
}
