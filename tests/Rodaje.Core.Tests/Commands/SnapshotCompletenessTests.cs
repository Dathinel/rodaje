using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Commands;

/// <summary>
/// La instantánea de deshacer no se deja nada: se arma por reflexión una secuencia con todos sus campos llenos (pistas,
/// clips, efectos, keyframes, transiciones, títulos con estilo, velocidades, buses, marcadores…) y se cambia cada dato
/// suelto que llega al archivo, de uno en uno. Restaurar la instantánea tiene que devolver el archivo idéntico y
/// <see cref="SequenceSnapshot.Matches"/> tiene que ver la diferencia. Un campo nuevo del modelo que se olvide en un
/// <c>Clone</c> o en la comparación hace caer esta prueba sin que nadie la toque.
/// </summary>
public sealed class SnapshotCompletenessTests
{
    [Fact]
    public void Restaurar_devuelve_y_Matches_ve_cada_dato_que_llega_al_archivo()
    {
        string original = Serialize(Filled());
        List<Step[]> leaves = Leaves();
        Assert.True(leaves.Count > 500, $"La secuencia de prueba tiene pocos datos ({leaves.Count}).");

        var lost = new List<string>();
        var unseen = new List<string>();
        foreach (Step[] path in leaves)
        {
            Sequence sequence = Filled();
            var snapshot = SequenceSnapshot.Capture(sequence);
            Apply(sequence, path);
            if (Serialize(sequence) == original)
            {
                continue;
            }

            if (snapshot.Matches(sequence))
            {
                unseen.Add(Describe(path));
            }

            snapshot.Restore(sequence);
            if (Serialize(sequence) != original)
            {
                lost.Add(Describe(path));
            }

            if (!snapshot.Matches(sequence))
            {
                unseen.Add("(tras restaurar) " + Describe(path));
            }
        }

        Assert.True(lost.Count == 0, "Restaurar no devuelve: " + string.Join(", ", lost));
        Assert.True(unseen.Count == 0, "Matches no ve el cambio de: " + string.Join(", ", unseen));
    }

    /// <summary>
    /// Capturar contra la instantánea anterior (un paso de un arrastre) comparte las pistas, clips y transiciones que la
    /// comparación da por iguales: si se le escapara un dato, la nueva restauraría el valor viejo. Con cada dato cambiado
    /// de uno en uno, las dos instantáneas restauran cada una su estado.
    /// </summary>
    [Fact]
    public void Capturar_contra_la_anterior_no_pierde_ningun_dato()
    {
        string original = Serialize(Filled());
        var lost = new List<string>();
        foreach (Step[] path in Leaves())
        {
            Sequence sequence = Filled();
            var first = SequenceSnapshot.Capture(sequence);
            Apply(sequence, path);
            string changed = Serialize(sequence);
            if (changed == original)
            {
                continue;
            }

            var second = SequenceSnapshot.Capture(sequence, first);
            first.Restore(sequence);
            bool firstBack = Serialize(sequence) == original;
            second.Restore(sequence);
            if (!firstBack || Serialize(sequence) != changed || !second.Matches(sequence))
            {
                lost.Add(Describe(path));
            }
        }

        Assert.True(lost.Count == 0, "Compartir con la anterior pierde: " + string.Join(", ", lost));
    }

    [Fact]
    public void La_copia_de_la_instantanea_no_comparte_nada_mutable()
    {
        // Cambiar la secuencia después de capturar no puede alcanzar a la instantánea, y restaurar dos veces da lo mismo.
        Sequence sequence = Filled();
        string original = Serialize(sequence);
        var snapshot = SequenceSnapshot.Capture(sequence);
        foreach (Step[] path in Leaves())
        {
            Apply(sequence, path);
        }

        snapshot.Restore(sequence);
        Assert.Equal(original, Serialize(sequence));
        foreach (Step[] path in Leaves())
        {
            Apply(sequence, path);
        }

        snapshot.Restore(sequence);
        Assert.Equal(original, Serialize(sequence));
    }

    private static string Serialize(Sequence sequence) =>
        ProjectSerializer.Serialize(new Project { Id = "p", Name = "p", Sequences = [sequence] });

    private static Sequence Filled() => (Sequence)new Filler().Create(typeof(Sequence), 0)!;

    private static string Describe(Step[] path) => string.Join(".", path.Select(s => s.ToString()));

    private abstract record Step;

    private sealed record PropertyStep(PropertyInfo Property) : Step
    {
        public override string ToString() => Property.DeclaringType!.Name + ":" + Property.Name;
    }

    private sealed record IndexStep(int Index) : Step
    {
        public override string ToString() => $"[{Index}]";
    }

    private sealed record KeyStep(string Key) : Step
    {
        public override string ToString() => $"[{Key}]";
    }

    /// <summary>Caminos hasta cada dato suelto (texto, número, tiempo, enumerado…) de la secuencia llena.</summary>
    private static List<Step[]> Leaves()
    {
        var result = new List<Step[]>();
        Walk(Filled(), typeof(Sequence), [], result);
        return result;
    }

    private static void Walk(object? value, Type type, List<Step> path, List<Step[]> result)
    {
        if (IsLeaf(type) || value is null)
        {
            result.Add([.. path]);
            return;
        }

        if (value is IDictionary dictionary)
        {
            Type valueType = type.GetGenericArguments()[1];
            foreach (string key in dictionary.Keys.Cast<string>().Order(StringComparer.Ordinal).ToList())
            {
                path.Add(new KeyStep(key));
                Walk(dictionary[key], valueType, path, result);
                path.RemoveAt(path.Count - 1);
            }

            return;
        }

        if (value is IList list)
        {
            Type elementType = type.GetGenericArguments()[0];
            for (int i = 0; i < list.Count; i++)
            {
                path.Add(new IndexStep(i));
                Walk(list[i], elementType, path, result);
                path.RemoveAt(path.Count - 1);
            }

            return;
        }

        foreach (PropertyInfo property in Settable(value.GetType()))
        {
            path.Add(new PropertyStep(property));
            Walk(property.GetValue(value), property.PropertyType, path, result);
            path.RemoveAt(path.Count - 1);
        }
    }

    private static void Apply(Sequence sequence, Step[] path) => Mutate(sequence, typeof(Sequence), path, 0);

    // Los records se tratan como inmutables (el modelo los comparte entre copias): se cambia una copia y se pone en su
    // lugar. Los demás objetos se cambian en sitio, que es justo lo que una copia superficial no aguantaría.
    private static object? Mutate(object? current, Type type, Step[] path, int i)
    {
        if (i == path.Length)
        {
            return Changed(current, type);
        }

        object target = current!;
        if (IsRecordClass(target.GetType()))
        {
            target = MemberwiseCloneMethod.Invoke(target, null)!;
        }

        switch (path[i])
        {
            case PropertyStep { Property: var property }:
                property.SetValue(target, Mutate(property.GetValue(target), property.PropertyType, path, i + 1));
                break;
            case IndexStep { Index: var index }:
                var list = (IList)target;
                list[index] = Mutate(list[index], type.GetGenericArguments()[0], path, i + 1);
                break;
            case KeyStep { Key: var key }:
                var dictionary = (IDictionary)target;
                dictionary[key] = Mutate(dictionary[key], type.GetGenericArguments()[1], path, i + 1);
                break;
        }

        return target;
    }

    private static readonly MethodInfo MemberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static object? Changed(object? value, Type type)
    {
        Type? inner = Nullable.GetUnderlyingType(type);
        if (value is null)
        {
            return new Filler(salt: 7).Create(inner ?? type, 0);
        }

        Type actual = inner ?? type;
        return value switch
        {
            string s => s + "~",
            bool b => !b,
            int n => n + 1,
            long n => n + 1,
            double d => d + 0.5,
            float f => f + 0.5f,
            Time t => t + Time.FromTicks(1),
            Rational r => r == Rational.Fps25 ? Rational.Ntsc30 : Rational.Fps25,
            Enum e => NextEnum(actual, e),
            _ => null,
        };
    }

    private static object NextEnum(Type type, Enum value)
    {
        Array values = Enum.GetValues(type);
        int index = Array.IndexOf(values, value);
        return values.GetValue((index + 1) % values.Length)!;
    }

    private static bool IsLeaf(Type type)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Time) || t == typeof(Rational) || t == typeof(decimal);
    }

    private static bool IsRecordClass(Type type) =>
        type.IsClass && type.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic) is not null;

    /// <summary>Propiedades públicas con lectura y escritura que son modelo (no las vistas «InFile» del archivo).</summary>
    private static IEnumerable<PropertyInfo> Settable(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetMethod is { IsPublic: true } && p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
            .Where(p => p.GetCustomAttribute<EditorBrowsableAttribute>()?.State != EditorBrowsableState.Never)
            .OrderBy(p => p.Name, StringComparer.Ordinal);

    /// <summary>Crea objetos del modelo con todos sus datos llenos y distintos de los de por omisión, siempre iguales.</summary>
    private sealed class Filler(int salt = 0)
    {
        private const int MaxDepth = 9;
        private int _counter = salt;

        public object? Create(Type type, int depth)
        {
            _counter++;
            Type? inner = Nullable.GetUnderlyingType(type);
            if (inner is not null)
            {
                return Create(inner, depth);
            }

            if (type == typeof(string))
            {
                return "t" + _counter.ToString(CultureInfo.InvariantCulture);
            }

            if (type == typeof(bool))
            {
                return _counter % 2 == 0;
            }

            if (type == typeof(int))
            {
                return 2 + (_counter % 5);
            }

            if (type == typeof(long))
            {
                return 3L + _counter;
            }

            if (type == typeof(double))
            {
                return 0.25 + (_counter % 7);
            }

            if (type == typeof(float))
            {
                return 0.5f;
            }

            if (type == typeof(Time))
            {
                return Time.FromTicks(1000 + _counter);
            }

            if (type == typeof(Rational))
            {
                return Rational.Ntsc24;
            }

            if (type.IsEnum)
            {
                Array values = Enum.GetValues(type);
                return values.GetValue(values.Length > 1 ? 1 : 0);
            }

            if (depth > MaxDepth || type.IsInterface || type.IsAbstract)
            {
                return null;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var list = (IList)Activator.CreateInstance(type)!;
                Type element = type.GetGenericArguments()[0];
                // Dos de cada en lo alto (pistas, clips) para ver el orden; una más abajo, para que la prueba sea rápida.
                for (int i = 0; i < (depth <= 3 ? 2 : 1); i++)
                {
                    list.Add(Create(element, depth + 1));
                }

                return list;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var dictionary = (IDictionary)Activator.CreateInstance(type)!;
                Type element = type.GetGenericArguments()[1];
                dictionary["k1"] = Create(element, depth + 1);
                if (depth <= 3)
                {
                    dictionary["k2"] = Create(element, depth + 1);
                }

                return dictionary;
            }

            ConstructorInfo? constructor = type.GetConstructor(Type.EmptyTypes)
                ?? type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            if (constructor is null)
            {
                return null;
            }

            object instance = constructor.Invoke([.. constructor.GetParameters().Select(p => Create(p.ParameterType, depth + 1))]);
            foreach (PropertyInfo property in Settable(type))
            {
                property.SetValue(instance, Create(property.PropertyType, depth + 1));
            }

            return instance;
        }
    }
}
