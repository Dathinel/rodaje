using System.Text.Json;
using System.Text.Json.Serialization;
using Rodaje.App.Services.Settings;
using Rodaje.Packages;

namespace Rodaje.App.Services.Updates;

/// <summary>Qué salió de una comprobación de versión.</summary>
public enum UpdateCheckStatus
{
    /// <summary>No se consultó: el aviso está apagado o aún no pasaron 24 h desde la última consulta.</summary>
    Skipped,

    /// <summary>Hay una versión más nueva que la instalada.</summary>
    Available,

    /// <summary>Hay una versión más nueva, pero ya se descartó con «Más tarde».</summary>
    Dismissed,

    /// <summary>La instalada es la última (o más nueva, en una compilación de desarrollo).</summary>
    UpToDate,

    /// <summary>Sin red, la API falló o la respuesta no se entendió.</summary>
    Unavailable,
}

/// <summary>Una versión publicada: la etiqueta tal cual (<c>v1.1.0</c>), su versión y la página de la release.</summary>
public sealed record PublishedRelease(string Tag, PackageVersion Version)
{
    /// <summary>La página de la release en GitHub, armada desde la etiqueta (no se confía en la URL de la respuesta).</summary>
    public Uri Url => new(UpdateChecker.ReleasePageBase + Uri.EscapeDataString(Tag));
}

/// <summary>El resultado: el estado y, si hay versión nueva (avisada o descartada), cuál.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, PublishedRelease? Release = null);

/// <summary>
/// Decide si hay que avisar de una versión nueva: compara la etiqueta de la última release con la versión de la app,
/// respeta las 24 h entre consultas al arrancar y la versión descartada con «Más tarde». Sin WinUI; la red va detrás de
/// <see cref="IReleaseSource"/>. Nunca lanza por un fallo de red o de formato: devuelve <see cref="UpdateCheckStatus.Unavailable"/>.
/// </summary>
public sealed class UpdateChecker(IReleaseSource source, PackageVersion current, TimeProvider? clock = null)
{
    /// <summary>Cuánto se espera entre dos consultas automáticas al arrancar.</summary>
    public static TimeSpan MinimumInterval { get; } = TimeSpan.FromHours(24);

    /// <summary>Delante de la etiqueta va la página de esa release.</summary>
    public const string ReleasePageBase = "https://github.com/Dathinel/rodaje/releases/tag/";

    private readonly IReleaseSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>La versión de la app con la que se compara.</summary>
    public PackageVersion Current { get; } = current ?? throw new ArgumentNullException(nameof(current));

    /// <summary>Al arrancar: <c>true</c> si el aviso está activado y pasaron 24 h (o nunca se consultó).</summary>
    public static bool IsDue(AppSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.CheckForUpdates)
        {
            return false;
        }

        // Un reloj atrasado (la última consulta queda en el futuro) no bloquea el aviso para siempre.
        return settings.LastUpdateCheck is not { } last || last > now || now - last >= MinimumInterval;
    }

    /// <summary>
    /// La versión de una etiqueta <c>vMAYOR.MENOR.PARCHE</c>; <c>null</c> con cualquier otra forma (sin «v», con
    /// previa, con dos números o con texto).
    /// </summary>
    public static PackageVersion? ParseTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag) || tag.Length > 32 || tag[0] != 'v')
        {
            return null;
        }

        string text = tag[1..];
        return PackageVersion.TryParse(text, out PackageVersion? version) && version.Prerelease is null && !text.Contains('+', StringComparison.Ordinal)
            ? version
            : null;
    }

    /// <summary>
    /// Lo que se decide de un cuerpo JSON ya recibido, con la versión descartada en <paramref name="settings"/> (salvo
    /// que <paramref name="ignoreDismissed"/>). Una respuesta rara da <see cref="UpdateCheckStatus.Unavailable"/>.
    /// </summary>
    public UpdateCheckResult Evaluate(string json, AppSettings settings, bool ignoreDismissed = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (ParseRelease(json) is not { } release)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Unavailable);
        }

        if (release.Version.CompareTo(Current) <= 0)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, release);
        }

        bool dismissed = !ignoreDismissed && string.Equals(settings.DismissedUpdateVersion, release.Tag, StringComparison.Ordinal);
        return new UpdateCheckResult(dismissed ? UpdateCheckStatus.Dismissed : UpdateCheckStatus.Available, release);
    }

    /// <summary>
    /// Consulta la última release y decide. Al arrancar (<paramref name="manual"/> en <c>false</c>) solo si toca según
    /// <see cref="IsDue"/>, y entonces anota la hora en los ajustes aunque falle (así una red caída no insiste en cada
    /// arranque). A mano consulta siempre, también con el aviso apagado o la versión descartada.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(AppSettings settings, bool manual, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DateTimeOffset now = _clock.GetUtcNow();
        if (!manual && !IsDue(settings, now))
        {
            return new UpdateCheckResult(UpdateCheckStatus.Skipped);
        }

        settings.LastUpdateCheck = now;
        string json;
        try
        {
            json = await _source.GetLatestReleaseJsonAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            // Sin red, con tiempo agotado o con una respuesta de error: silencio.
            return new UpdateCheckResult(UpdateCheckStatus.Unavailable);
        }

        return Evaluate(json, settings, ignoreDismissed: manual);
    }

    /// <summary>Anota que la versión de <paramref name="release"/> no se vuelve a avisar al arrancar.</summary>
    public static void Dismiss(AppSettings settings, PublishedRelease release)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(release);
        settings.DismissedUpdateVersion = release.Tag;
    }

    private static PublishedRelease? ParseRelease(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        GitHubRelease? release;
        try
        {
            release = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.GitHubRelease);
        }
        catch (JsonException)
        {
            return null;
        }

        return release?.TagName is { } tag && ParseTag(tag) is { } version ? new PublishedRelease(tag, version) : null;
    }
}

/// <summary>Lo único que se lee de la respuesta de <c>releases/latest</c>.</summary>
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }
}

[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
