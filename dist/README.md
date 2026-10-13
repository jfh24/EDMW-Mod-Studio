# EDMW Mod Studio distribution

**[Download EDMW Mod Studio v1.0.0 for Windows](https://github.com/jfh24/EDMW-Mod-Studio/releases/download/v1.0.0/EDMW.Mod.Studio.exe)**

See the [v1.0.0 release page](https://github.com/jfh24/EDMW-Mod-Studio/releases/tag/v1.0.0) for release notes and assets.

The compiled application is a GitHub release asset. It is not committed to Git, so a source clone contains this README but does not contain the executable. The download is named `EDMW.Mod.Studio.exe`; run it directly or rename it to `EDMW Mod Studio.exe`.

To generate the executable locally, run this from PowerShell in the repository root:

```powershell
.\build.ps1
```

The build writes:

```text
dist\EDMW Mod Studio.exe
```

The executable is self-contained and runs on 64-bit Windows 10 or later. See the [project README](../README.md) for build prerequisites, setup, and usage.
