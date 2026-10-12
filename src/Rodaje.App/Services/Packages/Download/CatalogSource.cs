using System.Net;
using Rodaje.Packages;
using Rodaje.Packages.Download;

namespace Rodaje.App.Services.Packages.Download;

/// <summary>
/// De dónde baja Rodaje sus complementos: el índice firmado de la última release de <c>Dathinel/rodaje-catalogo</c>,
/// con la clave pública que viaja dentro del exe (<c>clave-catalogo.txt</c>, la escribe <c>build\firmar-indice.ps1</c>).
/// Sin clave, la descarga queda desactivada y se instala desde archivo.
/// </summary>
public static class CatalogSource
{
    public const string PublicKeyResourceName = "Rodaje.App.clave-catalogo.txt";

    /// <summary>Versión del programa que se compara con el <c>minApp</c> de cada entrada (la de <c>Directory.Build.props</c>).</summary>
    public static PackageVersion AppVersion { get; } = PackageVersion.Parse(typeof(CatalogSource).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    public static Uri IndexUrl { get; } = new("https://github.com/Dathinel/rodaje-catalogo/releases/latest/download/indice.json");

    /// <summary>Descargas y parciales (se reanudan) junto a los complementos.</summary>
    public static string DownloadFolder(PackageLocations locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(locations.Packages))!, "descargas");
    }

    /// <summary>Modelos de IA compartidos, por hash.</summary>
    public static string ModelFolder(PackageLocations locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(locations.Packages))!, "modelos");
    }

    /// <summary>La clave del catálogo incluida en el programa, o <c>null</c> si esta compilación no la trae.</summary>
    public static string? BundledPublicKey()
    {
        using Stream? stream = typeof(CatalogSource).Assembly.GetManifestResourceStream(PublicKeyResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd().Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Un cliente HTTP para GitHub y Hugging Face: sigue redirecciones (a sus CDN) y se identifica.</summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Rodaje/{AppVersion}");
        return http;
    }

    /// <summary>
    /// El almacén de modelos de la app. Su cliente no tiene plazo total (un modelo puede tardar una hora): una conexión
    /// que se queda sin datos la corta <see cref="FileDownloader.StallTimeout"/> y se reanuda.
    /// </summary>
    public static ModelStore CreateModelStore(PackageLocations locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        HttpClient http = CreateHttpClient();
        http.Timeout = Timeout.InfiniteTimeSpan;
        return new ModelStore(ModelFolder(locations), new FileDownloader(http) { MaxAttempts = 20, RetryDelay = TimeSpan.FromSeconds(2) });
    }

    /// <summary>El descargador de la app, o uno no disponible si falta la clave.</summary>
    public static IPackageDownloader CreateDownloader(PackageLocations locations, string? publicKey = null)
    {
        ArgumentNullException.ThrowIfNull(locations);
        publicKey ??= BundledPublicKey();
        if (IndexSignature.TryImportPublicKey(publicKey) is not { } key)
        {
            return new UnavailablePackageDownloader("La descarga directa todavía no está activada en esta versión: baja el .paquete de la release y usa «Instalar desde archivo…».");
        }

        key.Dispose();
        return new GitHubReleaseDownloader(new GitHubReleaseOptions
        {
            IndexUrl = IndexUrl,
            PublicKey = publicKey,
            DownloadFolder = DownloadFolder(locations),
            AppVersion = AppVersion,
        }, CreateHttpClient());
    }
}
