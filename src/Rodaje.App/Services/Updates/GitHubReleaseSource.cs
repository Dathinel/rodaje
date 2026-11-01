using System.Net;
using System.Net.Http.Headers;
using Rodaje.Packages;

namespace Rodaje.App.Services.Updates;

/// <summary>
/// La última release de <c>Dathinel/rodaje</c> por la API pública de GitHub, sin credenciales: una petición corta (5 s
/// en total), identificada como <c>Rodaje/&lt;versión&gt;</c> y con la respuesta acotada a 1 MB.
/// </summary>
public sealed class GitHubReleaseSource(PackageVersion appVersion) : IReleaseSource
{
    public static Uri LatestReleaseUrl { get; } = new("https://api.github.com/repos/Dathinel/rodaje/releases/latest");

    /// <summary>Lo máximo que espera la consulta entera (conexión, cabeceras y cuerpo).</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

    private readonly PackageVersion _appVersion = appVersion ?? throw new ArgumentNullException(nameof(appVersion));

    public async Task<string> GetLatestReleaseJsonAsync(CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = Timeout,
        };
        using var http = new HttpClient(handler) { Timeout = Timeout, MaxResponseContentBufferSize = 1 << 20 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Rodaje/{_appVersion}");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        using HttpResponseMessage response = await http.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
