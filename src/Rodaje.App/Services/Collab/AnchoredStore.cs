using System.Text.Json;
using System.Text.Json.Serialization;
using Dathinel.Sala;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// Las personas que el anfitrión ya dejó entrar (clave, nombre y rol), junto al proyecto: vuelven sin pedir permiso otra vez.
/// Un archivo dañado se aparta y se empieza vacío, para que una sala nunca deje de abrirse por esto.
/// </summary>
public static partial class AnchoredStore
{
    public const string FileName = "anclados.json";
    private const int MaxMembers = 16;

    internal sealed record Entry(string Key, string Alias, string Role);

    [JsonSerializable(typeof(List<Entry>))]
    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    private sealed partial class Context : JsonSerializerContext;

    public static IReadOnlyList<Miembro> Load(string folder)
    {
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            List<Entry>? entries = JsonSerializer.Deserialize(File.ReadAllText(path), Context.Default.ListEntry);
            return [.. (entries ?? []).Take(MaxMembers).Where(Valid).Select(e => new Miembro(e.Key, e.Alias, e.Role == "mirar" ? Rol.Mirar : Rol.Editar))];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AppDataPaths.SetAside(path, ".danado");
            return [];
        }
    }

    public static void Save(string folder, IEnumerable<Miembro> members)
    {
        List<Entry> entries = [.. members.Select(m => new Entry(m.Clave, m.Alias, m.Rol == Rol.Mirar ? "mirar" : "editar"))];
        try
        {
            AppDataPaths.WriteAllTextAtomic(Path.Combine(folder, FileName), JsonSerializer.Serialize(entries, Context.Default.ListEntry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin poder guardar, la sala sigue: solo se pedirá permiso otra vez la próxima vez.
        }
    }

    private static bool Valid(Entry e) => CollabNames.IsKey(e.Key) && CollabNames.IsAlias(e.Alias);
}
