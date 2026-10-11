using Rodaje.Core.Timing;

namespace Rodaje.Engine.Timeline.Retime;

/// <summary>
/// Lee un clip de audio con velocidad: para cada muestra de la línea de tiempo toma del origen lo que manda su
/// <see cref="ClipTimeMap"/>, con <see cref="Wsola"/> si mantiene el tono y con
/// <see cref="VarispeedResampler"/> si no. Vive mientras su decodificador sirve al mismo clip con la misma velocidad.
/// </summary>
/// <remarks>
/// La reversa (fase 5c.3) ya pasa por aquí: <see cref="SampleMap"/> da la posición en el sentido de reproducción y
/// <see cref="SourceWindow"/> lee hacia atrás por bloques; falta medirla.
/// </remarks>
internal sealed class RetimedReader
{
    private readonly SourceWindow _source;
    private readonly SampleMap _map;
    private readonly Wsola? _wsola;
    private readonly VarispeedResampler? _varispeed;

    public RetimedReader(AudioRetime retime, IPcmSource pcm, long decoderNext)
    {
        ArgumentNullException.ThrowIfNull(retime);
        Retime = retime;
        _map = new SampleMap(retime.Course);
        _source = new SourceWindow(pcm, decoderNext);
        if (retime.PreservePitch)
        {
            _wsola = new Wsola(_map, _source, _map.MaxRate());
        }
        else
        {
            _varispeed = new VarispeedResampler(_map, _source);
        }
    }

    public AudioRetime Retime { get; private set; }

    /// <summary>
    /// Si puede seguir con <paramref name="other"/> sin rehacer nada: el mismo clip, o uno que muestra el mismo origen en
    /// cada instante (los trozos de un clip dividido, con velocidad constante o con curva, o el mismo tras mover otra
    /// cosa). Así un corte o una edición no reinician la cadena de WSOLA y no suenan.
    /// </summary>
    public bool Serves(AudioRetime other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other == Retime)
        {
            return true;
        }

        ClipTimeMap mine = Retime.Course;
        ClipTimeMap theirs = other.Course;
        return other.PreservePitch == Retime.PreservePitch && !mine.Freeze && mine.SameCourse(theirs)
            && (_wsola is null || _wsola.Hop == Wsola.HopFor(theirs.RateRange(theirs.Start, theirs.End).Max));
    }

    /// <summary>Sigue con <paramref name="other"/>, que <see cref="Serves"/> aceptó: lee su recorrido sin rehacer la cadena.</summary>
    public void Adopt(AudioRetime other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other != Retime)
        {
            Retime = other;
            _map.Retarget(other.Course);
        }
    }

    /// <summary>Muestra del origen que devolverá la próxima lectura del decodificador; -1 si no se sabe.</summary>
    public long DecoderNext => _source.DecoderNext;

    internal Wsola? Wsola => _wsola;

    internal SourceWindow Source => _source;

    /// <summary>Llena <paramref name="interleaved"/> (estéreo) con el clip desde la muestra <paramref name="from"/> de la línea de tiempo.</summary>
    /// <param name="decoderNext">Dónde quedó el decodificador, que pudo usar otro clip entre medias; -1 si no se sabe.</param>
    public void Read(long from, Span<float> interleaved, long decoderNext)
    {
        _source.DecoderNext = decoderNext;
        if (_wsola is not null)
        {
            _wsola.Render(from, interleaved);
        }
        else
        {
            _varispeed!.Render(from, interleaved);
        }
    }
}
