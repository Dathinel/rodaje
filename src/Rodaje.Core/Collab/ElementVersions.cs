using System.Text.Json.Nodes;

namespace Rodaje.Core.Collab;

/// <summary>
/// La versión de cada elemento compartido: un número que sube en uno cada vez que un parche aceptado lo toca (también al
/// borrarlo; la versión de un borrado se conserva para que deshacerlo pueda volver a crearlo). Sin entrada, la versión es 0.
/// Un invitado lleva la de lo confirmado por el anfitrión y, para los parches que aún no confirmó, una copia a la que les
/// aplica <see cref="Advance"/>.
/// </summary>
public sealed class ElementVersions
{
    private readonly Dictionary<ElementKey, int> _versions = [];

    public int Count => _versions.Count;

    public int Get(ElementKey key) => _versions.GetValueOrDefault(key);

    public int Get(ElementKind kind, string id) => Get(new ElementKey(kind, id));

    public void Set(ElementKey key, int version)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        if (version == 0)
        {
            _versions.Remove(key);
        }
        else
        {
            _versions[key] = version;
        }
    }

    /// <summary>Sube la versión de cada elemento del parche a la que tendrá una vez aceptado.</summary>
    public void Advance(Patch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        foreach (PatchOp op in patch.Ops)
        {
            Set(op.Key, (int)Math.Min(int.MaxValue, (long)Math.Max(Get(op.Key), op.BaseVersion) + 1));
        }
    }

    public ElementVersions Clone()
    {
        var copy = new ElementVersions();
        foreach ((ElementKey key, int version) in _versions)
        {
            copy._versions[key] = version;
        }

        return copy;
    }

    /// <summary>Texto JSON (<c>{"clip:abc":3,…}</c>) para entregárselo a quien entra a mitad de sesión.</summary>
    public string ToJson()
    {
        var node = new JsonObject();
        foreach ((ElementKey key, int version) in _versions.OrderBy(p => p.Key.WireKind, StringComparer.Ordinal).ThenBy(p => p.Key.Id, StringComparer.Ordinal))
        {
            node[key.ToString()] = version;
        }

        return node.ToJsonString();
    }

    /// <summary>Lee lo que escribió <see cref="ToJson"/>; <see langword="null"/> si el texto no es de ese formato.</summary>
    public static ElementVersions? TryParse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            if (JsonNode.Parse(json) is not JsonObject node)
            {
                return null;
            }

            var result = new ElementVersions();
            foreach ((string name, JsonNode? value) in node)
            {
                int colon = name.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0 || colon == name.Length - 1 || !ElementKinds.TryParse(name[..colon], out ElementKind kind)
                    || value is not JsonValue number || !number.TryGetValue(out int version) || version < 0)
                {
                    return null;
                }

                result.Set(new ElementKey(kind, name[(colon + 1)..]), version);
            }

            return result;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
