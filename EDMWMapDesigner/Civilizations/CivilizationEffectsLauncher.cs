using System.Diagnostics;
using System.Security.Cryptography;

namespace EDMWMapDesigner.Civilizations;

internal static class CivilizationEffectsLauncher
{
    public static void Launch(string gamePath)
    {
        CivilizationPackageManager.WithRuntimeEffectsForLaunch(gamePath, rules =>
        {
            var executable = Path.Combine(gamePath, "Empires_DMW.exe");
            var configuration = CivilizationRuntimeConfiguration.Decode(rules);
            if (configuration.Count == 0)
            {
                using var game = Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = gamePath, UseShellExecute = true })
                    ?? throw new InvalidOperationException("Windows did not start EDMW.");
                return;
            }
            using (var gameFile = File.OpenRead(executable))
                if (Convert.ToHexString(SHA256.HashData(gameFile)) != CivilizationGameInstaller.SupportedExecutableSha256)
                    throw new InvalidDataException("Runtime effects require the supported EDMW executable.");
            if (configuration.Units.Any(rule => rule.Trigger == CivilizationUnitEffectTrigger.Activated))
                CivilizationUnitEffectBindings.ValidateNativeTechnologyGroups(
                    File.ReadAllBytes(Path.Combine(gamePath, "Data", "db", "dbtechtree.dat")));
            // A unique immutable snapshot per launch also keeps the exact rules and
            // grant log available when diagnosing a save or multiplayer session.
            var session = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EDMW Mod Studio", "Effects", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(session);
            foreach (var name in new[] { "EDMWEffects.dll", "EDMWEffectsLauncher.exe" })
            {
                using var source = typeof(CivilizationEffectsLauncher).Assembly.GetManifestResourceStream("EDMWMapDesigner.Native." + name)
                    ?? throw new InvalidOperationException("The effect runtime was not bundled in this build.");
                using var target = new FileStream(Path.Combine(session, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(target);
            }
            var config = Path.Combine(session, "rules.edmwe");
            File.WriteAllBytes(config, rules);
            File.WriteAllText(Path.Combine(session, "fingerprint.txt"), Convert.ToHexString(SHA256.HashData(rules)));
            var start = new ProcessStartInfo(Path.Combine(session, "EDMWEffectsLauncher.exe"))
            {
                WorkingDirectory = session, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(Path.GetFullPath(executable));
            start.ArgumentList.Add(config);
            using var launcher = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the runtime launcher.");
            // Native waits are bounded; completion means the hook was installed
            // before the game was resumed, not merely that a helper was started.
            var error = launcher.StandardError.ReadToEnd();
            launcher.WaitForExit();
            if (launcher.ExitCode != 0) throw new InvalidOperationException(error.Trim());
        });
    }
}
