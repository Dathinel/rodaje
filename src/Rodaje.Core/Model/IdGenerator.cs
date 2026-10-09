using System.Globalization;

namespace Rodaje.Core.Model;

/// <summary>Fuente de identificadores para clips, pistas y demás objetos nuevos.</summary>
public interface IIdGenerator
{
    string NewId(string prefix);
}

/// <summary>Identificadores únicos globales, del tipo <c>clip-3f2a…</c>.</summary>
public sealed class GuidIdGenerator : IIdGenerator
{
    public static readonly GuidIdGenerator Instance = new();

    public string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}

/// <summary>Identificadores predecibles (<c>clip-1</c>, <c>clip-2</c>…), útiles en pruebas.</summary>
public sealed class SequentialIdGenerator : IIdGenerator
{
    private int _next;

    public string NewId(string prefix) =>
        $"{prefix}-{Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture)}";
}
