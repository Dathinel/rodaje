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
    private readonly Wsola? _wsola;
    private readonly VarispeedResampler? _varispeed;

    public RetimedReader(AudioRetime retime, IPcmSource pcm, long decoderNext)
    {
        ArgumentNullException.ThrowIfNull(retime);
        Retime = retime;
        var map = new SampleMap(retime.Map);
        _source = new SourceWindow(pcm, decoderNext);
        if (retime.PreservePitch)
        {
            _wsola = new Wsola(map, _source, map.MaxRate());
        }
        else
        {
            _varispeed = new VarispeedResampler(map, _source);
        }
    }

    public AudioRetime Retime { get; }

    /// <summary>
    /// Si puede seguir con <paramref name="other"/> sin rehacer nada: el mismo clip, o uno que muestra el mismo origen en
    /// cada instante (las dos mitades de un clip dividido a velocidad constante, o el mismo tras mover otra cosa). Así un corte o una edición
    /// no reinician la cadena de WSOLA y no suenan.
    /// </summary>
    public bool Serves(AudioRetime other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other == Retime)
        {
            return true;
        }

        ClipTimeMap mine = Retime.Map;
        ClipTimeMap theirs = other.Map;
        if (other.PreservePitch != Retime.PreservePitch || theirs.Reverse != mine.Reverse || theirs.Freeze || mine.Freeze
            || mine.Speed.IsCurve || !theirs.Speed.Equals(mine.Speed))
        {
            return false;
        }

        Time last = theirs.End - Time.FromTicks(1);
        Time middle = theirs.Start + Time.FromTicks(theirs.Duration.Ticks / 2);
        return mine.ExtendedSource(theirs.Start) == theirs.ExtendedSource(theirs.Start)
            && mine.ExtendedSource(middle) == theirs.ExtendedSource(middle)
            && mine.ExtendedSource(last) == theirs.ExtendedSource(last);
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
