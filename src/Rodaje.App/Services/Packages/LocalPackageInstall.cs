using Rodaje.Packages;

namespace Rodaje.App.Services.Packages;

public enum LocalInstallOutcome
{
    Installed,
    NeedsConfirmation,
    Cancelled,
}

public sealed record LocalPackageInstall(LocalInstallOutcome Outcome, InstallResult? Result = null);

internal sealed class PackageConfirmationRequiredException(string sha256) : PackageInstallException(
    "El paquete contiene un programa que se ejecutará y no está en el catálogo firmado. Instálalo solo si confías en el origen.")
{
    public string Sha256 { get; } = sha256;
}
