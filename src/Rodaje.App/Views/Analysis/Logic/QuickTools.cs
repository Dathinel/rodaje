using Rodaje.Core.Model;

namespace Rodaje.App.Views.Analysis;

/// <summary>
/// Una herramienta rápida: la acción que lanza (la misma de los menús), su nombre, ícono, qué hace y su grupo (el mismo
/// submenú de Herramientas).
/// </summary>
public sealed record QuickTool(string ActionId, string Label, string Glyph, string Help, string Group);

/// <summary>Si la herramienta aplica a lo seleccionado y, si no, por qué (se dice en la ayuda y al pulsarla).</summary>
public readonly record struct QuickToolState(bool Available, string? Reason);

/// <summary>
/// Las herramientas que se usan a cada rato (silencios, ritmo, voz, ruido, estabilizar, color, subtítulos, texto), sin
/// WinUI: qué hay en la barra y cuándo aplica cada una, con las mismas reglas que sus diálogos.
/// </summary>
public static class QuickTools
{
    public const string AutoSubtitles = "tools.autoSubtitles";
    public const string TextToSpeech = "voice.textToSpeech";
    public const string AddText = "clip.addText";
    public const string RemoveBackground = "vision.removeBackground";
    public const string Reframe = "vision.reframe";
    public const string VoiceOver = "audio.voiceOver";

    /// <summary>Herramientas de análisis del menú de clip que aplican al medio seleccionado.</summary>
    public static IReadOnlyList<string> ContextMenuActionIds(bool hasAudio, bool hasVideo)
    {
        var ids = new List<string>(hasAudio && hasVideo ? 9 : 4);
        if (hasAudio)
        {
            ids.AddRange(["analysis.silences", "analysis.beats", "analysis.ducking", "analysis.noise"]);
        }

        if (hasVideo)
        {
            ids.AddRange(["analysis.stabilize", "analysis.autocolor", "analysis.whitebalance", RemoveBackground, Reframe]);
        }

        return ids;
    }

    public static IReadOnlyList<QuickTool> All { get; } =
    [
        new("analysis.silences", "Quitar silencios", "", "Corta las pausas del clip con sonido elegido.", "Audio"),
        new("analysis.beats", "Detectar ritmo", "", "Pone marcadores en los pulsos de la música elegida.", "Audio"),
        new("analysis.ducking", "Bajar música bajo la voz", "", "Baja la música elegida mientras suena la voz de otra pista.", "Audio"),
        new("analysis.noise", "Reducir ruido", "", "Limpia el ruido de fondo de la voz (módulo «Audio con IA»).", "Audio"),
        new("analysis.stabilize", "Estabilizar", "", "Quita el temblor del video elegido.", "Imagen"),
        new("analysis.autocolor", "Color automático", "", "Corrige exposición y contraste del video elegido.", "Imagen"),
        new("analysis.whitebalance", "Balance de blancos automático", "", "Neutraliza el tono del video elegido.", "Imagen"),
        new(RemoveBackground, "Quitar fondo", "\uE7A8", "Recorta a la persona del video elegido y vuelve transparente el fondo (módulo «Visión con IA»).", "Imagen"),
        new(Reframe, "Reencuadre automático", "\uE7AD", "Sigue al sujeto con un recorte 9:16, 1:1 o 4:5 que se mueve suave (módulo «Visión con IA»).", "Imagen"),
        new(AutoSubtitles, "Subtítulos automáticos", "", "Escribe los subtítulos a partir de la voz.", "Texto y voz"),
        new(TextToSpeech, "Texto a voz", "", "Lee un texto en voz alta y lo pone en una pista de audio.", "Texto y voz"),
        new(AddText, "Añadir texto", "", "Un título de 5 s en el cursor.", "Texto y voz"),
        new(VoiceOver, "Grabar voz", "", "Graba tu voz desde el cursor mientras suena el resto.", "Texto y voz"),
    ];

    /// <param name="noiseModule">Si está instalado el módulo que limpia el ruido.</param>
    /// <param name="visionModule">Si está instalado el módulo de visión (quitar fondo y reencuadre).</param>
    /// <param name="subtitleModule">Si está instalado el módulo de subtítulos.</param>
    public static QuickToolState State(string actionId, Project project, Sequence sequence, IReadOnlyList<string> selection, bool noiseModule,
        bool visionModule = false, bool subtitleModule = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(selection);
        bool HasAudio(Clip c) => c.Generator is null && c.Enabled && project.FindMedia(c.MediaId) is { HasAudio: true };
        Clip? audio = selection.Select(id => sequence.FindClip(id)?.Clip).FirstOrDefault(c => c is not null && HasAudio(c));
        bool video = selection.Any(id => sequence.FindClip(id) is { } f && f.Clip.Generator is null
            && project.FindMedia(f.Clip.MediaId) is { HasVideo: true, FrameRate: not null });
        bool stillSelected = selection.Any(id => sequence.FindClip(id) is { } f
            && project.FindMedia(f.Clip.MediaId)?.IsStillImage == true);
        string videoMissing = stillSelected ? "No aplica a imágenes fijas." : "Selecciona un clip de video.";
        return actionId switch
        {
            "analysis.silences" => audio is null ? No("Selecciona un clip con sonido.") : Yes,
            "analysis.beats" => audio is null ? No("Selecciona el clip de música.") : Yes,
            "analysis.ducking" when audio is null => No("Selecciona el clip de música."),
            "analysis.ducking" => sequence.AllTracks.Any(t => t != sequence.FindClip(audio.Id)!.Value.Track
                && t.Clips.Any(c => c.Id != audio.Id && c.Start < audio.End && c.End > audio.Start && HasAudio(c)))
                ? Yes : No("Hace falta una voz en otra pista mientras suena la música."),
            "analysis.noise" when NoiseReduction.TargetClip(sequence, selection, HasAudio) is null =>
                No("Selecciona un clip con voz en una pista de audio (o un video con su audio enlazado)."),
            "analysis.noise" when !noiseModule => No("Hace falta el módulo «Audio con IA». Instálalo desde Complementos › Módulos de IA."),
            "analysis.noise" => Yes,
            "analysis.stabilize" or "analysis.autocolor" or "analysis.whitebalance" => video ? Yes : No(videoMissing),
            RemoveBackground or Reframe when !video => No(videoMissing),
            RemoveBackground or Reframe when !visionModule => No("Hace falta el módulo «Visión con IA». Instálalo desde Complementos › Módulos de IA."),
            AutoSubtitles when Subtitles.AutoSubtitles.TargetClip(sequence, selection, HasAudio) is null =>
                No("Selecciona un clip con voz (un video con sonido o un clip de audio)."),
            AutoSubtitles when !subtitleModule => No("Hace falta el módulo «Subtítulos automáticos». Instálalo desde Complementos › Módulos de IA."),
            _ => Yes,
        };
    }

    private static QuickToolState Yes => new(true, null);

    private static QuickToolState No(string reason) => new(false, reason);
}
