using Rodaje.App.Services.Inspector;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Audio;

/// <summary>El desplegable «Salida» de cada tira del mezclador, sin WinUI (ver <see cref="ComboChoices"/>).</summary>
public static class StripOutputChoices
{
    /// <summary>
    /// «Mezcla» (nulo) y cada bus por su id, en orden; se elige el bus de <paramref name="output"/> o, si no tiene o el bus ya
    /// no está, la mezcla.
    /// </summary>
    public static (ChoiceList<string?> List, int Selected) Build(IReadOnlyList<AudioBus> buses, string? output)
    {
        ArgumentNullException.ThrowIfNull(buses);
        var list = new ChoiceList<string?>();
        list.Add("Mezcla", null);
        foreach (AudioBus bus in buses)
        {
            list.Add(bus.Name, bus.Id);
        }

        return (list, Math.Max(0, list.IndexOf(output)));
    }

    /// <summary>Si la salida elegida sigue valiendo al aplicarla después del evento: la mezcla o un bus que sigue estando.</summary>
    public static bool StillThere(IReadOnlyList<AudioBus> buses, string? busId)
    {
        ArgumentNullException.ThrowIfNull(buses);
        return busId is null || buses.Any(b => b.Id == busId);
    }
}
