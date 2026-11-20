using Rodaje.Core.Collab;

namespace Rodaje.App.Services.Collab;

/// <summary>Un paso propio que queda a disposición de la persona, sin incorporarlo automáticamente.</summary>
public sealed record UnsettledChange(string Id, string Description, string Reason, Patch Patch);

/// <summary>«Cambios que no entraron». La edición y su persistencia las coordina el motor.</summary>
public sealed class UnsettledChanges
{
    private readonly Lock _gate = new();
    private readonly List<UnsettledChange> _items = [];

    public IReadOnlyList<UnsettledChange> Items
    {
        get { lock (_gate) return _items.ToArray(); }
    }

    internal bool Add(Patch patch, string reason)
    {
        lock (_gate)
        {
            if (_items.Any(i => i.Id == patch.Id)) return false;
            _items.Add(new UnsettledChange(patch.Id, patch.Description, reason, patch));
            return true;
        }
    }

    internal bool Remove(string id)
    {
        lock (_gate) return _items.RemoveAll(i => i.Id == id) > 0;
    }
}
