using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>Keyframes copiados con tiempos relativos al primero, para pegarlos en otro punto del mismo parámetro.</summary>
public sealed class KeyframeClipboard
{
    private readonly Keyframe[] _keys;

    private KeyframeClipboard(Keyframe[] keys) => _keys = keys;

    /// <summary>Keyframes relativos al instante de anclaje cero; conservan interpolación y manejadores.</summary>
    public IReadOnlyList<Keyframe> Keys => Array.AsReadOnly(_keys);

    /// <summary>Captura la selección, normalizando el primer instante a cero y preservando los espacios originales.</summary>
    public static KeyframeClipboard Copy(Parameter parameter, IEnumerable<Time> selectedTimes)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(selectedTimes);
        Time[] selected = selectedTimes.Distinct().Order().ToArray();
        if (selected.Length == 0)
        {
            throw new ArgumentException("Selecciona al menos un keyframe.", nameof(selectedTimes));
        }

        Dictionary<Time, Keyframe> available = parameter.Keyframes.ToDictionary(key => key.Time);
        Keyframe[] copied = new Keyframe[selected.Length];
        for (int index = 0; index < selected.Length; index++)
        {
            if (!available.TryGetValue(selected[index], out Keyframe? key))
            {
                throw new ArgumentException($"No hay un keyframe en {selected[index]}.", nameof(selectedTimes));
            }

            copied[index] = key with { Time = key.Time - selected[0] };
        }

        return new KeyframeClipboard(copied);
    }

    /// <summary>Desplaza la selección a <paramref name="at"/> como nuevo primer instante.</summary>
    public IReadOnlyList<Keyframe> PasteAt(Time at) => Array.AsReadOnly(_keys.Select(key => key with { Time = at + key.Time }).ToArray());
}
