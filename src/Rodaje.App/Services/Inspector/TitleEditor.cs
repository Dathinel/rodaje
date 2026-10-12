using System.Globalization;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Titles;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Inspector;

/// <summary>Qué hizo «Reaplicar estilo».</summary>
public enum ReapplyResult
{
    /// <summary>El título no salió de un paquete (o es el de fábrica).</summary>
    NoSource,

    /// <summary>El paquete o su estilo ya no están instalados.</summary>
    Missing,

    /// <summary>El título ya tenía el estilo del paquete tal cual.</summary>
    Unchanged,

    Applied,
}

/// <summary>
/// La lógica de la pestaña «Texto»: escribir (una tanda de tecleo es un paso del historial), cambiar la copia del estilo
/// (un deslizador es un paso hasta soltarlo), los bordes animados y «Convertir en keyframes».
/// </summary>
public sealed class TitleEditor(EditorSession session, TimeProvider? clock = null)
{
    // La app corre con InvariantGlobalization (no hay «es-ES»), así que CurrentCulture daría «0.5»: coma decimal fija,
    // con el formato común de la app.
    private static readonly NumberFormatInfo Spanish = SpanishNumbers.Format;

    /// <summary>Una pausa más larga que esto al escribir empieza otro paso del historial.</summary>
    public static readonly TimeSpan TypingPause = TimeSpan.FromSeconds(1);

    /// <summary>Familias que se ofrecen además de las de los paquetes; se puede escribir cualquier otra del sistema.</summary>
    public static IReadOnlyList<string> CommonFonts { get; } =
    [
        "Segoe UI", "Arial", "Bahnschrift", "Calibri", "Cambria", "Candara", "Comic Sans MS", "Consolas", "Constantia",
        "Corbel", "Courier New", "Franklin Gothic Medium", "Gabriola", "Georgia", "Impact", "Ink Free", "Palatino Linotype",
        "Segoe Print", "Segoe Script", "Sitka Text", "Tahoma", "Times New Roman", "Trebuchet MS", "Verdana",
    ];

    /// <summary>Pesos con nombre para el desplegable.</summary>
    public static IReadOnlyList<(int Weight, string Name)> Weights { get; } =
    [
        (300, "Fina"), (400, "Normal"), (500, "Media"), (600, "Seminegrita"), (700, "Negrita"), (800, "Extranegrita"), (900, "Black"),
    ];

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private string? _typingClip;
    private long _lastKey;

    public EditorSession Session { get; } = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>El título que se edita: el primero seleccionado.</summary>
    public Clip? Target() => Session.SelectedTitle;

    /// <summary>Cambia el texto. Las teclas seguidas se funden en un paso; una pausa o <see cref="EndTyping"/> lo cierra.</summary>
    public bool SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Target() is not { Generator: { } generator } clip || generator.Text == text)
        {
            return false;
        }

        long now = _clock.GetTimestamp();
        if (_typingClip != clip.Id || _clock.GetElapsedTime(_lastKey, now) > TypingPause)
        {
            Session.BreakMerge();
        }

        _typingClip = clip.Id;
        _lastKey = now;
        return Session.Execute(Edits.SetTitleText(Session.SequenceId, clip.Id, text));
    }

    /// <summary>Windows escribe «\r» al pulsar Intro en un <c>TextBox</c>; el formato guarda «\n».</summary>
    public static string FromTextBox(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>Se dejó de escribir (el campo perdió el foco): lo próximo es otro paso.</summary>
    public void EndTyping()
    {
        if (_typingClip is not null)
        {
            _typingClip = null;
            Session.BreakMerge();
        }
    }

    /// <summary>
    /// Cambia la copia del estilo con <paramref name="change"/>. Los cambios seguidos del mismo <paramref name="field"/> se
    /// funden (un deslizador) hasta <see cref="EndChange"/>.
    /// </summary>
    public bool ChangeStyle(string field, Action<TextStyle> change)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(change);
        if (Target() is not { Generator: { } generator } clip)
        {
            return false;
        }

        TextStyle style = generator.Style?.Clone() ?? TitleStyles.Default();
        change(style);
        if (generator.Style is { } current && current.ContentEquals(style))
        {
            return false;
        }

        return Session.Execute(Edits.SetTitleStyle(Session.SequenceId, clip.Id, style, field));
    }

    /// <summary>Se soltó un deslizador o se confirmó un campo: el próximo cambio es otro paso.</summary>
    public void EndChange() => Session.BreakMerge();

    /// <summary>Un cambio suelto (un desplegable, una casilla): un paso propio del historial.</summary>
    public bool CommitStyle(string field, Action<TextStyle> change)
    {
        Session.BreakMerge();
        bool done = ChangeStyle(field, change);
        EndChange();
        return done;
    }

    /// <summary>Sustituye todo el estilo (elegido de la lista de estilos).</summary>
    public bool ApplyStyle(TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return Target() is { } clip && Session.ApplyTitleStyle(clip.Id, style);
    }

    /// <summary>
    /// «Guardar como estilo»: la copia del estilo del título va a los complementos propios y el título pasa a decir que
    /// salió de ahí (un paso del historial). Nulo si no hay título.
    /// </summary>
    public (string Path, string Reference)? SaveStyle(string folder, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Target() is not { Generator.Style: { } style })
        {
            return null;
        }

        (string Path, string Reference) saved = TitleStyleFile.Save(folder, name, style);
        CommitStyle("source", s =>
        {
            s.Source = saved.Reference;
            s.Name = name.Trim();
        });
        return saved;
    }

    /// <summary>
    /// «Reaplicar estilo»: vuelve a copiar el estilo del paquete de <see cref="TextStyle.Source"/> sobre el título, con
    /// lo que el paquete tenga hoy (D-029). Los errores de lectura del paquete pasan a quien llama.
    /// </summary>
    public ReapplyResult ReapplyStyle(IContentLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        if (Target() is not { Generator.Style.Source: { } source } clip || source == TitleStyles.BuiltInReference)
        {
            return ReapplyResult.NoSource;
        }

        if (loader.LoadTextStyle(source) is not { } preset)
        {
            return ReapplyResult.Missing;
        }

        TextStyle fresh = TitleStyles.FromPreset(preset, source);
        if (clip.Generator!.Style!.ContentEquals(fresh))
        {
            return ReapplyResult.Unchanged;
        }

        return Session.ApplyTitleStyle(clip.Id, fresh) ? ReapplyResult.Applied : ReapplyResult.Unchanged;
    }

    public bool SetEdges(bool? animateIn, bool? animateOut) =>
        Target() is { } clip && Session.Execute(Edits.SetTitleEdges(Session.SequenceId, clip.Id, animateIn, animateOut));

    public bool ConvertToKeyframes() =>
        Target() is { } clip && Session.Execute(Edits.ConvertTitleAnimation(Session.SequenceId, clip.Id));

    /// <summary>«Entrada 0,4 s · opacidad y posición», o null si no hay.</summary>
    public static string? DescribeAnimation(StyleAnimation? animation, bool entry)
    {
        if (animation is null)
        {
            return null;
        }

        string seconds = animation.DurationSeconds.ToString("0.##", Spanish);
        var parts = new List<string>();
        foreach (string name in animation.Curves.Keys)
        {
            string part = name switch
            {
                "x" or "y" => "posición",
                "scale" => "tamaño",
                "rotation" => "giro",
                "opacity" => "opacidad",
                _ => name,
            };
            if (!parts.Contains(part))
            {
                parts.Add(part);
            }
        }

        string what = parts.Count == 0 ? "" : " · " + string.Join(", ", parts);
        return $"{(entry ? "Entrada" : "Salida")} de {seconds} s{what}";
    }

    /// <summary>El nombre de un peso, o su número si no tiene.</summary>
    public static string WeightName(int weight) =>
        Weights.FirstOrDefault(w => w.Weight == weight).Name ?? weight.ToString(Spanish);
}
