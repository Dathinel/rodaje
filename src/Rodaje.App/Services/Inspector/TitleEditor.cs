using System.Globalization;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Titles;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

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
    public Clip? Target() => InspectorTargets.First(Session, IsTitle)?.Clip;

    /// <summary>Los clips de texto seleccionados.</summary>
    public IReadOnlyList<Clip> Targets() => InspectorTargets.Selected(Session, IsTitle).ConvertAll(t => t.Clip);

    private static bool IsTitle((Track Track, Clip Clip) target) => target.Clip.Generator is not null;

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
        IReadOnlyList<Clip> targets = Targets();
        if (targets.Count == 0)
        {
            return false;
        }

        if (targets.Count == 1)
        {
            Clip clip = targets[0];
            ClipGenerator generator = clip.Generator!;
            TextStyle style = generator.Style?.Clone() ?? TitleStyles.Default();
            change(style);
            if (generator.Style is { } current && current.ContentEquals(style))
            {
                return false;
            }

            return Session.Execute(Edits.SetTitleStyle(Session.SequenceId, clip.Id, style, field));
        }

        var updates = new List<(string ClipId, TextStyle Style)>();
        foreach (Clip clip in targets)
        {
            TextStyle style = clip.Generator!.Style?.Clone() ?? TitleStyles.Default();
            change(style);
            updates.Add((clip.Id, style));
        }

        return Session.Execute(Edits.SetTitleStyles(Session.SequenceId, updates, field));
    }

    /// <summary>Se soltó un deslizador o se confirmó un campo: el próximo cambio es otro paso.</summary>
    public void EndChange() => Session.BreakMerge();

    /// <summary>
    /// Formato propio para un trozo del texto (lo elegido en el campo o en el visor): <paramref name="change"/> recibe lo que
    /// tenía cada parte. Un paso del historial.
    /// </summary>
    public bool FormatText(int start, int length, Func<TextSpan, TextSpan> change, string label = "Dar formato al texto")
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Target() is not { Generator: { } generator } clip || length <= 0 || start < 0 || start + length > generator.Text.Length)
        {
            return false;
        }

        EndTyping();
        Session.BreakMerge();
        bool done = Session.Execute(Edits.FormatTitleText(Session.SequenceId, clip.Id, start, length, change, label));
        Session.BreakMerge();
        return done;
    }

    /// <summary>El formato propio del carácter <paramref name="index"/> del título, o nulo si es el del estilo.</summary>
    public TextSpan? SpanAt(int index) => Rodaje.Core.Text.TextSpans.At(Target()?.Generator?.Spans, index);

    /// <summary>Negrita, cursiva o subrayado de un trozo: si todo el trozo ya lo tiene, lo quita.</summary>
    public bool Toggle(int start, int length, Func<TextSpan, bool> has, Func<TextSpan, bool, TextSpan> set, string label)
    {
        bool all = length > 0 && Enumerable.Range(start, length).All(i => SpanAt(i) is { } span && has(span));
        return FormatText(start, length, s => set(s, !all), label);
    }

    /// <summary>La caja del título (ajuste de línea y ajuste del tamaño): un paso del historial.</summary>
    public bool SetBox(TextFrame? box)
    {
        if (Target() is not { Generator: { } } clip)
        {
            return false;
        }

        Session.BreakMerge();
        return Session.Execute(Edits.SetTitleBox(Session.SequenceId, clip.Id, box));
    }

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
    public (string Path, string Reference)? SaveStyle(string folder, string name, bool withAnimation = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Target() is not { Generator.Style: { } current })
        {
            return null;
        }

        TextStyle style = current.Clone();
        if (!withAnimation)
        {
            style.In = null;
            style.Out = null;
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

    /// <summary>Corrige la palabra <paramref name="index"/> de un subtítulo automático (con espacios, varias; vacía, se borra).</summary>
    public bool CorrectWord(int index, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Target() is not { Generator.Words: { } words } clip || index < 0 || index >= words.Count || words[index].Text == text.Trim())
        {
            return false;
        }

        Session.BreakMerge();
        return Session.Execute(Edits.CorrectSubtitleWord(Session.SequenceId, clip.Id, index, text));
    }

    /// <summary>Divide el subtítulo donde empieza la palabra <paramref name="index"/>; el trozo de la izquierda sigue elegido.</summary>
    public bool SplitAtWord(int index) =>
        Target() is { } clip && Session.Execute(Edits.SplitSubtitleAtWord(Session.SequenceId, clip.Id, index));

    public bool JoinWithNext() =>
        Target() is { } clip && Session.Execute(Edits.JoinSubtitleWithNext(Session.SequenceId, clip.Id));

    /// <summary>Hay otro texto después del elegido en su pista, para «Unir con el siguiente».</summary>
    public bool HasNextTitle() =>
        Target() is { } clip && Session.Sequence.FindClip(clip.Id) is { Track: var track }
        && track.Clips.Any(c => c.Start >= clip.End && c.Generator is { Kind: GeneratorKinds.Text });

    /// <summary>Dónde suena la palabra en la línea de tiempo.</summary>
    public static Time WordTime(Clip clip, GeneratorWord word)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(word);
        return clip.Start + (word.Start - clip.SourceIn);
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
                "rotation" => "rotación",
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
