using Rodaje.Core.Editing;

namespace Rodaje.App.Views.Subtitles;

/// <summary>
/// La propuesta del diálogo «Subtítulos automáticos»: lo transcrito, en qué idioma y si se puede aplicar. Cada
/// transcripción nueva descarta la anterior al empezar, así que si falla o se cancela (por ejemplo, tras cambiar el idioma)
/// no queda a la vista ni se puede aplicar la del idioma anterior: «Aplicar» solo se activa con lo que salió de la última
/// transcripción terminada bien. Sin WinUI, para probarla sola.
/// </summary>
public sealed class TranscriptionDraft
{
    /// <summary>Los segmentos con sus palabras en la línea de tiempo; <see langword="null"/> si no hay propuesta.</summary>
    public IReadOnlyList<SpeechSegment>? Segments { get; private set; }

    /// <summary>Los subtítulos como quedarán (vacío si no hay propuesta o no se oyó voz).</summary>
    public IReadOnlyList<SubtitleCue> Cues { get; private set; } = [];

    /// <summary>El idioma pedido en la transcripción que está a la vista; vacío si no hay ninguna.</summary>
    public string Language { get; private set; } = "";

    /// <summary>Si hay una transcripción en curso.</summary>
    public bool Running { get; private set; }

    /// <summary>Si «Aplicar» pondría algo: hay subtítulos de una transcripción terminada y no hay otra en curso.</summary>
    public bool CanApply => !Running && Segments is { Count: > 0 } && Cues.Count > 0;

    /// <summary>
    /// Empieza una transcripción: descarta la propuesta anterior para que un fallo o una cancelación no deje aplicar la de
    /// otro idioma.
    /// </summary>
    public void Begin()
    {
        Running = true;
        Segments = null;
        Cues = [];
        Language = "";
    }

    /// <summary>La transcripción terminó bien: su resultado pasa a ser la propuesta.</summary>
    public void Complete(string language, IReadOnlyList<SpeechSegment> segments, IReadOnlyList<SubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(cues);
        Segments = segments;
        Cues = cues;
        Language = language;
        Running = false;
    }

    /// <summary>La transcripción falló o se canceló: no queda propuesta.</summary>
    public void Fail()
    {
        Segments = null;
        Cues = [];
        Language = "";
        Running = false;
    }

    /// <summary>
    /// Si «Transcribir de nuevo» tiene sentido con <paramref name="selected"/>: no hay otra en curso y el idioma elegido no
    /// es el de la propuesta a la vista (tras un fallo no hay propuesta, así que se puede reintentar el mismo idioma).
    /// </summary>
    public bool CanTranscribe(string selected) => !Running && !string.Equals(selected, Language, StringComparison.Ordinal);
}
