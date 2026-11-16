using System.Globalization;
using System.Text;

namespace Rodaje.Core.Interchange.Xml;

/// <summary>Un elemento XML leído: nombre, atributos en orden, hijos y el texto que tenga dentro (sin el de sus hijos).</summary>
internal sealed class XmlNode(string name)
{
    public string Name { get; } = name;

    public List<KeyValuePair<string, string>> Attributes { get; } = [];

    public List<XmlNode> Children { get; } = [];

    public string Text { get; set; } = "";

    public string? this[string attribute]
    {
        get
        {
            foreach (KeyValuePair<string, string> pair in Attributes)
            {
                if (pair.Key == attribute)
                {
                    return pair.Value;
                }
            }

            return null;
        }
    }

    public IEnumerable<XmlNode> Elements(string name) => Children.Where(c => c.Name == name);

    public XmlNode? Element(string name) => Children.Find(c => c.Name == name);
}

/// <summary>
/// Lector y escritor de XML mínimos para el FCPXML (D-062): <c>System.Xml</c> suma 3,9 MB al exe con AOT. Entiende
/// elementos, atributos, texto, comentarios, instrucciones de proceso, <c>CDATA</c>, las cinco entidades predefinidas y
/// las numéricas. La DTD se salta sin leerla: una entidad declarada en ella es desconocida y da error, así que no hay
/// expansión de entidades ni nada externo que resolver.
/// </summary>
internal static class XmlLite
{
    public static XmlNode Parse(string text, InterchangeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(limits);
        var reader = new Reader(text, limits);
        return reader.Document();
    }

    public static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\n': builder.Append("&#10;"); break;
                case '\r': builder.Append("&#13;"); break;
                case '\t': builder.Append("&#9;"); break;
                default:
                    if (c < 0x20)
                    {
                        // XML 1.0 no admite otros caracteres de control ni escapados: se quitan.
                        break;
                    }

                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private sealed class Reader(string text, InterchangeLimits limits)
    {
        private int _pos;
        private int _elements;

        // El XML de un FCPXML anida más que un OTIO (biblioteca, evento, proyecto, secuencia, spine…): el doble del presupuesto.
        private readonly int _maxDepth = limits.MaxDepth * 2;

        public XmlNode Document()
        {
            if (text.Length > 0 && text[0] == '﻿')
            {
                _pos = 1;
            }

            XmlNode? root = null;
            var stack = new Stack<XmlNode>();
            var content = new StringBuilder();
            while (_pos < text.Length)
            {
                if (text[_pos] != '<')
                {
                    int next = text.IndexOf('<', _pos);
                    int end = next < 0 ? text.Length : next;
                    if (stack.Count == 0)
                    {
                        if (!string.IsNullOrWhiteSpace(text.AsSpan(_pos, end - _pos).ToString()))
                        {
                            throw Error("hay texto fuera del elemento raíz");
                        }
                    }
                    else
                    {
                        AppendText(content, text.AsSpan(_pos, end - _pos));
                    }

                    _pos = end;
                    continue;
                }

                if (Starts("<!--"))
                {
                    Skip("-->");
                }
                else if (Starts("<?"))
                {
                    Skip("?>");
                }
                else if (Starts("<![CDATA["))
                {
                    if (stack.Count == 0)
                    {
                        throw Error("hay CDATA fuera del elemento raíz");
                    }

                    int start = _pos + 9;
                    int end = text.IndexOf("]]>", start, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        throw Error("un CDATA no se cierra");
                    }

                    Limit(content.Length + end - start);
                    content.Append(text, start, end - start);
                    _pos = end + 3;
                }
                else if (Starts("<!DOCTYPE"))
                {
                    SkipDoctype();
                }
                else if (Starts("</"))
                {
                    _pos += 2;
                    string name = Name();
                    Spaces();
                    Expect('>');
                    if (stack.Count == 0 || stack.Peek().Name != name)
                    {
                        throw Error($"«</{name}>» no cierra el elemento abierto");
                    }

                    XmlNode closed = stack.Pop();
                    closed.Text = content.ToString();
                    content.Clear();
                }
                else
                {
                    _pos++;
                    if (root is not null && stack.Count == 0)
                    {
                        throw Error("hay más de un elemento raíz");
                    }

                    if (++_elements > limits.MaxObjects)
                    {
                        throw new InterchangeException($"El XML tiene más de {limits.MaxObjects} elementos.");
                    }

                    if (stack.Count >= _maxDepth)
                    {
                        throw new InterchangeException($"El XML anida más de {_maxDepth} niveles.");
                    }

                    var node = new XmlNode(Name());
                    if (stack.Count > 0)
                    {
                        // El texto antes de un hijo se descarta: en el FCPXML solo cuenta el de los elementos sin hijos.
                        content.Clear();
                        stack.Peek().Children.Add(node);
                    }

                    root ??= node;
                    bool selfClosing = Attributes(node);
                    if (!selfClosing)
                    {
                        stack.Push(node);
                    }
                }
            }

            if (stack.Count > 0)
            {
                throw Error($"«<{stack.Peek().Name}>» no se cierra");
            }

            return root ?? throw Error("no hay ningún elemento");
        }

        private bool Attributes(XmlNode node)
        {
            // Con un conjunto: buscar cada atributo entre los anteriores sería cuadrático con miles de atributos.
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                bool space = Spaces();
                if (_pos >= text.Length)
                {
                    throw Error($"«<{node.Name}» no se cierra");
                }

                if (text[_pos] == '>')
                {
                    _pos++;
                    return false;
                }

                if (Starts("/>"))
                {
                    _pos += 2;
                    return true;
                }

                if (!space)
                {
                    throw Error($"falta un espacio entre los atributos de «{node.Name}»");
                }

                string name = Name();
                Spaces();
                Expect('=');
                Spaces();
                if (_pos >= text.Length || text[_pos] is not ('"' or '\''))
                {
                    throw Error($"el atributo «{name}» no va entre comillas");
                }

                char quote = text[_pos++];
                int end = text.IndexOf(quote, _pos);
                if (end < 0)
                {
                    throw Error($"el atributo «{name}» no se cierra");
                }

                if (text.AsSpan(_pos, end - _pos).Contains('<'))
                {
                    throw Error($"el atributo «{name}» tiene un «<»");
                }

                var value = new StringBuilder();
                AppendText(value, text.AsSpan(_pos, end - _pos));
                _pos = end + 1;
                if (!names.Add(name))
                {
                    throw Error($"el atributo «{name}» está dos veces");
                }

                if (++_elements > limits.MaxObjects)
                {
                    throw new InterchangeException($"El XML tiene más de {limits.MaxObjects} elementos y atributos.");
                }

                node.Attributes.Add(new(name, value.ToString()));
            }
        }

        private void AppendText(StringBuilder into, ReadOnlySpan<char> raw)
        {
            int amp;
            while ((amp = raw.IndexOf('&')) >= 0)
            {
                into.Append(raw[..amp]);
                raw = raw[(amp + 1)..];
                int semi = raw.IndexOf(';');
                if (semi <= 0 || semi > 10)
                {
                    throw Error("hay un «&» que no es una entidad");
                }

                ReadOnlySpan<char> entity = raw[..semi];
                raw = raw[(semi + 1)..];
                switch (entity)
                {
                    case "lt": into.Append('<'); break;
                    case "gt": into.Append('>'); break;
                    case "amp": into.Append('&'); break;
                    case "quot": into.Append('"'); break;
                    case "apos": into.Append('\''); break;
                    default:
                        if (entity[0] != '#'
                            || !(entity.Length > 1 && (entity[1] is 'x' or 'X')
                                ? int.TryParse(entity[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code)
                                : int.TryParse(entity[1..], NumberStyles.None, CultureInfo.InvariantCulture, out code))
                            || code is <= 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
                        {
                            throw Error($"la entidad «&{entity};» no se admite (solo las predefinidas y las numéricas)");
                        }

                        into.Append(char.ConvertFromUtf32(code));
                        break;
                }

                Limit(into.Length);
            }

            into.Append(raw);
            Limit(into.Length);
        }

        private void Limit(int length)
        {
            if (length > limits.MaxTextLength)
            {
                throw new InterchangeException($"Un texto del XML pasa de {limits.MaxTextLength} caracteres.");
            }
        }

        /// <summary>Salta <c>&lt;!DOCTYPE …&gt;</c> con su subconjunto interno entre corchetes, sin interpretarlo.</summary>
        private void SkipDoctype()
        {
            int bracket = 0;
            char? quote = null;
            for (_pos += 9; _pos < text.Length; _pos++)
            {
                char c = text[_pos];
                if (quote is not null)
                {
                    quote = c == quote ? null : quote;
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '[')
                {
                    bracket++;
                }
                else if (c == ']')
                {
                    bracket--;
                }
                else if (c == '>' && bracket <= 0)
                {
                    _pos++;
                    return;
                }
            }

            throw Error("la DTD no se cierra");
        }

        private bool Starts(string prefix) => text.AsSpan(_pos).StartsWith(prefix, StringComparison.Ordinal);

        private void Skip(string terminator)
        {
            int end = text.IndexOf(terminator, _pos + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                throw Error($"falta «{terminator}»");
            }

            _pos = end + terminator.Length;
        }

        private bool Spaces()
        {
            int start = _pos;
            while (_pos < text.Length && text[_pos] is ' ' or '\t' or '\r' or '\n')
            {
                _pos++;
            }

            return _pos > start;
        }

        private void Expect(char c)
        {
            if (_pos >= text.Length || text[_pos] != c)
            {
                throw Error($"se esperaba «{c}»");
            }

            _pos++;
        }

        private string Name()
        {
            int start = _pos;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '_' or '-' or '.' or ':'))
            {
                _pos++;
            }

            if (_pos == start || char.IsDigit(text[start]) || text[start] is '-' or '.')
            {
                throw Error("se esperaba un nombre");
            }

            if (_pos - start > 256)
            {
                throw Error("un nombre pasa de 256 caracteres");
            }

            return text[start.._pos];
        }

        private InterchangeException Error(string what)
        {
            int line = 1;
            for (int i = 0; i < Math.Min(_pos, text.Length); i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                }
            }

            return new InterchangeException($"El XML no es válido en la línea {line}: {what}.");
        }
    }
}

/// <summary>Escribe XML con sangría de dos espacios; los atributos se escapan siempre.</summary>
internal sealed class XmlLiteWriter
{
    private readonly StringBuilder _text = new();
    private readonly Stack<(string Name, bool HasChildren)> _open = new();
    private bool _tagOpen;

    public XmlLiteWriter(string? doctype = null)
    {
        _text.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        if (doctype is not null)
        {
            _text.Append("<!DOCTYPE ").Append(doctype).Append(">\n");
        }
    }

    public XmlLiteWriter Start(string name)
    {
        CloseTag(children: true);
        if (_open.Count > 0)
        {
            _open.Push((_open.Pop().Name, true));
        }

        _text.Append(' ', _open.Count * 2).Append('<').Append(name);
        _open.Push((name, false));
        _tagOpen = true;
        return this;
    }

    public XmlLiteWriter Attr(string name, string? value)
    {
        if (value is not null)
        {
            if (!_tagOpen)
            {
                throw new InvalidOperationException("Un atributo va justo después de abrir el elemento.");
            }

            _text.Append(' ').Append(name).Append("=\"").Append(XmlLite.Escape(value)).Append('"');
        }

        return this;
    }

    public XmlLiteWriter End()
    {
        (string name, bool children) = _open.Pop();
        if (_tagOpen)
        {
            _text.Append("/>\n");
            _tagOpen = false;
        }
        else
        {
            if (children)
            {
                _text.Append(' ', _open.Count * 2);
            }

            _text.Append("</").Append(name).Append(">\n");
        }

        return this;
    }

    /// <summary>Un elemento con solo texto dentro: <c>&lt;name&gt;texto&lt;/name&gt;</c>.</summary>
    public XmlLiteWriter Element(string name, string text)
    {
        Start(name);
        _text.Append('>').Append(XmlLite.Escape(text)).Append("</").Append(name).Append(">\n");
        _open.Pop();
        _tagOpen = false;
        return this;
    }

    private void CloseTag(bool children)
    {
        if (_tagOpen)
        {
            _text.Append(children ? ">\n" : ">");
            _tagOpen = false;
        }
    }

    public override string ToString()
    {
        if (_open.Count > 0)
        {
            throw new InvalidOperationException($"Queda abierto «{_open.Peek().Name}».");
        }

        return _text.ToString();
    }
}
