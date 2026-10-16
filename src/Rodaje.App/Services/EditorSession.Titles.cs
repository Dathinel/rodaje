using Rodaje.App.Services.Titles;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services;

/// <summary>Títulos y subtítulos: crearlos en el cursor, cambiarles el estilo y la pista de subtítulos.</summary>
public sealed partial class EditorSession
{
    /// <summary>Se pidió la pestaña «Texto» (al crear un título); la abre el inspector.</summary>
    public event EventHandler? TextPanelRequested;

    /// <summary>El primer título seleccionado, si hay.</summary>
    public Clip? SelectedTitle
    {
        get
        {
            Sequence sequence = Sequence;
            foreach (string id in _selection)
            {
                if (sequence.FindClip(id) is { Clip.Generator: not null } found)
                {
                    return found.Clip;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Pone un título de <see cref="TitleEdits.DefaultDuration"/> en el cursor, en la pista de video libre más alta (o en
    /// una nueva encima), lo selecciona y pide la pestaña «Texto». Un solo paso del historial.
    /// </summary>
    public bool AddTitleAtPlayhead(TextStyle? style = null) => AddTitleAt(null, EditPosition, style);

    /// <summary>
    /// Como <see cref="AddTitleAtPlayhead"/>, en <paramref name="at"/>; con <paramref name="trackId"/> (soltar un estilo en
    /// una pista libre) va en esa pista.
    /// </summary>
    public bool AddTitleAt(string? trackId, Time at, TextStyle? style = null)
    {
        VerifyAccess();
        // En una pista de subtítulos, el texto de fábrica nace con el estilo de subtítulos.
        bool subtitles = trackId is not null && Sequence.FindTrack(trackId)?.Role == TrackRoles.Subtitles;
        ClipGenerator title = subtitles && (style is null || style.ContentEquals(TitleStyles.Default()))
            ? SubtitleStyles.NewSubtitle()
            : TitleStyles.NewTitle(style);
        SequenceEditCommand command = trackId is null
            ? Edits.InsertTitle(SequenceId, at, TitleEdits.DefaultDuration, title, UniqueTrackName("Texto"), _ids)
            : Edits.InsertGenerator(SequenceId, trackId, at, TitleEdits.DefaultDuration, title, _ids);
        if (!Execute(command))
        {
            return false;
        }

        Select([command.CreatedIds[0]]);
        TextPanelRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Sustituye el estilo de un título por una copia de <paramref name="style"/> (D-029).</summary>
    public bool ApplyTitleStyle(string clipId, TextStyle style)
    {
        VerifyAccess();
        return Execute(Edits.SetTitleStyle(SequenceId, clipId, style, null, _ids));
    }

    /// <summary>Agrega una pista de video al final de las pistas de video.</summary>
    public bool AddVideoTrack()
    {
        VerifyAccess();
        return Execute(Edits.AddTrack(SequenceId, TrackKind.Video, Sequence.VideoTracks.Count, UniqueTrackName("Video"), _ids));
    }

    /// <summary>Agrega una pista de audio al final de las pistas de audio.</summary>
    public bool AddAudioTrack()
    {
        VerifyAccess();
        return Execute(Edits.AddTrack(SequenceId, TrackKind.Audio, Sequence.AudioTracks.Count, UniqueTrackName("Audio"), _ids));
    }

    /// <summary>Agrega una pista de subtítulos encima de todas.</summary>
    public bool AddSubtitleTrack()
    {
        VerifyAccess();
        return Execute(Edits.AddSubtitleTrack(SequenceId, UniqueTrackName("Subtítulos"), _ids));
    }

    /// <summary>Convierte la pista en pista de subtítulos o la vuelve corriente.</summary>
    public bool SetSubtitleRole(string trackId, bool subtitles)
    {
        VerifyAccess();
        return Execute(Edits.SetTrackRole(SequenceId, trackId, subtitles ? TrackRoles.Subtitles : null, _ids));
    }

    /// <summary>
    /// Importa subtítulos de un archivo a una pista de subtítulos nueva con el estilo de subtítulos por defecto, en un
    /// paso del historial, y deja seleccionados sus clips. Devuelve cuántos entraron (0 si no se pudo).
    /// </summary>
    public int ImportSubtitles(IReadOnlyList<SubtitleCue> cues, string trackName)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(cues);
        SequenceEditCommand command = Edits.ImportSubtitles(SequenceId, cues, SubtitleStyles.NewSubtitle(), UniqueTrackName(trackName), _ids);
        if (!Execute(command))
        {
            return 0;
        }

        Select(command.CreatedIds.Take(command.CreatedIds.Count - 1));
        return command.CreatedIds.Count - 1;
    }

    /// <summary>
    /// La pista de subtítulos que se exporta: la pedida, la del clip de texto seleccionado o la de subtítulos más alta
    /// con texto; nula si no hay ninguna con texto.
    /// </summary>
    public Track? SubtitleTrackToExport(string? trackId = null)
    {
        Sequence sequence = Sequence;
        static bool HasText(Track t) => t.Clips.Any(c => c.Generator is { Kind: GeneratorKinds.Text });
        if (trackId is not null)
        {
            return sequence.FindTrack(trackId) is { } chosen && HasText(chosen) ? chosen : null;
        }

        foreach (string id in _selection)
        {
            if (sequence.FindClip(id) is { Clip.Generator.Kind: GeneratorKinds.Text } found && found.Track.Kind == TrackKind.Video)
            {
                return found.Track;
            }
        }

        return sequence.VideoTracks.LastOrDefault(t => t.Role == TrackRoles.Subtitles && HasText(t))
            ?? sequence.VideoTracks.LastOrDefault(HasText);
    }

    /// <summary>«Texto», o «Texto 2», «Texto 3»… si ya hay una pista con ese nombre.</summary>
    private string UniqueTrackName(string name)
    {
        var used = new HashSet<string>(Sequence.AllTracks.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name))
        {
            return name;
        }

        int n = 2;
        while (used.Contains(FormattableString.Invariant($"{name} {n}")))
        {
            n++;
        }

        return FormattableString.Invariant($"{name} {n}");
    }
}