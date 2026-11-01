using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Updates;
using Rodaje.Packages;

namespace Rodaje.App.Tests.Updates;

/// <summary>
/// Aviso de versión nueva: la respuesta de <c>releases/latest</c> se compara con la versión de la app, se respetan las
/// 24 h entre consultas y la versión descartada, y cualquier fallo queda en silencio.
/// </summary>
[Collection("StillImageDefaults")]
public class UpdateCheckerTests
{
    private static readonly PackageVersion Installed = PackageVersion.Parse("1.0.2");

    /// <summary>Reloj que solo avanza cuando la prueba lo pide.</summary>
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Devuelve el JSON que se le da, o falla como una red caída; cuenta las consultas.</summary>
    private sealed class FakeSource(string? json) : IReleaseSource
    {
        public int Calls { get; private set; }

        public Task<string> GetLatestReleaseJsonAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return json is null
                ? Task.FromException<string>(new HttpRequestException("No hay red."))
                : Task.FromResult(json);
        }
    }

    private static string Release(string tag) =>
        $$"""{"url":"https://api.github.com/repos/Dathinel/rodaje/releases/1","tag_name":"{{tag}}","name":"Rodaje {{tag}}","draft":false,"prerelease":false,"body":"Notas","assets":[]}""";

    private static (UpdateChecker Checker, FakeSource Source, ManualClock Clock) Make(string? json)
    {
        var source = new FakeSource(json);
        var clock = new ManualClock();
        return (new UpdateChecker(source, Installed, clock), source, clock);
    }

    [Fact]
    public async Task UnaVersionMayorSeAvisaConSuEtiquetaYSuPagina()
    {
        (UpdateChecker checker, FakeSource source, _) = Make(Release("v1.1.0"));
        var settings = new AppSettings();

        UpdateCheckResult result = await checker.CheckAsync(settings, manual: false);

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        PublishedRelease release = Assert.IsType<PublishedRelease>(result.Release);
        Assert.Equal("v1.1.0", release.Tag);
        Assert.Equal("1.1.0", release.Version.ToString());
        Assert.Equal("https://github.com/Dathinel/rodaje/releases/tag/v1.1.0", release.Url.AbsoluteUri);
        Assert.Equal(1, source.Calls);
    }

    [Theory]
    [InlineData("1.1.0")]
    [InlineData("v1.1")]
    [InlineData("v1.1.0-beta.1")]
    [InlineData("v1.1.0+5")]
    [InlineData("latest")]
    [InlineData("v01.1.0")]
    [InlineData("")]
    public async Task UnaEtiquetaRaraNoAvisa(string tag)
    {
        (UpdateChecker checker, _, _) = Make(Release(tag));

        UpdateCheckResult result = await checker.CheckAsync(new AppSettings(), manual: true);

        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.Null(result.Release);
        Assert.Null(UpdateChecker.ParseTag(tag));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no es json")]
    [InlineData("{\"message\":\"Not Found\"}")]
    [InlineData("[]")]
    [InlineData("{\"tag_name\":12}")]
    public async Task UnaRespuestaQueNoSeEntiendeNoAvisa(string json)
    {
        (UpdateChecker checker, _, _) = Make(json);

        UpdateCheckResult result = await checker.CheckAsync(new AppSettings(), manual: true);

        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task LaMismaVersionEsLaUltima()
    {
        (UpdateChecker checker, _, _) = Make(Release("v1.0.2"));

        UpdateCheckResult result = await checker.CheckAsync(new AppSettings(), manual: true);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Equal("v1.0.2", result.Release?.Tag);
    }

    [Fact]
    public async Task UnaVersionMenorTambienEsLaUltima()
    {
        (UpdateChecker checker, _, _) = Make(Release("v1.0.1"));

        UpdateCheckResult result = await checker.CheckAsync(new AppSettings(), manual: false);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task SinRedQuedaEnSilencioYAnotaLaConsulta()
    {
        (UpdateChecker checker, _, ManualClock clock) = Make(json: null);
        var settings = new AppSettings();

        UpdateCheckResult result = await checker.CheckAsync(settings, manual: false);

        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.Equal(clock.Now, settings.LastUpdateCheck);
    }

    [Fact]
    public async Task LaVersionDescartadaNoSeVuelveAAvisarAlArrancarPeroSiAMano()
    {
        (UpdateChecker checker, _, _) = Make(Release("v1.1.0"));
        var settings = new AppSettings();
        UpdateCheckResult first = await checker.CheckAsync(settings, manual: false);
        UpdateChecker.Dismiss(settings, first.Release!);
        settings.LastUpdateCheck = null;

        UpdateCheckResult startup = await checker.CheckAsync(settings, manual: false);
        UpdateCheckResult manual = await checker.CheckAsync(settings, manual: true);

        Assert.Equal("v1.1.0", settings.DismissedUpdateVersion);
        Assert.Equal(UpdateCheckStatus.Dismissed, startup.Status);
        Assert.Equal(UpdateCheckStatus.Available, manual.Status);
    }

    [Fact]
    public async Task OtraVersionMasNuevaQueLaDescartadaSiSeAvisa()
    {
        (UpdateChecker checker, _, _) = Make(Release("v1.2.0"));
        var settings = new AppSettings { DismissedUpdateVersion = "v1.1.0" };

        UpdateCheckResult result = await checker.CheckAsync(settings, manual: false);

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
    }

    [Fact]
    public async Task AlArrancarNoSeConsultaHastaPasadas24Horas()
    {
        (UpdateChecker checker, FakeSource source, ManualClock clock) = Make(Release("v1.1.0"));
        var settings = new AppSettings();
        await checker.CheckAsync(settings, manual: false);
        DateTimeOffset firstCheck = clock.Now;

        clock.Now += TimeSpan.FromHours(23);
        UpdateCheckResult tooSoon = await checker.CheckAsync(settings, manual: false);
        clock.Now += TimeSpan.FromHours(1);
        UpdateCheckResult due = await checker.CheckAsync(settings, manual: false);

        Assert.Equal(UpdateCheckStatus.Skipped, tooSoon.Status);
        Assert.Equal(UpdateCheckStatus.Available, due.Status);
        Assert.Equal(2, source.Calls);
        Assert.Equal(firstCheck + TimeSpan.FromHours(24), settings.LastUpdateCheck);
    }

    [Fact]
    public async Task AManoSeConsultaAunqueNoHayanPasado24Horas()
    {
        (UpdateChecker checker, FakeSource source, _) = Make(Release("v1.1.0"));
        var settings = new AppSettings();
        await checker.CheckAsync(settings, manual: false);

        UpdateCheckResult result = await checker.CheckAsync(settings, manual: true);

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task ConElAvisoApagadoNoSeConsultaAlArrancarPeroSiAMano()
    {
        (UpdateChecker checker, FakeSource source, _) = Make(Release("v1.1.0"));
        var settings = new AppSettings { CheckForUpdates = false };

        UpdateCheckResult startup = await checker.CheckAsync(settings, manual: false);
        UpdateCheckResult manual = await checker.CheckAsync(settings, manual: true);

        Assert.Equal(UpdateCheckStatus.Skipped, startup.Status);
        Assert.Null(settings.LastUpdateCheck);
        Assert.Equal(UpdateCheckStatus.Available, manual.Status);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public void UnaUltimaConsultaEnElFuturoNoBloqueaElAviso()
    {
        var now = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var settings = new AppSettings { LastUpdateCheck = now + TimeSpan.FromDays(3) };

        Assert.True(UpdateChecker.IsDue(settings, now));
    }

    [Fact]
    public void LosAjustesDeFabricaAvisanYNormalizeLimpiaLaVersionDescartada()
    {
        var settings = new AppSettings { DismissedUpdateVersion = "  " };

        settings.Normalize();

        Assert.True(settings.CheckForUpdates);
        Assert.Null(settings.LastUpdateCheck);
        Assert.Null(settings.DismissedUpdateVersion);
    }

    [Fact]
    public void LasPreferenciasLeenYEscribenElInterruptor()
    {
        var settings = new AppSettings();
        var model = new Rodaje.App.Views.Preferences.PreferencesViewModel(settings);

        model.CheckForUpdates = false;

        Assert.False(settings.CheckForUpdates);
        Assert.False(model.CheckForUpdates);
    }

    [Fact]
    public void ElOrigenRealPideLaReleaseDelRepositorioConPlazoCorto()
    {
        Assert.Equal("https://api.github.com/repos/Dathinel/rodaje/releases/latest", GitHubReleaseSource.LatestReleaseUrl.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(5), GitHubReleaseSource.Timeout);
    }
}
