using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// La «pila» de una tarea asíncrona que no termina: sigue la cadena de <c>await</c> desde la tarea hasta lo que de verdad
/// espera (una lectura de la tubería, el candado de escritura, un <see cref="TaskCompletionSource"/>…). Una tarea
/// <c>async</c> pendiente no está en ningún hilo, así que ni <c>dotnet-stack</c> la muestra; lo que queda es su máquina de
/// estados, con el número de estado, el <c>await</c> en curso y las variables que sobreviven a él. Se lee por reflexión
/// (campos que genera el compilador y campos privados de la biblioteca base): es solo para el mensaje de un tope que ya
/// falló, y si algo cambia de nombre la descripción se acorta, pero nunca lanza.
/// </summary>
internal static class PendingAwaits
{
    private const int MaxDepth = 16;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Una línea por eslabón, de la tarea de fuera hacia la espera de dentro.</summary>
    public static string Describe(Task task)
    {
        StringBuilder text = new();
        try
        {
            object? current = task;
            for (int depth = 0; current is not null && depth < MaxDepth; depth++)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n  {new string(' ', depth * 2)}→ ");
                current = Link(current, text);
            }
        }
#pragma warning disable CA1031 // Es el mensaje de un fallo: si la reflexión no encuentra algo, se dice y se sigue.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            text.Append(CultureInfo.InvariantCulture, $" (no se pudo seguir: {ex.GetType().Name}: {ex.Message})");
        }
        return text.ToString();
    }

    /// <summary>Describe un eslabón y devuelve el siguiente (lo que espera), o <c>null</c> si es la hoja.</summary>
    private static object? Link(object node, StringBuilder text)
    {
        if (node is Task task && StateMachineOf(task) is { } machine)
        {
            Type type = machine.GetType();
            text.Append(CultureInfo.InvariantCulture, $"{Name(type)} [{task.Status}]");
            object? next = null;
            List<string> locals = [];
            foreach (FieldInfo field in type.GetFields(Fields))
            {
                object? value = field.GetValue(machine);
                if (field.Name == "<>1__state") text.Append(CultureInfo.InvariantCulture, $" estado {value}");
                else if (field.Name.StartsWith("<>u__", StringComparison.Ordinal)) next ??= Awaited(value);
                else if (Local(field.Name) is { } local && Summary(value) is { } summary) locals.Add($"{local}={summary}");
            }
            if (locals.Count > 0) text.Append(" {").AppendJoin(", ", locals).Append('}');
            return next;
        }
        if (node is Task other)
        {
            // Una tarea que no es de un método async: WhenAll, WaitAsync, Delay, el candado de SemaphoreSlim, un TCS…
            text.Append(CultureInfo.InvariantCulture, $"{Name(other.GetType())} [{other.Status}]");
            return Inner(other);
        }
        // Un IValueTaskSource: por ejemplo la lectura o la escritura en curso de la tubería.
        text.Append(Name(node.GetType()));
        return null;
    }

    /// <summary>La máquina de estados de una tarea de un método <c>async</c> (el campo público de AsyncStateMachineBox).</summary>
    private static object? StateMachineOf(Task task) =>
        task.GetType().GetField("StateMachine", BindingFlags.Instance | BindingFlags.Public)?.GetValue(task);

    /// <summary>La tarea (o la fuente) que hay dentro de un awaiter: TaskAwaiter, ValueTaskAwaiter y sus Configured….</summary>
    private static object? Awaited(object? awaiter)
    {
        if (awaiter is null) return null;
        foreach (FieldInfo field in awaiter.GetType().GetFields(Fields))
        {
            object? value = field.GetValue(awaiter);
            if (value is Task task) return task;
            if (value is null) continue;
            Type type = field.FieldType;
            if (type == typeof(ValueTask) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
                return ValueTaskSource(value);
            if (type.IsValueType && !type.IsPrimitive && Awaited(value) is { } inner) return inner;
        }
        return null;
    }

    private static object? ValueTaskSource(object? valueTask) =>
        valueTask?.GetType().GetField("_obj", Fields)?.GetValue(valueTask);

    /// <summary>Lo que espera una tarea que no es de un método async, si se sabe: la tarea de WaitAsync o la que envuelve.</summary>
    private static Task? Inner(Task task)
    {
        foreach (FieldInfo field in task.GetType().GetFields(Fields))
            if (field.GetValue(task) is Task inner && !ReferenceEquals(inner, task) && field.Name is "_task" or "m_task" or "_innerTask")
                return inner;
        return null;
    }

    /// <summary>El nombre de una variable que sobrevive a un await («&lt;active&gt;5__2» → «active»); null si es otra cosa.</summary>
    private static string? Local(string field)
    {
        if (field == "<>4__this") return "this";
        if (!field.StartsWith('<')) return null;
        int end = field.IndexOf('>', StringComparison.Ordinal);
        return end > 1 && field.AsSpan(end).StartsWith(">5__") ? field[1..end] : null;
    }

    /// <summary>Valores cortos: números, booleanos, textos breves y el tamaño de las colecciones.</summary>
    private static string? Summary(object? value) => value switch
    {
        null => null,
        bool or int or long or double => Convert.ToString(value, CultureInfo.InvariantCulture),
        string s => s.Length <= 40 ? $"«{s}»" : null,
        ICollection collection => $"{Name(value.GetType())}({collection.Count})",
        CancellationTokenSource cts => $"CTS(cancelado={cts.IsCancellationRequested})",
        _ => null,
    };

    private static string Name(Type type)
    {
        string name = type.FullName ?? type.Name;
        int generic = name.IndexOf('[', StringComparison.Ordinal);
        if (generic > 0) name = name[..generic];
        return name.StartsWith("Rodaje.Modules.Protocol.", StringComparison.Ordinal) ? name["Rodaje.Modules.Protocol.".Length..] : name;
    }
}
