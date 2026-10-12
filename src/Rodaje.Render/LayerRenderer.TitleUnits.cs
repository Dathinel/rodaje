using System.Numerics;
using Rodaje.Core.Model;
using Rodaje.Core.Text;
using Rodaje.Playback.Composition;
using Rodaje.Render.Effects;
using Rodaje.Render.Text;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render;

/// <summary>Una imagen de título lista para colocar: su textura, lo que se usa de ella y lo que mide en píxeles de la secuencia a escala 1.</summary>
internal readonly record struct TitleImage(RenderTarget Target, int Width, int Height, double LocalWidth, double LocalHeight);

internal sealed unsafe partial class LayerRenderer
{
    /// <summary>
    /// Dibuja un título ya rasterizado. Uno por unidades se compone antes en un intermedio (cada línea, palabra o letra con
    /// su pose del cuadro, su desenfoque por mipmap y el karaoke) y desde ahí sigue el mismo camino que un bloque.
    /// </summary>
    public bool DrawTitle(LayerFrame layer, TitleTexture title, in Destination destination, ref FrameStats stats)
    {
        if (title.Units is not { } units)
        {
            return DrawTitleImage(layer, new TitleImage(title.Target, title.Width, title.Height, title.LocalWidth, title.LocalHeight), destination,
                ref stats);
        }

        _effects.BindConstants();
        if (ComposeUnits(layer, title, units, destination) is not { } composed)
        {
            return false;
        }

        try
        {
            return DrawTitleImage(layer, composed, destination, ref stats);
        }
        finally
        {
            _pool.Return(composed.Target);
        }
    }

    /// <summary>
    /// Las celdas del atlas con la pose de su unidad en un intermedio transparente, centrado en el centro del bloque y del
    /// tamaño que ocupan en este cuadro. Nulo si no se ve nada, falta el shader o no hay memoria.
    /// </summary>
    private TitleImage? ComposeUnits(LayerFrame layer, TitleTexture title, TitleUnitSet set, in Destination destination)
    {
        ID3D11PixelShader* shader = _effects.TryGetShader(EffectShader.Fx, 0);
        if (shader == null)
        {
            return null;
        }

        TextMotion motion = layer.TextMotion;
        Pose(set, motion);

        // Lo que ocupan las celdas visibles, desde el centro del bloque.
        float halfWidth = 1;
        float halfHeight = 1;
        float maxBlur = 0;
        TitleCell[] cells = set.Cells;
        for (int i = 0; i < cells.Length; i++)
        {
            ref TitleCell cell = ref cells[i];
            int u = cell.Unit;
            if (u >= 0 && !(set.Opacities[u] > 0))
            {
                continue;
            }

            Matrix3x2 m = u >= 0 ? set.Matrices[u] : Matrix3x2.Identity;
            Grow(Vector2.Transform(new Vector2(cell.Rest.Left, cell.Rest.Top), m), ref halfWidth, ref halfHeight);
            Grow(Vector2.Transform(new Vector2(cell.Rest.Right, cell.Rest.Top), m), ref halfWidth, ref halfHeight);
            Grow(Vector2.Transform(new Vector2(cell.Rest.Left, cell.Rest.Bottom), m), ref halfWidth, ref halfHeight);
            Grow(Vector2.Transform(new Vector2(cell.Rest.Right, cell.Rest.Bottom), m), ref halfWidth, ref halfHeight);
            maxBlur = u >= 0 ? Math.Max(maxBlur, set.Blurs[u]) : maxBlur;
        }

        halfWidth += maxBlur + 2;
        halfHeight += maxBlur + 2;

        // A la escala en pantalla, sin pasar la del atlas ni el tamaño de un intermedio razonable.
        float onScreen = (float)Math.Abs(TransformOf(layer).Scale) * destination.PixelScale;
        float k = Math.Clamp(Math.Min(onScreen, (float)title.Scale), 0.02f, 8f);
        k = Math.Min(k, 4096 / (2 * Math.Max(halfWidth, halfHeight)));
        int width = Math.Max(1, (int)MathF.Ceiling(2 * halfWidth * k));
        int height = Math.Max(1, (int)MathF.Ceiling(2 * halfHeight * k));
        RenderTarget? target = _pool.Rent(width, height, IntermediateFormat);
        if (target is null)
        {
            return null;
        }

        float* clear = stackalloc float[4];
        _context->ClearRenderTargetView(target.Rtv, clear);
        _quad.Bind(target.Rtv, width, height, shader, _effects.BlendState(BlendMode.Normal));
        BindInputs(null, null, title.Target, null);
        Matrix3x2 toTarget = Matrix3x2.CreateTranslation(width / (2 * k), height / (2 * k)) * Matrix3x2.CreateScale(k);
        float texelsPerPixel = (float)title.Scale;
        float maxLod = set.MipLevels - 1;
        StyleHighlight? highlight = set.Highlight;
        int word = motion.Word;
        float sweepX = float.NaN;
        if (highlight is { Mode: HighlightMode.Sweep } && word >= 0 && word < set.SpokenExtents.Length)
        {
            (float left, float right) = set.SpokenExtents[word];
            sweepX = left + ((right - left) * (float)Math.Clamp(motion.WordProgress, 0, 1));
        }

        for (int i = 0; i < cells.Length; i++)
        {
            ref TitleCell cell = ref cells[i];
            int u = cell.Unit;
            float opacity = u >= 0 ? set.Opacities[u] : 1;
            if (!(opacity > 0))
            {
                continue;
            }

            float lo = cell.Uv.X;
            float hi = cell.Uv.Z;
            if (cell.Kind is TitleCellKind.Fill or TitleCellKind.Highlight && highlight is not null && u >= 0)
            {
                // Karaoke: qué parte de esta unidad ya se dijo (1 entera, 0 nada) y, en el barrido, por dónde va.
                int spoken = set.Units[u].Spoken;
                float said = word < 0 || spoken < 0 ? 0
                    : highlight.Mode == HighlightMode.Word ? (spoken == word ? 1 : 0)
                    : spoken < word ? 1
                    : spoken > word ? 0
                    : Math.Clamp((sweepX - cell.Rest.Left) / Math.Max(1e-3f, cell.Rest.Width), 0, 1);
                float split = lo + ((hi - lo) * said);
                (lo, hi) = cell.Kind == TitleCellKind.Highlight ? (lo, split) : (split, hi);
                if (!(hi > lo))
                {
                    continue;
                }
            }
            else if (cell.Kind == TitleCellKind.Highlight)
            {
                continue;
            }

            Matrix3x2 unit = u >= 0 ? set.Matrices[u] : Matrix3x2.Identity;
            Matrix3x2 local = Matrix3x2.CreateTranslation(cell.Rest.Left, cell.Rest.Top) * unit * toTarget;
            float unitScale = new Vector2(unit.M11, unit.M12).Length();
            float drawnScale = Math.Max(1e-4f, unitScale * k);
            var placement = new LayerPlacement(local, cell.Rest.Width, cell.Rest.Height, drawnScale);

            // Desenfoque y achicado salen del mipmap: el nivel cuyo texel mide el radio (o lo que se achica).
            float blurTexels = u >= 0 ? set.Blurs[u] * texelsPerPixel : 0;
            float minify = texelsPerPixel / drawnScale;
            float lod = Math.Clamp(MathF.Log2(Math.Max(1, Math.Max(blurTexels, minify))), 0, maxLod);
            float halfTexelX = 0.5f / title.Width;
            float halfTexelY = 0.5f / title.Height;
            var uv = new QuadTexCoords(new TexCoord(cell.Uv.X, cell.Uv.Y), new TexCoord(cell.Uv.Z, cell.Uv.Y), new TexCoord(cell.Uv.X, cell.Uv.W),
                new TexCoord(cell.Uv.Z, cell.Uv.W));
            EffectConstants constants = default;
            constants.Fx0 = new Float4((float)FxOp.TitleUnit, lod, opacity, 0);
            constants.Fx1 = new Float4(cell.Uv.X + halfTexelX, cell.Uv.Y + halfTexelY, cell.Uv.Z - halfTexelX, cell.Uv.W - halfTexelY);
            constants.Fx2 = new Float4(lo, hi, 1f / (title.Width * Math.Max(1e-3f, drawnScale / texelsPerPixel)), 0);
            _effects.Set(constants);
            _quad.Draw(PlacedQuad(placement, uv, 1, width, height, 1));
        }

        UnbindInputs();
        return new TitleImage(target, width, height, width / k, height / k);
    }

    /// <summary>La matriz, la opacidad y el desenfoque de cada unidad en este cuadro: arco, y la pose de su grupo si hay animación en curso.</summary>
    private static void Pose(TitleUnitSet set, TextMotion motion)
    {
        TextAnimator? animator = motion.Phase switch
        {
            TextPhase.In => set.In,
            TextPhase.Out => set.Out,
            _ => null,
        };
        double[] delays = motion.Phase == TextPhase.In ? set.InDelays : set.OutDelays;
        int[]? groups = null;
        Vector2[]? centers = null;
        if (animator is not null)
        {
            groups = set.GroupOf[(int)animator.Unit];
            centers = set.GroupCenters[(int)animator.Unit];
            for (int g = 0; g < centers.Length && g < delays.Length; g++)
            {
                set.Poses[g] = animator.Evaluate(motion.Progress, delays[g]);
            }
        }

        TitleUnit[] units = set.Units;
        float unitScale = (float)set.UnitScale;
        for (int u = 0; u < units.Length; u++)
        {
            ref TitleUnit unit = ref units[u];
            Matrix3x2 m = unit.Angle == 0 && unit.Placed == unit.Center
                ? Matrix3x2.Identity
                : Matrix3x2.CreateTranslation(-unit.Center) * Matrix3x2.CreateRotation(unit.Angle) * Matrix3x2.CreateTranslation(unit.Placed);
            float opacity = 1;
            float blur = 0;
            if (groups is not null && centers is not null)
            {
                int g = groups[u];
                UnitPose pose = set.Poses[g];
                Vector2 c = centers[g];
                m *= Matrix3x2.CreateTranslation(-c) * Matrix3x2.CreateScale((float)pose.Scale) * Matrix3x2.CreateRotation((float)(pose.Rotation * Math.PI / 180))
                    * Matrix3x2.CreateTranslation(c + (new Vector2((float)pose.X, (float)pose.Y) * unitScale));
                opacity = (float)pose.Opacity;
                blur = (float)pose.Blur * unitScale;
            }

            set.Matrices[u] = m;
            set.Opacities[u] = opacity;
            set.Blurs[u] = blur;
        }
    }

    private static void Grow(Vector2 p, ref float halfWidth, ref float halfHeight)
    {
        halfWidth = Math.Max(halfWidth, Math.Abs(p.X));
        halfHeight = Math.Max(halfHeight, Math.Abs(p.Y));
    }
}
