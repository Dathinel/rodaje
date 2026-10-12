using Rodaje.Core.Model;

namespace Rodaje.App.Views.Analysis;

/// <summary>Una herramienta de la barra rápida: la acción que lanza (la misma de los menús), su nombre, ícono y qué hace.</summary>
public sealed record QuickTool(string ActionId, string Label, string Glyph, string Help);

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

    public static IReadOnlyList<QuickTool> All { get; } =
    [
        new("analysis.silences", "Quitar silencios", "", "Corta las pausas del clip con sonido elegido."),
        new("analysis.beats", "Detectar ritmo", "", "Pone marcadores en los pulsos de la música elegida."),
        new("analysis.ducking", "Bajar música bajo la voz", "", "Baja la música elegida mientras suena la voz de otra pista."),
        new("analysis.noise", "Reducir ruido", "", "Limpia el ruido de fondo de la voz (módulo «Audio con IA»)."),
        new("analysis.stabilize", "Estabilizar", "", "Quita el temblor del video elegido."),
        new("analysis.autocolor", "Color automático", "", "Corrige exposición y contraste del video elegido."),
        new("analysis.whitebalance", "Balance de blancos", "", "Neutraliza el tono del video elegido."),
        new(AutoSubtitles, "Subtítulos automáticos", "", "Escribe los subtítulos a partir de la voz."),
        new(TextToSpeech, "Texto a voz", "", "Lee un texto en voz alta y lo pone en una pista de audio."),
        new(AddText, "Añadir texto", "", "Un título de 5 s en el cursor."),
    ];

    /// <param name="noiseModule">Si está instalado el módulo que limpia el ruido.</param>
    /// <param name="subtitleModule">Si está instalado el módulo de subtítulos.</param>
    public static QuickToolState State(string actionId, Project project, Sequence sequence, IReadOnlyList<string> selection, bool noiseModule,
        bool subtitleModule)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(selection);
        bool HasAudio(Clip c) => c.Generator is null && c.Enabled && project.FindMedia(c.MediaId) is { HasAudio: true };
        Clip? audio = selection.Select(id => sequence.FindClip(id)?.Clip).FirstOrDefault(c => c is not null && HasAudio(c));
        bool video = selection.Any(id => sequence.FindClip(id) is { } f && f.Clip.Generator is null
            && project.FindMedia(f.Clip.MediaId) is { HasVideo: true, FrameRate: not null });
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
            "analysis.noise" when !noiseModule => No("Hace falta el módulo «Audio con IA»: instálalo desde Complementos."),
            "analysis.noise" => Yes,
            "analysis.stabilize" or "analysis.autocolor" or "analysis.whitebalance" => video ? Yes : No("Selecciona un clip de video."),
            AutoSubtitles when Subtitles.AutoSubtitles.TargetClip(sequence, selection, HasAudio) is null =>
                No("Selecciona un clip con voz (un video con sonido o un clip de audio)."),
            AutoSubtitles when !subtitleModule => No("Hace falta el módulo «Subtítulos automáticos»: instálalo desde Complementos."),
            _ => Yes,
        };
    }

    private static QuickToolState Yes => new(true, null);

    private static QuickToolState No(string reason) => new(false, reason);
}
