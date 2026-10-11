using Rodaje.Core.Model;

namespace Rodaje.App.Services.Settings;

/// <summary>
/// De qué pistas de video sale el audio de un medio a su propia pista al importarlo o soltarlo (se elige en Ajustes):
/// de todas, o solo de las de una lista. Las pistas se cuentan por su lugar en la secuencia: «Video 1» es la de más
/// abajo (la principal), «Video 2» la siguiente, y así. Si el video cae en una pista que no está en la lista, el medio
/// entra sin su clip de audio.
/// </summary>
public sealed class LinkedAudioTracks
{
    /// <summary>Número de pista más alto que se acepta en la lista (un archivo editado a mano no la hace crecer sin fin).</summary>
    public const int MaxNumber = 64;

    private readonly int[]? _numbers;

    private LinkedAudioTracks(int[]? numbers)
    {
        _numbers = numbers;
    }

    /// <summary>El audio sale de todas las pistas de video, también de las que se añadan después (lo de siempre).</summary>
    public static LinkedAudioTracks All { get; } = new(null);

    /// <summary><c>true</c> si el audio sale de todas las pistas de video.</summary>
    public bool IsAll => _numbers is null;

    /// <summary>Las pistas de la lista, ordenadas y sin repetir; vacía con <see cref="All"/>.</summary>
    public IReadOnlyList<int> Numbers => _numbers ?? [];

    /// <summary>
    /// Solo de las pistas con esos números (1 = «Video 1»). Se descartan los que no estén entre 1 y
    /// <see cref="MaxNumber"/>; una lista vacía quiere decir que ninguna pista de video trae su audio.
    /// </summary>
    public static LinkedAudioTracks Only(IEnumerable<int> numbers)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        return new LinkedAudioTracks([.. numbers.Where(n => n is >= 1 and <= MaxNumber).Distinct().Order()]);
    }

    /// <summary>Lo guardado en <c>ajustes.json</c>: <c>null</c> para todas, o la lista.</summary>
    public static LinkedAudioTracks FromSetting(IEnumerable<int>? numbers) => numbers is null ? All : Only(numbers);

    /// <summary>Lo que se guarda en <c>ajustes.json</c>: <c>null</c> para todas, o la lista.</summary>
    public List<int>? ToSetting() => _numbers is null ? null : [.. _numbers];

    /// <summary>Si la pista de video número <paramref name="number"/> (1 = «Video 1») trae el audio del medio.</summary>
    public bool AllowsNumber(int number) => number >= 1 && (_numbers is null || Array.BinarySearch(_numbers, number) >= 0);

    /// <summary>
    /// Si un medio que cae en la pista <paramref name="trackId"/> trae su audio a una pista de audio. Una pista que no
    /// es de video (o que no está en la secuencia) no la limita esta regla.
    /// </summary>
    public bool Allows(Sequence sequence, string trackId)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        int index = sequence.VideoTracks.FindIndex(t => t.Id == trackId);
        return index < 0 || AllowsNumber(index + 1);
    }

    /// <summary>
    /// Pone o quita la pista <paramref name="number"/> de la lista. Desde «todas» se pasa a una lista con las pistas 1 a
    /// <paramref name="knownTracks"/> salvo esa.
    /// </summary>
    public LinkedAudioTracks Toggle(int number, int knownTracks)
    {
        IEnumerable<int> current = _numbers ?? Enumerable.Range(1, Math.Clamp(Math.Max(knownTracks, number), 1, MaxNumber));
        return AllowsNumber(number) ? Only(current.Where(n => n != number)) : Only(current.Append(number));
    }
}
