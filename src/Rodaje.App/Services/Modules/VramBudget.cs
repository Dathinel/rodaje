namespace Rodaje.App.Services.Modules;

/// <summary>
/// Reparte la memoria de video entre los módulos abiertos (un módulo de GPU ocupa su <c>vramMb</c> mientras su proceso
/// vive, con el modelo cargado). Reglas: un solo módulo pesado (≥ <see cref="HeavyMb"/>) a la vez, y la suma no pasa del
/// presupuesto, que es la VRAM menos los 3/8 de la vista previa. Uno que no cabe ni solo se deja correr solo (el módulo
/// reparte capas a la CPU). Sin VRAM conocida solo se aplica la regla del pesado. Seguro entre hilos.
/// </summary>
public sealed class VramBudget
{
    /// <summary>Desde aquí un módulo cuenta como «modelo pesado».</summary>
    public const int HeavyMb = 1024;

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _resident = new(StringComparer.Ordinal);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="dedicatedVideoMemoryBytes">VRAM dedicada; 0 si no se sabe.</param>
    public VramBudget(long dedicatedVideoMemoryBytes)
    {
        long mb = dedicatedVideoMemoryBytes / (1024 * 1024);
        BudgetMb = mb <= 0 ? null : (int)Math.Min(int.MaxValue, mb - (mb * 3 / 8));
    }

    /// <summary>Lo que pueden usar los módulos, o <c>null</c> si no se conoce la VRAM.</summary>
    public int? BudgetMb { get; }

    public int UsedMb
    {
        get
        {
            lock (_lock)
            {
                return _resident.Values.Sum();
            }
        }
    }

    /// <summary>Se completa la próxima vez que algo se libera.</summary>
    public Task Changed
    {
        get
        {
            lock (_lock)
            {
                return _changed.Task;
            }
        }
    }

    public IReadOnlyList<string> Resident
    {
        get
        {
            lock (_lock)
            {
                return [.. _resident.Keys];
            }
        }
    }

    /// <summary>Reserva <paramref name="mb"/> para el módulo; si ya la tenía, no cambia nada.</summary>
    public bool TryReserve(string moduleId, int mb)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleId);
        if (mb <= 0)
        {
            return true;
        }

        lock (_lock)
        {
            if (_resident.ContainsKey(moduleId))
            {
                return true;
            }

            if (_resident.Count > 0)
            {
                if (mb >= HeavyMb && _resident.Values.Any(v => v >= HeavyMb))
                {
                    return false;
                }

                if (BudgetMb is { } budget && _resident.Values.Sum() + mb > budget)
                {
                    return false;
                }
            }

            _resident[moduleId] = mb;
            return true;
        }
    }

    /// <summary>Lo que habría que cerrar para que entre <paramref name="mb"/> (los candidatos, en orden), o vacío si ya entra.</summary>
    public IReadOnlyList<string> Blocking(int mb)
    {
        lock (_lock)
        {
            if (mb <= 0 || _resident.Count == 0)
            {
                return [];
            }

            var blocking = new List<string>();
            if (mb >= HeavyMb)
            {
                blocking.AddRange(_resident.Where(r => r.Value >= HeavyMb).Select(r => r.Key));
            }

            if (BudgetMb is { } budget)
            {
                int used = _resident.Where(r => !blocking.Contains(r.Key)).Sum(r => r.Value);
                foreach (KeyValuePair<string, int> r in _resident.OrderByDescending(r => r.Value))
                {
                    if (used + mb <= budget)
                    {
                        break;
                    }

                    if (!blocking.Contains(r.Key))
                    {
                        blocking.Add(r.Key);
                        used -= r.Value;
                    }
                }
            }

            return blocking;
        }
    }

    public void Release(string moduleId)
    {
        TaskCompletionSource changed;
        lock (_lock)
        {
            if (!_resident.Remove(moduleId))
            {
                return;
            }

            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }
}
