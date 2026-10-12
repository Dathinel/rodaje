using System.Numerics;
using Rodaje.Core.Model;
using Rodaje.Core.Text;

namespace Rodaje.Render.Text;

/// <summary>Qué lleva una celda del atlas de un título por unidades; se dibujan en este orden, cada tipo para todas las unidades.</summary>
internal enum TitleCellKind : byte
{
    Background,
    Shadow,
    Stroke,
    Fill,
    Highlight,
}

/// <summary>Una celda del atlas: su rectángulo en el título en reposo (píxeles de la secuencia, desde el centro del bloque) y en el atlas.</summary>
internal struct TitleCell
{
    /// <summary>Unidad a la que pertenece; −1 el fondo del bloque.</summary>
    public int Unit;
    public TitleCellKind Kind;
    public TitleRasterizer.Rect Rest;

    /// <summary>Rectángulo útil en el atlas, en uv (sin el margen de separación).</summary>
    public Vector4 Uv;
    public int AtlasX;
    public int AtlasY;
    public int AtlasWidth;
    public int AtlasHeight;
}

/// <summary>Una unidad (línea, palabra o letra) del título: dónde está en el texto y en reposo, y a qué grupos pertenece.</summary>
internal struct TitleUnit
{
    public int Start;
    public int End;

    /// <summary>Centro en reposo antes del arco, desde el centro del bloque.</summary>
    public Vector2 Center;

    /// <summary>Centro con el arco aplicado (igual a <see cref="Center"/> en línea recta).</summary>
    public Vector2 Placed;

    /// <summary>Giro del arco en radianes (sentido horario).</summary>
    public float Angle;
    public int Word;
    public int Line;

    /// <summary>Palabra dicha (karaoke) que la contiene, o −1.</summary>
    public int Spoken;

    /// <summary>Lo que ocupa en horizontal en reposo (para el barrido del karaoke).</summary>
    public float InkLeft;
    public float InkRight;
    public float Top;
    public float Bottom;
}

/// <summary>
/// Un título rasterizado por unidades: el atlas (en <see cref="TitleTexture"/>) con sus celdas y lo que hace falta para
/// animar cada unidad en cada cuadro. Los arreglos de trabajo son del hilo del compositor: se reutilizan sin reservar.
/// </summary>
public sealed class TitleUnitSet
{
    internal TitleUnitSet(TitleCell[] cells, TitleUnit[] units, TextUnit granularity, StyleAnimation? @in, StyleAnimation? @out, double unitScale,
        StyleHighlight? highlight, int mipLevels)
    {
        Cells = cells;
        Units = units;
        Granularity = granularity;
        UnitScale = unitScale;
        Highlight = highlight;
        MipLevels = mipLevels;
        GroupOf = new int[4][];
        GroupCenters = new Vector2[4][];
        for (int kind = 0; kind <= (int)granularity; kind++)
        {
            int[] groups = new int[units.Length];
            var ids = new Dictionary<int, int>();
            for (int u = 0; u < units.Length; u++)
            {
                int key = (TextUnit)kind switch
                {
                    TextUnit.Line => units[u].Line,
                    TextUnit.Word => units[u].Word,
                    TextUnit.Letter => u,
                    _ => 0,
                };
                if (!ids.TryGetValue(key, out int id))
                {
                    id = ids.Count;
                    ids[key] = id;
                }

                groups[u] = id;
            }

            var sums = new Vector2[ids.Count];
            int[] counts = new int[ids.Count];
            for (int u = 0; u < units.Length; u++)
            {
                sums[groups[u]] += units[u].Placed;
                counts[groups[u]]++;
            }

            for (int g = 0; g < sums.Length; g++)
            {
                sums[g] /= Math.Max(1, counts[g]);
            }

            GroupOf[kind] = groups;
            GroupCenters[kind] = sums;
        }

        In = Animator(@in, out InDelays);
        Out = Animator(@out, out OutDelays);
        Poses = new UnitPose[units.Length + 1];
        Matrices = new Matrix3x2[units.Length];
        Opacities = new float[units.Length];
        Blurs = new float[units.Length];
        int spoken = units.Length == 0 ? 0 : units.Max(u => u.Spoken) + 1;
        SpokenExtents = new (float Left, float Right)[spoken];
        for (int w = 0; w < spoken; w++)
        {
            SpokenExtents[w] = (float.MaxValue, float.MinValue);
        }

        foreach (TitleUnit unit in units)
        {
            if (unit.Spoken >= 0)
            {
                (float left, float right) = SpokenExtents[unit.Spoken];
                SpokenExtents[unit.Spoken] = (Math.Min(left, unit.InkLeft), Math.Max(right, unit.InkRight));
            }
        }
    }

    public int UnitCount => Units.Length;

    public int CellCount => Cells.Length;

    /// <summary>La unidad más fina que se rasterizó por separado.</summary>
    public TextUnit Granularity { get; }

    internal TitleCell[] Cells { get; }

    internal TitleUnit[] Units { get; }

    /// <summary>Grupo de cada unidad para cada tipo de unidad animada (índice: <see cref="TextUnit"/>).</summary>
    internal int[][] GroupOf { get; }

    internal Vector2[][] GroupCenters { get; }

    internal TextAnimator? In { get; }

    internal TextAnimator? Out { get; }

    internal double[] InDelays;

    internal double[] OutDelays;

    internal UnitPose[] Poses { get; }

    internal Matrix3x2[] Matrices { get; }

    internal float[] Opacities { get; }

    internal float[] Blurs { get; }

    internal (float Left, float Right)[] SpokenExtents { get; }

    /// <summary>Píxeles de la secuencia por píxel de la referencia del estilo.</summary>
    internal double UnitScale { get; }

    internal StyleHighlight? Highlight { get; }

    internal int MipLevels { get; }

    private TextAnimator? Animator(StyleAnimation? animation, out double[] delays)
    {
        TextAnimator? animator = TextAnimator.From(animation);
        int groups = animator is null || (int)animator.Unit > (int)Granularity ? 0 : GroupCenters[(int)animator.Unit].Length;
        delays = new double[groups];
        if (animator is not null)
        {
            TextAnimator.Delays(animator.Order, delays);
        }

        return animator is not null && (int)animator.Unit <= (int)Granularity ? animator : null;
    }
}
