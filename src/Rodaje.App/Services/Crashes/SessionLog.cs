using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Rodaje.App.Services.Crashes;

/// <summary>
/// Registro de la sesión: las últimas <see cref="Capacity"/> líneas en memoria, para el informe de un fallo, y una copia
/// en disco línea a línea (<see cref="MirrorTo"/>) para que un cierre que no avisa (fallo nativo, «stowed» de WinRT,
/// cierre forzado) deje rastro al siguiente arranque.
/// </summary>
public sealed partial class SessionLog : IDisposable
{
    /// <summary>Líneas que guarda el registro y que lleva el informe.</summary>
    public const int DefaultCapacity = 200;

    private readonly Lock _gate = new();
    private readonly string[] _lines;
    private readonly Func<DateTime> _clock;
    private int _next;
    private int _count;
    private StreamWriter? _mirror;

    public SessionLog(int capacity = DefaultCapacity, Func<DateTime>? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _lines = new string[capacity];
        _clock = clock ?? (() => DateTime.Now);
    }

    public int Capacity => _lines.Length;

    /// <summary>Archivo donde se copia cada línea, o <c>null</c> si no se copia.</summary>
    public string? MirrorPath { get; private set; }

    /// <summary>Agrega un mensaje con la hora; uno de varias líneas ocupa una línea por cada una.</summary>
    public void Append(string? message)
    {
        string stamp = _clock().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string[] parts = (message ?? string.Empty).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        lock (_gate)
        {
            foreach (string part in parts)
            {
                string line = stamp + " " + part;
                _lines[_next] = line;
                _next = (_next + 1) % _lines.Length;
                _count = Math.Min(_count + 1, _lines.Length);
                WriteMirror(line);
            }
        }
    }

    /// <summary>Las líneas guardadas, de la más vieja a la más nueva.</summary>
    public IReadOnlyList<string> Tail()
    {
        lock (_gate)
        {
            return TailUnlocked();
        }
    }

    /// <summary>Empieza a copiar cada línea (y las que ya hay) en <paramref name="path"/>. Si no se puede, sigue solo en memoria.</summary>
    public void MirrorTo(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (_gate)
        {
            CloseMirrorCore();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _mirror = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                MirrorPath = path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (string line in TailUnlocked())
            {
                WriteMirror(line);
            }
        }
    }

    /// <summary>Deja de copiar en disco y, con <paramref name="delete"/>, borra la copia (cierre limpio o fallo ya informado).</summary>
    public void CloseMirror(bool delete)
    {
        lock (_gate)
        {
            string? path = MirrorPath;
            CloseMirrorCore();
            if (delete && path is not null)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public void Dispose() => CloseMirror(delete: false);

    /// <summary>Las últimas <paramref name="count"/> líneas de una copia en disco (de una sesión anterior).</summary>
    public static IReadOnlyList<string> ReadTail(string path, int count = DefaultCapacity)
    {
        var tail = new Queue<string>(count);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (tail.Count == count)
            {
                tail.Dequeue();
            }

            tail.Enqueue(line);
        }

        return [.. tail];
    }

    private List<string> TailUnlocked()
    {
        var result = new List<string>(_count);
        int start = (_next - _count + _lines.Length) % _lines.Length;
        for (int i = 0; i < _count; i++)
        {
            result.Add(_lines[(start + i) % _lines.Length]);
        }

        return result;
    }

    private void WriteMirror(string line)
    {
        if (_mirror is null)
        {
            return;
        }

        try
        {
            _mirror.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            CloseMirrorCore();
        }
    }

    private void CloseMirrorCore()
    {
        try
        {
            _mirror?.Dispose();
        }
        catch (IOException)
        {
        }

        _mirror = null;
        MirrorPath = null;
    }
}

/// <summary>Lleva al <see cref="SessionLog"/> lo que el código escribe con <see cref="Trace"/> (módulos, exportación).</summary>
public sealed partial class SessionLogTraceListener : TraceListener
{
    private readonly SessionLog _log;
    private readonly StringBuilder _pending = new();

    public SessionLogTraceListener(SessionLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        Name = "Rodaje.SessionLog";
    }

    public override void Write(string? message) => _pending.Append(message);

    public override void WriteLine(string? message)
    {
        _pending.Append(message);
        _log.Append(_pending.ToString());
        _pending.Clear();
    }
}
