using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Services.Format;

/// <summary>
/// Cambia el formato de una secuencia con clips (medidas y cuadros por segundo) en un paso del historial y ajusta cada
/// clip de video según <see cref="ClipFit"/>. La capa se dibuja encajada en el cuadro y escalada por
/// <c>rodaje.transform.scale</c>, así que encajar no toca la escala; rellenar y mantener la corrigen, y la posición se
/// escala con el tamaño dibujado para que el clip siga en el mismo lugar relativo. Los títulos (tamaño propio) y las pistas
/// bloqueadas no cambian.
/// </summary>
public static class SequenceFormatEdit
{
    public static SequenceEditCommand Command(string sequenceId, int width, int height, Rational frameRate, ClipFit fit, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(sequenceId);
        if (!ProjectFormats.IsValidSize(width, height))
        {
            throw new ArgumentException($"{width}×{height} no es un tamaño válido: pares, de {ProjectFormats.MinSide} a {ProjectFormats.MaxSide} píxeles.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate.Num);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate.Den);
        return new SequenceEditCommand($"Formato del proyecto: {ProjectFormats.AspectName(width, height)} · {width}×{height}", sequenceId,
            ctx => Apply(ctx, width, height, frameRate, fit), ids);
    }

    private static void Apply(EditContext ctx, int width, int height, Rational frameRate, ClipFit fit)
    {
        Sequence sequence = ctx.Sequence;
        (int oldWidth, int oldHeight) = (sequence.Width, sequence.Height);
        sequence.Width = width;
        sequence.Height = height;
        sequence.FrameRate = frameRate;
        foreach (Track track in sequence.VideoTracks)
        {
            if (track.Locked)
            {
                continue;
            }

            foreach (Clip clip in track.Clips)
            {
                if (clip.Generator is null && ctx.Project.FindMedia(clip.MediaId) is { Width: > 0, Height: > 0 } media)
                {
                    Adjust(ctx, clip, Factors(media.Width, media.Height, oldWidth, oldHeight, width, height, fit));
                }
            }
        }
    }

    /// <summary>Cuánto se multiplica la escala del clip y cuánto su desplazamiento.</summary>
    internal static (double Scale, double Offset) Factors(int mediaWidth, int mediaHeight, int oldWidth, int oldHeight, int width, int height, ClipFit fit)
    {
        double fitOld = Math.Min((double)oldWidth / mediaWidth, (double)oldHeight / mediaHeight);
        double fitNew = Math.Min((double)width / mediaWidth, (double)height / mediaHeight);
        double coverNew = Math.Max((double)width / mediaWidth, (double)height / mediaHeight);
        return fit switch
        {
            ClipFit.Fill => (coverNew / fitNew, coverNew / fitOld),
            ClipFit.Keep => (fitOld / fitNew, 1),
            _ => (1, fitNew / fitOld),
        };
    }

    private static void Adjust(EditContext ctx, Clip clip, (double Scale, double Offset) factors)
    {
        const double Tolerance = 1e-9;
        if (Math.Abs(factors.Scale - 1) < Tolerance && Math.Abs(factors.Offset - 1) < Tolerance)
        {
            return;
        }

        EffectInstance? transform = clip.Effects.Find(e => e.Type == BuiltInEffects.Transform);
        if (transform is null)
        {
            if (Math.Abs(factors.Scale - 1) < Tolerance)
            {
                return;
            }

            transform = new EffectInstance { Id = ctx.Ids.NewId("effect"), Type = BuiltInEffects.Transform };
            clip.Effects.Insert(0, transform);
        }

        Multiply(transform, T.Scale, factors.Scale, 100);
        Multiply(transform, T.X, factors.Offset, double.PositiveInfinity);
        Multiply(transform, T.Y, factors.Offset, double.PositiveInfinity);
    }

    private static void Multiply(EffectInstance effect, string name, double factor, double limit)
    {
        if (Math.Abs(factor - 1) < 1e-9)
        {
            return;
        }

        if (!effect.Parameters.TryGetValue(name, out Parameter? parameter))
        {
            double fallback = T.DefaultValue(name);
            if (fallback == 0)
            {
                return;
            }

            parameter = new Parameter { Value = fallback };
            effect.Parameters[name] = parameter;
        }

        parameter.Value = Math.Clamp(parameter.Value * factor, -limit, limit);
        parameter.Keyframes = parameter.Keyframes.ConvertAll(k => k with { Value = Math.Clamp(k.Value * factor, -limit, limit) });
    }
}
