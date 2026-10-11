using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Titles;
using Rodaje.Core.Commands;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Paquetes;
using Rodaje.Paquetes.Content;

namespace Rodaje.App.Services.Effects;

/// <summary>
/// Aplica un elemento de la biblioteca al clip seleccionado: efectos y filtros del motor se añaden a la pila, los de
/// paquete se copian (expandidos, con <c>options.source</c>), una LUT va al efecto de color, una transición al corte
/// donde termina el clip (o, si no hay corte ahí, al de su inicio) y un estilo de texto al título seleccionado (o a uno
/// nuevo en el cursor).
/// </summary>
public sealed class ElementApplier(EditorSession session, IContentLoader loader)
{
    private readonly IContentLoader _loader = loader ?? throw new ArgumentNullException(nameof(loader));

    public EditorSession Session { get; } = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>Si el elemento se puede aplicar (no dice si hay un clip seleccionado que lo acepte).</summary>
    public static bool CanApply(LibraryElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.IsAvailable && element.Kind is ItemKind.Effect or ItemKind.Filter or ItemKind.Lut or ItemKind.Transition or ItemKind.TextStyle;
    }

    /// <summary>
    /// La copia del estilo de texto del elemento: el de fábrica o el de su paquete con <c>source</c>; <c>null</c> (y un
    /// aviso) si no se pudo leer.
    /// </summary>
    public async Task<TextStyle?> LoadTextStyleAsync(LibraryElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element.IsBuiltIn)
        {
            return TitleStyles.Default();
        }

        string reference = element.Reference;
        try
        {
            if (await Task.Run(() => _loader.LoadTextStyle(reference)) is { } preset)
            {
                return TitleStyles.FromPreset(preset, reference);
            }

            Session.Notify(NotificationSeverity.Warning, $"No se pudo usar «{element.Name}»", "El paquete ya no está instalado.");
        }
        catch (Exception ex) when (ex is PackageContentException or IOException or UnauthorizedAccessException)
        {
            Session.Notify(NotificationSeverity.Warning, $"No se pudo usar «{element.Name}»", ex.Message);
        }

        return null;
    }

    /// <summary>Un título nuevo en el cursor con el estilo del elemento.</summary>
    public async Task<bool> AddTitleAsync(LibraryElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        string sequenceId = Session.SequenceId;
        return await LoadTextStyleAsync(element) is { } style && Session.SequenceId == sequenceId && Session.AddTitleAtPlayhead(style);
    }

    private async Task<bool> ApplyTextStyleAsync(LibraryElement element)
    {
        if (Session.SelectedTitle is not { } title)
        {
            return await AddTitleAsync(element);
        }

        string clipId = title.Id;
        return await LoadTextStyleAsync(element) is { } style && Session.Sequence.FindClip(clipId) is not null
            && Session.ApplyTitleStyle(clipId, style);
    }

    /// <returns><c>false</c> si no se aplicó; la sesión ya avisó por qué.</returns>
    public async Task<bool> ApplyAsync(LibraryElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!CanApply(element))
        {
            return false;
        }

        TrackKind kind = element.IsBuiltIn && EffectRegistry.Find(element.Reference) is { Target: EffectTarget.Audio } ? TrackKind.Audio : TrackKind.Video;
        if (element.Kind == ItemKind.Transition)
        {
            return ApplyTransition(element);
        }

        // Un estilo de texto cambia el título seleccionado o, si no hay, crea uno en el cursor.
        if (element.Kind == ItemKind.TextStyle)
        {
            return await ApplyTextStyleAsync(element);
        }

        if (Target(kind) is not { } clip)
        {
            Session.Notify(NotificationSeverity.Informational, $"Aplicar «{element.Name}»",
                kind == TrackKind.Audio ? "Selecciona un clip de audio en la línea de tiempo." : "Selecciona un clip de video en la línea de tiempo.");
            return false;
        }

        string clipId = clip.Id;
        string sequenceId = Session.SequenceId;
        if (element.IsBuiltIn)
        {
            return Session.Execute(Edits.AddEffect(sequenceId, clipId, element.Reference));
        }

        if (element.Kind == ItemKind.Lut)
        {
            return Session.Execute(EffectCommands.SetLut(sequenceId, clipId, null, element.Reference));
        }

        string reference = element.Reference;
        ICommand? command;
        try
        {
            command = element.Kind == ItemKind.Filter
                ? await Task.Run(() => _loader.LoadFilter(reference)) is { } filter ? EffectCommands.ApplyFilter(sequenceId, clipId, filter, reference) : null
                : await Task.Run(() => _loader.LoadEffect(reference)) is { } effect ? EffectCommands.ApplyEffect(sequenceId, clipId, effect, reference) : null;
        }
        catch (Exception ex) when (ex is PackageContentException or NotImplementedException or IOException or UnauthorizedAccessException)
        {
            Session.Notify(NotificationSeverity.Warning, $"No se pudo aplicar «{element.Name}»", ex.Message);
            return false;
        }

        if (command is null)
        {
            Session.Notify(NotificationSeverity.Warning, $"No se pudo aplicar «{element.Name}»", "El paquete ya no está instalado.");
            return false;
        }

        // Mientras se leía el archivo pudo cambiar de proyecto o borrarse el clip.
        return Session.SequenceId == sequenceId && Session.Sequence.FindClip(clipId) is not null && Session.Execute(command);
    }

    private bool ApplyTransition(LibraryElement element)
    {
        if (!TransitionDrag.TryParse(element.Reference, out TransitionDrag? drag))
        {
            return false;
        }

        foreach (string id in Session.SelectedClipIds)
        {
            if (Session.Sequence.FindClip(id) is not { } found)
            {
                continue;
            }

            if (CutFrom(found.Track, found.Clip) is { } from)
            {
                return Session.Execute(drag.CreateCommand(Session.SequenceId, from.Id));
            }
        }

        Session.Notify(NotificationSeverity.Informational, $"Aplicar «{element.Name}»",
            "Selecciona un clip pegado a otro: la transición va en el corte entre los dos. También puedes arrastrarla al corte.");
        return false;
    }

    /// <summary>El clip que sale del corte donde termina <paramref name="clip"/>, o del corte donde empieza si no hay otro.</summary>
    private static Clip? CutFrom(Track track, Clip clip)
    {
        int index = track.Clips.IndexOf(clip);
        if (index + 1 < track.Clips.Count && track.Clips[index + 1].Start == clip.End && track.TransitionFrom(clip.Id) is null)
        {
            return clip;
        }

        if (index > 0 && track.Clips[index - 1].End == clip.Start && track.TransitionFrom(track.Clips[index - 1].Id) is null)
        {
            return track.Clips[index - 1];
        }

        return null;
    }

    private Clip? Target(TrackKind kind)
    {
        Sequence sequence = Session.Sequence;
        foreach (string id in Session.SelectedClipIds)
        {
            if (sequence.FindClip(id) is { } found && found.Track.Kind == kind)
            {
                return found.Clip;
            }
        }

        if (kind == TrackKind.Audio && Target(TrackKind.Video) is { } video)
        {
            return sequence.GetLinkGroup(video.Id).Find(g => g.Track.Kind == TrackKind.Audio).Clip;
        }

        return null;
    }
}
