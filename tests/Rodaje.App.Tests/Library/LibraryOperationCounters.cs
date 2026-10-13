namespace Rodaje.App.Views.Library;

// Costura de prueba: los contadores con que las pruebas de rendimiento de la biblioteca cuentan operaciones en vez de medir
// segundos. Los métodos parciales sin cuerpo de LibraryOrganization.cs desaparecen al compilar la app (ni la llamada queda);
// este proyecto compila los mismos fuentes y les da cuerpo aquí. Antes eran campos [ThreadStatic] que la app sumaba en
// rutas calientes solo para las pruebas.

public sealed partial class LibraryFolder
{
    /// <summary>Lecturas de <see cref="Id"/> en este hilo (una búsqueda lineal por id lee el de cada carpeta).</summary>
    [ThreadStatic]
    internal static long IdReads;

    static partial void CountIdRead() => IdReads++;
}

public sealed partial class LibraryOrganization
{
    /// <summary>Nombres de carpeta plegados en este hilo al buscar un nombre libre.</summary>
    [ThreadStatic]
    internal static long NameFolds;

    static partial void CountNameFold() => NameFolds++;
}

public sealed partial record Subclip
{
    /// <summary>Lecturas de <see cref="Id"/> y <see cref="MediaId"/> en este hilo (una búsqueda lineal lee la de cada subclip).</summary>
    [ThreadStatic]
    internal static long KeyReads;

    static partial void CountKeyRead() => KeyReads++;
}
