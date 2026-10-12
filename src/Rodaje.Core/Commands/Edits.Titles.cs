using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Commands;

public static partial class Edits
{
    /// <summary>Pone un título en la pista de video libre más alta (o en una nueva encima de todas).</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: el clip y, si se creó, la pista.</remarks>
    public static SequenceEditCommand InsertTitle(
        string sequenceId,
        Time at,
        Time duration,
        ClipGenerator generator,
        string newTrackName = "Texto",
        IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ClipGenerator copy = generator.Clone();
        return new("Añadir texto", sequenceId, ctx => TitleEdits.InsertTitle(ctx, at, duration, copy, newTrackName), ids);
    }

    /// <summary>Pone un título en una pista concreta (soltar un estilo en la línea de tiempo).</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: el clip nuevo.</remarks>
    public static SequenceEditCommand InsertGenerator(
        string sequenceId,
        string trackId,
        Time at,
        Time duration,
        ClipGenerator generator,
        IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ClipGenerator copy = generator.Clone();
        return new(
            "Añadir texto",
            sequenceId,
            ctx => (IReadOnlyList<string>)[TitleEdits.InsertGenerator(ctx, trackId, at, duration, copy)],
            ids);
    }

    /// <summary>Cambia el texto. Se funde por clip: una tanda de tecleo es un paso hasta el siguiente <c>BreakMerge</c>.</summary>
    public static SequenceEditCommand SetTitleText(string sequenceId, string clipId, string text, IIdGenerator? ids = null) =>
        new("Escribir texto", sequenceId, ctx => TitleEdits.SetText(ctx, clipId, text), ids, $"title-text:{clipId}");

    /// <summary>Formato de un trozo del texto (negrita, cursiva, subrayado, color, tamaño): un paso del historial.</summary>
    public static SequenceEditCommand FormatTitleText(string sequenceId, string clipId, int start, int length, Func<TextSpan, TextSpan> change,
        string label = "Dar formato al texto", IIdGenerator? ids = null) =>
        new(label, sequenceId, ctx => TitleEdits.FormatText(ctx, clipId, start, length, change), ids);

    /// <summary>Sustituye la copia del estilo. Con <paramref name="mergeKey"/> (un deslizador) los pasos seguidos se funden.</summary>
    public static SequenceEditCommand SetTitleStyle(
        string sequenceId,
        string clipId,
        TextStyle style,
        string? mergeKey = null,
        IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        TextStyle copy = style.Clone();
        return new(
            "Cambiar estilo del texto",
            sequenceId,
            ctx => TitleEdits.SetStyle(ctx, clipId, copy),
            ids,
            mergeKey is null ? null : $"title-style:{clipId}:{mergeKey}");
    }

    public static SequenceEditCommand SetTitleEdges(string sequenceId, string clipId, bool? animateIn, bool? animateOut, IIdGenerator? ids = null) =>
        new("Animación de los bordes", sequenceId, ctx => TitleEdits.SetEdges(ctx, clipId, animateIn, animateOut), ids);

    public static SequenceEditCommand SetTitleBox(string sequenceId, string clipId, TextFrame? box, IIdGenerator? ids = null)
    {
        TextFrame? copy = box?.Clone();
        return new("Caja del texto", sequenceId, ctx => TitleEdits.SetBox(ctx, clipId, copy), ids, $"title-box:{clipId}");
    }

    /// <summary>Pasa la entrada y la salida del estilo a keyframes de la transformación del clip.</summary>
    public static SequenceEditCommand ConvertTitleAnimation(string sequenceId, string clipId, IIdGenerator? ids = null) =>
        new("Convertir en keyframes", sequenceId, ctx => TitleEdits.ConvertAnimationToKeyframes(ctx, clipId), ids);

    public static SequenceEditCommand CorrectSubtitleWord(string sequenceId, string clipId, int index, string text, IIdGenerator? ids = null) =>
        new("Corregir palabra", sequenceId, ctx => SubtitleEdits.CorrectWord(ctx, clipId, index, text), ids);

    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: el trozo de la derecha.</remarks>
    public static SequenceEditCommand SplitSubtitleAtWord(string sequenceId, string clipId, int index, IIdGenerator? ids = null) =>
        new("Dividir subtítulo", sequenceId, ctx => (IReadOnlyList<string>)[SubtitleEdits.SplitAtWord(ctx, clipId, index)], ids);

    public static SequenceEditCommand JoinSubtitleWithNext(string sequenceId, string clipId, IIdGenerator? ids = null) =>
        new("Unir subtítulos", sequenceId, ctx => SubtitleEdits.JoinWithNext(ctx, clipId), ids);

    /// <summary>Agrega una pista de subtítulos encima de todas.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: la pista nueva.</remarks>
    public static SequenceEditCommand AddSubtitleTrack(string sequenceId, string name = "Subtítulos", IIdGenerator? ids = null) =>
        new("Añadir pista de subtítulos", sequenceId, ctx => (IReadOnlyList<string>)[TitleEdits.AddSubtitleTrack(ctx, name)], ids);

    public static SequenceEditCommand SetTrackRole(string sequenceId, string trackId, string? role, IIdGenerator? ids = null) =>
        new(role is null ? "Pista corriente" : "Pista de subtítulos", sequenceId, ctx => TitleEdits.SetTrackRole(ctx, trackId, role), ids);
}
