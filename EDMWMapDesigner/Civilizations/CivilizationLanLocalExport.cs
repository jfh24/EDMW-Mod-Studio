namespace EDMWMapDesigner.Civilizations;

/// <summary>Prepares a portable sender export and installs it once without losing its original baselines.</summary>
public static class CivilizationLanLocalExport
{
    public static CivilizationInstallerPackage Prepare(CivilizationEditProject project, string gamePath)
    {
        ArgumentNullException.ThrowIfNull(project);
        return CivilizationPackageManager.ReadInstalledExport(project, gamePath) ?? CivilizationInstallerPackage.Create(project, gamePath);
    }

    public static CivilizationLanResult Install(CivilizationInstallerPackage package, string gamePath)
    {
        var result = CivilizationPackageManager.InstallOrVerifyInstalled(package, gamePath);
        return new(result.Success ? CivilizationLanStatus.Installed : CivilizationLanStatus.Failed, result.Message);
    }

    public static bool IsInstalled(CivilizationInstallerPackage package, string gamePath) =>
        CivilizationPackageManager.IsInstalledAndVerified(package, gamePath);
}
