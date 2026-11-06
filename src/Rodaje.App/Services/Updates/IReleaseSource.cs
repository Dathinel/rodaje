namespace Rodaje.App.Services.Updates;

/// <summary>
/// De dónde sale la última versión publicada: el JSON de la release más reciente del repositorio. Lo real es
/// <see cref="GitHubReleaseSource"/>; las pruebas dan uno de mentira. Puede lanzar cualquier error de red o de lectura:
/// <see cref="UpdateChecker"/> lo trata como «no se pudo comprobar».
/// </summary>
public interface IReleaseSource
{
    /// <summary>El cuerpo JSON de la última release, tal como lo devuelve la API.</summary>
    Task<string> GetLatestReleaseJsonAsync(CancellationToken cancellationToken);
}
