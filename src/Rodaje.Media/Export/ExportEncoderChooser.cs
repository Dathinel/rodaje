namespace Rodaje.Media.Export;

/// <summary>El codificador elegido y, si no es lo que se pidió o hay algo que avisar, por qué.</summary>
/// <param name="Encoder">Codificador a usar.</param>
/// <param name="Requested">Códec que se pidió, o <c>null</c> si se pidió la elección por defecto.</param>
/// <param name="Note">Explicación en español cuando el códec cambió o cuando no es el habitual; si no, <c>null</c>.</param>
public sealed record ExportEncoderChoice(EncoderSpec Encoder, ExportCodec? Requested, string? Note)
{
    /// <summary>Si el códec elegido no es el pedido.</summary>
    public bool Substituted => Requested is { } r && r != Encoder.Codec;
}

/// <summary>
/// Reglas de elección automática: «GPU si hay, si no CPU» (D-028).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Solo entran los codificadores con <see cref="EncoderSpec.InDefaults"/>: kvazaar y OpenH264 nunca salen de aquí,
/// solo de <see cref="ChooseById"/> cuando el usuario los elige.</item>
/// <item>Con GPU gana su codificador (H.264, luego HEVC, luego AV1; a igual códec, el primero del catálogo).</item>
/// <item>Sin GPU, H.264 y HEVC van por Media Foundation de Windows (con la licencia de patentes del sistema); si tampoco
/// está (Windows N), caen a AV1 (SVT-AV1, luego libaom) o VP9 por CPU, y la elección lo dice.</item>
/// <item>El audio sigue el catálogo: el AAC de Windows antes que el de FFmpeg.</item>
/// </list>
/// </remarks>
public static class ExportEncoderChooser
{
    // Solo hay GPU para estos tres; VP9 no tiene codificador de hardware en el catálogo.
    private static readonly ExportCodec[] HardwarePreference = [ExportCodec.H264, ExportCodec.Hevc, ExportCodec.Av1];

    // Sin GPU: Media Foundation (H.264, HEVC) y luego el software libre de regalías.
    private static readonly ExportCodec[] CpuPreference = [ExportCodec.H264, ExportCodec.Hevc, ExportCodec.Av1, ExportCodec.Vp9];

    // Cuando se pidió H.264 o HEVC y no hay forma de hacerlo con licencia (ni GPU ni Media Foundation).
    private static readonly ExportCodec[] PatentFreeFallback = [ExportCodec.Av1, ExportCodec.Vp9];

    /// <summary>La elección de video por defecto, o <c>null</c> si no hay ningún codificador automático disponible.</summary>
    public static ExportEncoderChoice? ChooseDefault(IEnumerable<EncoderSpec> available)
    {
        EncoderSpec[] candidates = Candidates(available, audio: false);
        EncoderSpec? chosen = HardwarePreference
            .Select(codec => candidates.FirstOrDefault(e => e.IsHardware && e.Codec == codec))
            .FirstOrDefault(e => e is not null)
            ?? CpuPreference
                .Select(codec => candidates.FirstOrDefault(e => !e.IsHardware && e.Codec == codec))
                .FirstOrDefault(e => e is not null);
        if (chosen is null)
        {
            return null;
        }

        string? note = chosen.Codec is ExportCodec.H264 or ExportCodec.Hevc ? null : NoLicensedH26xNote(chosen);
        return new ExportEncoderChoice(chosen, null, note);
    }

    /// <summary>La elección de audio por defecto (AAC), o <c>null</c> si no hay.</summary>
    public static ExportEncoderChoice? ChooseDefaultAudio(IEnumerable<EncoderSpec> available)
    {
        EncoderSpec? chosen = Candidates(available, audio: true).FirstOrDefault(e => e.Codec == ExportCodec.Aac);
        return chosen is null ? null : new ExportEncoderChoice(chosen, null, null);
    }

    /// <summary>
    /// La elección para un códec pedido (de video o de audio): GPU de ese códec, si no software automático de ese códec;
    /// H.264 o HEVC sin GPU ni Media Foundation caen a AV1 o VP9 por CPU. <c>null</c> si no hay forma de cumplirlo.
    /// </summary>
    public static ExportEncoderChoice? Choose(IEnumerable<EncoderSpec> available, ExportCodec requested)
    {
        EncoderSpec[] candidates = Candidates(available, requested.IsAudio());
        EncoderSpec? exact = candidates.FirstOrDefault(e => e.Codec == requested && e.IsHardware)
            ?? candidates.FirstOrDefault(e => e.Codec == requested);
        if (exact is not null)
        {
            return new ExportEncoderChoice(exact, requested, null);
        }

        if (requested is not (ExportCodec.H264 or ExportCodec.Hevc))
        {
            return null;
        }

        EncoderSpec? substitute = PatentFreeFallback
            .Select(codec => candidates.FirstOrDefault(e => e.Codec == codec && e.IsHardware)
                ?? candidates.FirstOrDefault(e => e.Codec == codec))
            .FirstOrDefault(e => e is not null);
        return substitute is null ? null : new ExportEncoderChoice(substitute, requested, NoLicensedH26xNote(substitute, requested));
    }

    /// <summary>
    /// El codificador que eligió el usuario a mano, aunque no entre en los automáticos (kvazaar, OpenH264), con su
    /// <see cref="EncoderSpec.Note"/> como aviso. <c>null</c> si no está disponible.
    /// </summary>
    public static ExportEncoderChoice? ChooseById(IEnumerable<EncoderSpec> available, string id)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        EncoderSpec? chosen = available.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        return chosen is null ? null : new ExportEncoderChoice(chosen, chosen.Codec, chosen.InDefaults ? null : chosen.Note);
    }

    /// <summary>
    /// La lista con los codificadores por GPU del fabricante de la GPU principal delante de los de otras GPU (en un portátil
    /// híbrido, NVENC de la dedicada antes que Quick Sync de la integrada; en un equipo AMD con gráfica Intel, AMF antes que
    /// Quick Sync), y el resto detrás, sin cambiar el orden dentro de cada grupo.
    /// </summary>
    public static IReadOnlyList<EncoderSpec> PreferVendor(IEnumerable<EncoderSpec> encoders, EncoderVendor? preferred)
    {
        ArgumentNullException.ThrowIfNull(encoders);
        return [.. encoders.OrderBy(e => !e.IsHardware ? 2 : e.Vendor == preferred ? 0 : 1)];
    }

    private static string NoLicensedH26xNote(EncoderSpec chosen, ExportCodec? requested = null)
    {
        string what = requested switch
        {
            ExportCodec.Hevc => "HEVC",
            ExportCodec.H264 => "H.264",
            _ => "H.264 ni HEVC",
        };
        return $"No hay {what} por GPU ni por Media Foundation en este equipo; se usa {ExportEncoderCatalog.DisplayName(chosen)} por CPU.";
    }

    // Los automáticos del tipo pedido, sin repetir, en el orden del catálogo; lo que no esté en él (dobles) va al final.
    private static EncoderSpec[] Candidates(IEnumerable<EncoderSpec> available, bool audio)
    {
        ArgumentNullException.ThrowIfNull(available);
        return [.. available
            .Where(e => e.InDefaults && e.IsAudio == audio)
            .DistinctBy(e => e.Id)
            .OrderBy(e => ExportEncoderCatalog.IndexOf(e.Id))];
    }
}
