# WinSweep

A Windows desktop app that cleans disposable files, uninstalls applications safely,
explores disk usage, and shows machine health. Native WinUI 3, written in C#.

> **Working title.** "WinSweep" is a placeholder. Rename it before you publish:
> change the assembly name, the window title, the app data folder
> (`%LOCALAPPDATA%\WinSweep`) and the `AppSettings.SettingsPath` constant.

## What it does

| Screen | What it does |
|---|---|
| **Status** | Read-only health score, CPU, memory, storage, battery, network and top processes |
| **Clean** | Finds known disposable caches and temporary files, reviews them, then removes them |
| **Apps** | Lists registry and Microsoft Store applications, runs their uninstallers, and reviews leftover folders |
| **Projects** | Finds old, rebuildable project output such as `node_modules`, `target`, `build` and `dist` |
| **Disk** | Explores a folder tree, measures sizes and lists large files |
| **Optimize** | Runs bounded, one-purpose system tasks and reports exactly what each one changed |
| **History** | Every action the app took, newest first |
| **Settings** | Recycle Bin behaviour, size measurement, and your own protected paths |

## Design rules

These are the rules the code enforces, not aspirations:

- **Nothing is removed without being reviewed first.** Every destructive action goes
  through `PathGuard.Evaluate`, which refuses drive roots, system folders, the user
  profile root and reparse points.
- **Unknown stays unknown.** A folder that cannot be read reports `Partial` or
  `Unavailable` with a lower bound or no size at all. It never reports zero.
- **Removals are recoverable by default.** Items go to the Recycle Bin through the
  Windows shell, so the user can undo them.
- **A running application protects its own cache.** If Chrome is running, its cache
  folders are listed but never selected.
- **Recent files are left alone.** A cache younger than the rule's minimum age is
  listed and explained, but not selected.
- **Personal data is never selected by default.** Settings and profile folders stay
  unticked in the uninstall review.
- **Only exact matches.** The uninstall review matches folder names exactly. There is
  no vendor-prefix or wildcard sweeping.
- **Every action is logged.** One JSON line per action, in
  `%LOCALAPPDATA%\WinSweep\operations.jsonl`.
- **Maintenance is bounded.** Each task is one known action with a stated effect and a
  hard time limit. A task that needs an administrator account never starts without
  one; the app asks for permission and restarts elevated instead.
- **Store app data is personal data.** The `AppData\Packages` folder of a Microsoft
  Store application is listed but never selected by default.
- **Project roots are explicit.** WinSweep suggests only conventional source folders
  such as `source`, `Projects`, `workspace`, or `code`; it does not scan the whole
  profile, Documents, Downloads, OneDrive, or system folders by default.
- **Project artifacts are verified twice.** A candidate must be a recognized output
  folder under a project marker, older than seven days, fully measurable, and—when
  Git is present—free of tracked files. Recent, linked, sensitive, tracked, or
  unverifiable folders stay unselected or are omitted. The app checks again at the
  Recycle Bin boundary.

## Project layout

```
src/WinSweep.Core     Domain logic: safety guard, scanners, cleanup rules, logging
src/WinSweep.App      WinUI 3 shell and pages
tests/WinSweep.Core.Tests   xUnit tests for the safety-critical parts
```

The UI never talks to the file system directly. Everything goes through `WinSweep.Core`,
which is why the dangerous parts are testable without a window.

## Build and run

Requires the .NET 8 SDK (or newer) and Visual Studio 2022 or newer with the .NET
desktop workload. The WinUI 3 project needs the full MSBuild from Visual Studio,
because the XAML compiler runs as an MSBuild task.

The easiest route:

```powershell
.\build.ps1                        # tests + Debug build
.\build.ps1 -Configuration Release -Publish
```

Or by hand:

```powershell
# Tests
dotnet test tests\WinSweep.Core.Tests\WinSweep.Core.Tests.csproj

# App (needs MSBuild, not the dotnet CLI)
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
    src\WinSweep.App\WinSweep.App.csproj -t:Build -p:Configuration=Debug -p:Platform=x64
```

Run it:

```powershell
src\WinSweep.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\WinSweep.App.exe
```

Startup switches, useful for testing:

```powershell
WinSweep.App.exe --page Clean --scan    # open the Clean page and start a scan
WinSweep.App.exe --page Disk
WinSweep.App.exe --page Artifacts --scan
```

The published folder is about 226 MB, because the build is self-contained: it
bundles .NET and the Windows App SDK so the app runs on any Windows 10 1809+ machine
without installing a runtime. Set `SelfContained` to `false` and depend on the
Windows App SDK bootstrapper if you want a smaller download.

## Publishing

There are two deployment shapes, and they differ in one important way.

### Unpackaged (default)

The app runs straight from the build output and carries its own copy of the Windows
App Runtime, so it needs nothing installed on the machine.

```powershell
.\build.ps1 -Configuration Release -Publish
```

The published folder is about 226 MB, because the build is self-contained: it
bundles .NET and the Windows App SDK so the app runs on any Windows 10 1809+ machine
without installing a runtime.

### MSIX package

A packaged app installs from the Start menu and gets an identity, but it uses the
**Windows App Runtime framework package** that is already on the machine instead of
carrying its own copy. On this machine that is `Microsoft.WindowsAppRuntime.2`.

```powershell
# Build the package
.\build.ps1 -Configuration Release -PackageAsMsix

# Sign it with a development certificate, trust it and install it
.\tools\Sign-And-Install.ps1

# Remove the package and the certificate again
.\tools\Sign-And-Install.ps1 -Uninstall
```

The signing script creates a self-signed certificate matching the manifest publisher
(`CN=4F57FA0D-0E3C-4115-91BB-AEA7447D17EF`), adds it to
the machine's Trusted People and Root stores (the first run asks for administrator
rights), signs the package and installs it. `-SkipInstall` stops after signing.

Two details in the manifest are not obvious and both break the app if they are wrong:

- `EntryPoint="$targetentrypoint$"`, not the application class. The generated entry
  point runs on a single-threaded apartment, and the XAML runtime refuses to start
  otherwise.
- The manifest must name the `Windows.Universal` device family, because that is the
  app model family the XAML runtime activates under.

### App assets

The logos and the splash screen are generated, so every size stays consistent:

```powershell
.\tools\New-AppAssets.ps1
```

## Microsoft Store submission

The app is structurally ready for the Microsoft Store, but a few things must be
changed before you submit:

- **Identity.** The manifest already uses the Partner Center values
  (`MikePerac.WinPulseStudio` / `CN=4F57FA0D-0E3C-4115-91BB-AEA7447D17EF`). If you
  reserve a different name later, update both lines in `Package.appxmanifest`.
- **Do not sign the package.** Submit the unsigned MSIX from `artifacts\msix\`. The
  Store signs it with its own certificate. `tools/Sign-And-Install.ps1` is for
  sideloading on your own machine only.
- **Elevation.** Tasks that need an administrator account are marked
  `StoreAvailable = false` and report as unavailable in a Store build. The app
  detects a packaged installation at runtime and never tries to elevate.
- **Privacy policy.** Add a privacy policy URL in Partner Center. The app reads
  system information locally and sends nothing anywhere, but the Store still
  requires one.
- **Restricted capability.** `runFullTrust` needs Partner Center approval before
  submission. Request it early; it is the only capability the app needs.
- **Assets.** The 71×71 medium tile is generated by `tools/New-AppAssets.ps1`.

## Known gaps

- Store applications without a Start-menu app entry are omitted; this keeps runtime
  packages and internal components out of the user-facing list.
- App sizes show as `Unknown` when the install location cannot be read, which is
  common for Store applications.
- The Projects scanner recognizes a conservative set of build-output folder names;
  it does not clean arbitrary files, project caches outside those patterns, or
  user-global package caches.
- The Store applications cannot be uninstalled by the app. Windows owns that, so the
  button opens the application list in Settings.
- The maintenance tasks are a small, fixed set. There is no registry cleaner, no
  driver updater and no "boost" mode.
- There is no scheduler, background service or tray icon.
- The package is signed with a development certificate. A real release needs a
  certificate from a trusted authority and a matching publisher name.

## Naming and licence

This project is an independent piece of work. It does not reuse code from the Mole
project, which is GPL-3.0, and it does not use the Mole name or logo, which are
trademarks. If you later copy or adapt GPL-3.0 code into this project, the whole
project has to comply with the GPL and needs a different name and icon.
# WinSweep
