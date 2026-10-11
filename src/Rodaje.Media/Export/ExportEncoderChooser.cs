using System.Collections.Immutable;

namespace Rodaje.Media.Export;

/// <summary>El codificador elegido y, si no es el que se pidió, por qué.</summary>
/// <param name="Encoder">Codificador a usar.</param>
/// <param name="Requested">Códec que pidió el usuario, o <c>null</c> si se pidió la elección por defecto.</param>
/// <param name="Note">Explicación en español cuando hubo sustitución o es un último recurso; si no, <c>null</c>.</param>
public sealed record ExportEncoderChoice(ExportEncoderInfo Encoder, ExportCodec? Requested, string? Note)
{
    /// <summary>Si el códec elegido no es el pedido.</summary>
    public bool Substituted => Requested is { } r && r != Encoder.Codec;
}

/// <summary>
/// Reglas de elección: «GPU si hay, si no CPU». Con GPU gana el codificador de hardware (H.264, luego HEVC, luego
/// AV1; a igual códec, el primero del catálogo). Sin GPU gana el software en el orden del catálogo (AV1, VP9,
/// HEVC; OpenH264 solo si no hay otra cosa). Si se pide H.264 y no hay GPU, se cambia a AV1 o HEVC por CPU y la
/// elección lo dice; es una decisión pendiente de Daniel (<c>CLAUDE.md</c>, «Hacia la versión pública»).
/// </summary>
public static class ExportEncoderChooser
{
    private static readonly ExportCodec[] HardwarePreference = [ExportCodec.H264, ExportCodec.Hevc, ExportCodec.Av1, ExportCodec.Vp9];

    // Cuando se pidió H.264 y solo hay CPU.
    private static readonly ExportCodec[] H264Substitutes = [ExportCodec.Av1, ExportCodec.Hevc, ExportCodec.Vp9];

    /// <summary>La elección por defecto, o <c>null</c> si no hay ningún codificador disponible.</summary>
    public static ExportEncoderChoice? ChooseDefault(IEnumerable<ExportEncoderInfo> available)
    {
        ImmutableArray<ExportEncoderInfo> ordered = Order(available);
        ExportEncoderInfo? hardware = HardwarePreference
            .Select(codec => ordered.FirstOrDefault(e => e.IsHardware && e.Codec == codec))
            .FirstOrDefault(e => e is not null);
        if (hardware is not null)
        {
            return new ExportEncoderChoice(hardware, null, null);
        }

        ExportEncoderInfo? software = ordered.FirstOrDefault(e => !e.LowQuality);
        if (software is not null)
        {
            return new ExportEncoderChoice(software, null, null);
        }

        ExportEncoderInfo? last = ordered.FirstOrDefault();
        return last is null ? null : new ExportEncoderChoice(last, null, LastResortNote(last));
    }

    /// <summary>
    /// La elección para un códec pedido. Hardware de ese códec, si no software de ese códec; para H.264 sin GPU,
    /// AV1 o HEVC por CPU. <c>null</c> si no hay forma de cumplirlo.
    /// </summary>
    public static ExportEncoderChoice? Choose(IEnumerable<ExportEncoderInfo> available, ExportCodec requested)
    {
        ImmutableArray<ExportEncoderInfo> ordered = Order(available);
        ExportEncoderInfo? exact = ordered.FirstOrDefault(e => e.Codec == requested && e.IsHardware)
            ?? ordered.FirstOrDefault(e => e.Codec == requested && !e.LowQuality);
        if (exact is not null)
        {
            return new ExportEncoderChoice(exact, requested, null);
        }

        if (requested != ExportCodec.H264)
        {
            return null;
        }

        foreach (ExportCodec codec in H264Substitutes)
        {
            ExportEncoderInfo? substitute = ordered.FirstOrDefault(e => e.Codec == codec && !e.LowQuality);
            if (substitute is not null)
            {
                return new ExportEncoderChoice(substitute, requested,
                    $"No hay codificador H.264 por GPU; se usa {substitute.DisplayName} por CPU.");
            }
        }

        ExportEncoderInfo? openH264 = ordered.FirstOrDefault(e => e.Codec == requested);
        return openH264 is null ? null : new ExportEncoderChoice(openH264, requested, LastResortNote(openH264));
    }

    private static string LastResortNote(ExportEncoderInfo encoder) =>
        $"Solo hay {encoder.DisplayName}, de calidad limitada: es el último recurso.";

    // Orden del catálogo; lo que no esté en él (dobles de prueba) va al final, sin repetirse.
    private static ImmutableArray<ExportEncoderInfo> Order(IEnumerable<ExportEncoderInfo> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        return [.. available
            .DistinctBy(e => e.Id)
            .OrderBy(e => IndexInCatalog(e.Id))];
    }

    private static int IndexInCatalog(string id)
    {
        ImmutableArray<ExportEncoderInfo> catalog = ExportEncoderCatalog.All;
        for (int i = 0; i < catalog.Length; i++)
        {
            if (catalog[i].Id == id)
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
