# Time-Machine Dry Run + Deep Artifact Sweeper — Implementation Plan

**Goal:** (1) Every sweep is previewable as a dry-run manifest, goes into an encrypted,
compressed quarantine vault instead of being deleted, and can be rolled back with one click.
(2) The Projects page only offers build output of *dormant* projects, and WinSweep also finds
Docker leftovers, stale shader caches and orphaned game redistributables.

**Tech:** C# / .NET 8 (`net8.0-windows10.0.19041.0`), WinUI 3 app (`src/WinSweep.App`), logic in
`src/WinSweep.Core`, xUnit tests in `tests/WinSweep.Core.Tests`. Branch: `feature/time-machine-deep-sweeper`.

## Global Constraints (bind every task)

- **Layering:** all logic lives in `WinSweep.Core` and is unit-tested; `WinSweep.App` holds only
  view models (plain `ObservableObject`, no MVVM toolkit) and XAML with code-behind click handlers.
  Follow the neighbouring file's style: file-scoped namespaces, `sealed` classes, XML `<summary>` on
  public members, narrow `catch (Exception e) when (e is IOException or UnauthorizedAccessException)`.
- **Safety funnel:** nothing is removed, quarantined or restored without `PathGuard.Evaluate(path).Allowed`
  being true for that exact path at the moment of the action. Never pass `allowProtectedLocation: true`.
  Never follow or archive reparse points (`PathGuard.IsReparsePoint`).
- **Locked files are skipped, never fatal.** No shell dialogs, no unhandled exceptions from file IO.
- **No new dependencies** except `System.Security.Cryptography.ProtectedData` (Task 2). No new
  abstractions, interfaces or options the task text does not name.
- **External commands** only through `WinSweep.Core.Maintenance.CommandRunner.RunAsync(fileName, arguments, timeout, cancellationToken)`.
  Arguments built from machine output must be validated first; prune/restore commands use fixed strings.
- **History:** every real change appends an `OperationRecord` to `OperationLog` (see `MaintenanceService.Log`).
- **Tests** use real temp folders under `Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"))`
  and clean up after themselves. Tests must never touch the real vault, real settings, the real Recycle
  Bin, Docker or Steam: every class that reads a machine location takes that location as a parameter.
- **Sweep id** = 8 lowercase hex chars (`Guid.NewGuid().ToString("N")[..8]`), the same value used as
  the `RunId` in the operation log. Anything that turns an id into a path must first match `^[0-9a-f]{8}$`.
- **Commands:** Core tests: `dotnet test tests/WinSweep.Core.Tests/WinSweep.Core.Tests.csproj --nologo -v q`.
  App build (the only thing that catches XAML errors): `powershell -NoProfile -File .\build.ps1 -SkipTests`.
  Tasks that touch `src/WinSweep.App` must run both and report both.
- **Git:** commit on the current branch, plain messages, no attribution/co-author lines, never push.
- **Do not run the app's destructive actions on this machine** while testing (no real cleaning, no
  `docker ... prune`, no VSS creation). Launching the app to look at a page is fine.

## Architecture decisions already made (do not reopen)

- **One file is manifest and restoration index.** `sweep-manifest.json` is written by the dry run
  (to `%LOCALAPPDATA%\WinSweep\sweep-manifest.json`) and, for a real sweep, into the vault folder
  `%LOCALAPPDATA%\WinSweep\Vault\<sweep-id>\sweep-manifest.json`, where the vault fills in archive
  name, SHA-256, ACL and status per entry.
- **Archive format `.wsq`:** `[16-byte IV][AES-256-CBC( GZip( tar ) )]`, one archive per swept target.
  The AES key is 32 random bytes stored DPAPI-protected (CurrentUser) in `<vault root>\vault.key`.
  The SHA-256 in the manifest is of the whole `.wsq` file and is verified before anything is extracted.
- **Registry:** WinSweep never modifies the registry (see the comment on `MaintenanceCatalog`), so the
  manifest carries a `RegistryKeys` list that is always empty and there is no `.reg` export/import code.
- **ACLs:** the access-rule SDDL of each swept *target root* is stored and re-applied; children inherit.
- **Timestamps:** last-write time only. Last-access time is unreliable on Windows and is not used.
- **Vault beats Recycle Bin:** when `AppSettings.UseQuarantineVault` is true (default) Clean and Projects
  sweeps go to the vault; otherwise the existing Recycle Bin / permanent-delete behaviour is unchanged.
  Uninstall leftovers on the Apps page keep using the Recycle Bin.
- **Snapshot:** a VSS shadow copy is attempted only when elevated and `SnapshotBeforeSweep` is true;
  failure or non-elevation is a warning in the manifest and the status text, never a blocker.
- **Game redistributables inside protected roots** (e.g. `C:\Program Files (x86)\Steam`) are listed
  but blocked; the guard is not relaxed for them.

---

### Task 1: Sweep manifest, risk analyzer and planner (Core)

**Create** `src/WinSweep.Core/Sweep/SweepManifest.cs`, `RiskAnalyzer.cs`, `SweepPlanner.cs`;
`tests/WinSweep.Core.Tests/SweepPlannerTests.cs`.

`SweepManifest.cs` (namespace `WinSweep.Core.Sweep`):

```csharp
public enum SweepRisk { Safe, Caution, Locked }
public enum SweepEntryStatus { Planned, Quarantined, Skipped, Failed, Restored }

/// One thing a sweep wants to remove, as the caller knows it.
public sealed record SweepTarget(string Path, string Name, string Category, long SizeBytes);

public sealed record SnapshotInfo(bool Attempted, bool Succeeded, string Message);

public sealed class SweepEntry
{
    public required string Path { get; init; }      // PathGuard.Normalize'd
    public required string Name { get; init; }
    public required string Category { get; init; }
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public DateTime? LastWriteUtc { get; init; }
    public SweepRisk Risk { get; init; }
    public string RiskReason { get; init; } = string.Empty;
    // Filled in by the vault during a real sweep:
    public SweepEntryStatus Status { get; set; } = SweepEntryStatus.Planned;
    public string? Archive { get; set; }            // file name inside the sweep folder, e.g. "0003.wsq"
    public string? Sha256 { get; set; }             // lowercase hex of the .wsq file
    public string? AclSddl { get; set; }
    public int FilesArchived { get; set; }
    public List<string> SkippedFiles { get; set; } = new();
    public string Detail { get; set; } = string.Empty;
}

public sealed class SweepManifest
{
    public required string Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public bool DryRun { get; set; }
    public required string Source { get; init; }    // "clean" or "purge" (the OperationLog command names)
    public long TotalBytes { get; init; }
    public Dictionary<string, long> BytesByCategory { get; init; } = new();
    public List<SweepEntry> Entries { get; init; } = new();
    public List<string> RegistryKeys { get; init; } = new();   // always empty: WinSweep never edits the registry
    public SnapshotInfo? Snapshot { get; set; }

    public void Save(string path);                  // creates the directory; indented JSON, enums as strings
    public static SweepManifest? Load(string path); // null on missing file / IOException / JsonException
}
```

Use the same `JsonSerializerOptions` shape as `OperationLog` (`JsonStringEnumConverter`) plus `WriteIndented = true`.

`RiskAnalyzer.cs`:

```csharp
public sealed class RiskAnalyzer
{
    public RiskAnalyzer(IReadOnlyList<InstalledApp> apps, IReadOnlyList<string> runningProcessPaths);
    /// Reads the Uninstall registry keys (AppInventory.ListInstalledApps(measureSizes: false))
    /// and the main-module path of every process it is allowed to read.
    public static RiskAnalyzer ForThisMachine();
    public (SweepRisk Risk, string Reason) Evaluate(string path);
}
```

`Evaluate` rules, first match wins:
1. **Locked** — a running process path is the same as or under `path` (`PathGuard.IsSameOrUnder(processPath, path)`).
   Reason: `"{exe file name} is running from this location."`
2. **Caution** — `path` is the same as or under an app's non-empty `InstallLocation`.
   Reason: `"Inside the install folder of {app.Name}."`
3. **Caution** — `path` is under `%LOCALAPPDATA%` or `%APPDATA%` and the first path segment below that
   root equals an installed app's `Name` or `Publisher` (case-insensitive, exact).
   Reason: `"Data that {app.Name} is installed to use; the app recreates it."`
4. **Caution** — `PathGuard.LooksLikePersonalData(path)`. Reason: `"Looks like personal data."`
5. **Safe**, reason empty.

`ForThisMachine`: `Process.GetProcesses()`, read `MainModule?.FileName` inside try/catch
(`Win32Exception`, `InvalidOperationException`, `NotSupportedException` → skip that process), dispose each process.

`SweepPlanner.cs`:

```csharp
public static class SweepPlanner
{
    public static string DryRunManifestPath { get; }   // %LOCALAPPDATA%\WinSweep\sweep-manifest.json
    public static SweepManifest Build(string source, IEnumerable<SweepTarget> targets,
        RiskAnalyzer analyzer, bool dryRun, string? id = null);
}
```

`Build` is read-only on the targets. For each target: normalize the path; skip targets whose path no
longer exists; `IsDirectory`/`LastWriteUtc` from the filesystem (try/catch → null); risk from the analyzer.
`TotalBytes` = sum of `SizeBytes`; `BytesByCategory` = sum per `Category`. Entries ordered by size descending.
`id` defaults to a new sweep id.

**Tests** (`SweepPlannerTests`, all with temp dirs and a hand-built `RiskAnalyzer`):
- totals and `BytesByCategory` add up for three targets in two categories;
- a target that does not exist is left out;
- a process path under a target → `Locked`; target under an app `InstallLocation` → `Caution`; otherwise `Safe`;
- `Locked` wins when both rules 1 and 2 match;
- `Save` then `Load` round-trips every property (including enum names as strings in the JSON text);
- `Build` does not create, change or delete anything in the target folder (compare a file listing before/after).

**Done when:** tests pass; no existing file changed.

---

### Task 2: Quarantine vault with encrypted archives and restore (Core)

**Create** `src/WinSweep.Core/Sweep/SweepArchive.cs`, `QuarantineVault.cs`;
`tests/WinSweep.Core.Tests/QuarantineVaultTests.cs`.
**Modify** `src/WinSweep.Core/WinSweep.Core.csproj`: add `<PackageReference Include="System.Security.Cryptography.ProtectedData" Version="8.0.0" />`.

`SweepArchive.cs` — `internal static class SweepArchive` (add `<InternalsVisibleTo Include="WinSweep.Core.Tests" />`
to the Core csproj only if a test needs it; prefer testing through `QuarantineVault`):

```csharp
internal sealed record ArchiveResult(List<string> ArchivedFiles, List<string> SkippedFiles, long Bytes);

/// Writes [IV][AES-CBC(GZip(tar))]. Never modifies the source.
internal static ArchiveResult Create(string sourcePath, string archivePath, byte[] key, CancellationToken ct);
/// Extracts into destinationRoot (a directory target) or as destinationRoot itself (a file target).
internal static (int Restored, int SkippedExisting) Extract(string archivePath, string destinationPath,
    bool isDirectory, byte[] key, CancellationToken ct);
internal static string Sha256Hex(string filePath);   // lowercase hex, streaming
```

`Create` details:
- Stream chain: `FileStream(archivePath, CreateNew)` → write 16 random IV bytes → `CryptoStream(aes.CreateEncryptor(), Write)`
  → `GZipStream(CompressionLevel.Fastest)` → `new TarWriter(stream, TarEntryFormat.Pax)` (`System.Formats.Tar`).
- Directory target: walk it with an explicit stack. Entry names are paths relative to the target, with `/`.
  For each sub-directory write `new PaxTarEntry(TarEntryType.Directory, name + "/")` (so empty folders survive).
  Skip (and add to `SkippedFiles`) anything where `PathGuard.IsReparsePoint` is true; do not descend into it.
- File target: one entry named `Path.GetFileName(sourcePath)`.
- For each file: open it **yourself first** with `new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)`.
  If the open throws `IOException`/`UnauthorizedAccessException` → add to `SkippedFiles`, continue (nothing has
  been written to the tar for it). Otherwise write `new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = fs, ModificationTime = lastWriteUtc }`.
- Any exception *after* a file was opened (read error mid-copy) propagates: the caller deletes the partial archive.

`Extract` details:
- Read the 16-byte IV, then `CryptoStream(Read)` → `GZipStream(Decompress)` → `TarReader`.
- For a directory target the destination root is `destinationPath`; for a file target it is its parent and the
  single entry must be named `Path.GetFileName(destinationPath)`.
- Reject an entry (throw `InvalidDataException`) when its name is rooted, contains a `..` segment, or
  `PathGuard.IsSameOrUnder(Path.GetFullPath(Path.Combine(root, name)), root)` is false.
- Directory entry → `Directory.CreateDirectory`. Regular file → if the file already exists count it in
  `SkippedExisting` and leave it alone; else create the parent and `entry.ExtractToFile(dest, overwrite: false)`.
  Ignore every other entry type.

`QuarantineVault.cs`:

```csharp
public sealed record RestoreResult(int Restored, int Failed, int SkippedExisting, IReadOnlyList<string> Problems);

public sealed class QuarantineVault
{
    public QuarantineVault(string? root = null);       // default %LOCALAPPDATA%\WinSweep\Vault
    public string Root { get; }
    public SweepSession Begin(SweepManifest manifest); // sets DryRun = false, creates <Root>\<Id>\, saves the manifest
    public IReadOnlyList<SweepManifest> List();        // newest first; folders without a readable manifest are skipped
    public RestoreResult Restore(string sweepId, CancellationToken cancellationToken = default);
    public bool Delete(string sweepId);                // permanently removes one sweep folder
    public int PurgeExpired(TimeSpan maxAge, DateTimeOffset? now = null);  // by manifest.CreatedAt; returns count
}

public sealed class SweepSession
{
    public SweepManifest Manifest { get; }
    /// Same shape as RecycleBin.Send so services can take it as their remover.
    public RemovalResult Quarantine(string path, long sizeBytes);
    public void Complete();                            // saves the manifest
}
```

`Quarantine(path, _)`, in order — any failure returns `RemovalResult.Failed(reason)` and sets the entry's
`Status = Failed` and `Detail`:
1. `PathGuard.Evaluate(path).Allowed`, else `"Refused by the safety guard"`.
2. Refuse when `path` is the same as/under the vault root or the vault root is under `path`.
3. Find the entry by normalized path (case-insensitive); none → `"Not part of this sweep"`.
4. Path must exist.
5. Record `AclSddl`: `new DirectoryInfo/FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access)`; on exception leave null.
6. `SweepArchive.Create(path, <folder>\{index:D4}.wsq, key, ...)`. On exception: delete the partial `.wsq`, fail with the message. **Nothing has been removed yet.**
7. If no file was archived and at least one was skipped: delete the `.wsq`, `Status = Skipped`,
   `Detail = "Every file is in use"`, return `Failed("Every file is in use")`.
8. `Sha256 = SweepArchive.Sha256Hex(archive)`, `Archive`, `FilesArchived`, `SkippedFiles` set.
9. Only now delete each archived file (`File.Delete`; clear a read-only attribute first; a file that will not
   delete is appended to `SkippedFiles`). Then remove now-empty directories bottom-up, including the target root
   if it ended up empty (`Directory.Delete(dir)` non-recursive, ignore `IOException`).
10. `Status = Quarantined`; save the manifest (after every entry, so a crash loses nothing); return
    `RemovalResult.Ok(bytes)` where bytes = total length of the files actually deleted.

Key handling (private): `<Root>\vault.key` holds `ProtectedData.Protect(32 random bytes, null, DataProtectionScope.CurrentUser)`;
created on first `Begin`; `Restore` fails every entry with `"The vault key is missing or unreadable"` if it cannot be read.

`Restore(sweepId)`: id must match `^[0-9a-f]{8}$` and the manifest must load, else
`new RestoreResult(0, 0, 0, ["That sweep does not exist."])`. For each entry with `Status == Quarantined`:
1. archive file exists and `Sha256Hex(archive) == entry.Sha256` (ordinal-ignore-case), else Failed +
   problem `"{Path}: checksum mismatch, not restored"`; **do not extract**.
2. `PathGuard.Evaluate(entry.Path).Allowed`, else Failed + problem.
3. `SweepArchive.Extract(...)`; `InvalidDataException`/`CryptographicException`/IO errors → Failed + problem.
4. Re-apply `AclSddl` to the target root when not null (`SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access)`
   then `SetAccessControl`); an exception here is a problem line, not a failure.
5. `Status = Restored`, delete the `.wsq`.
Save the manifest at the end. `Restored`/`Failed` count entries; `SkippedExisting` sums files.

`Delete` and `PurgeExpired` validate the id pattern, require the folder to be directly under `Root`, and use
`Directory.Delete(folder, recursive: true)` inside the usual try/catch. They never touch `vault.key`.

**Tests** (`QuarantineVaultTests`, vault root and targets in temp):
- quarantine a folder with nested files and an empty sub-folder → originals gone, `.wsq` exists, manifest on disk
  has `Quarantined`, a 64-char `Sha256`, `FilesArchived` correct;
- **rollback integrity:** restore brings back every file with identical bytes and last-write time, and the empty folder;
- a single-file target round-trips;
- the `.wsq` bytes do not contain a known plaintext marker string that was in a source file (it is encrypted);
- flipping one byte of the `.wsq` → `Restore` reports the checksum problem, restores nothing, entry stays `Quarantined`;
- a file held open with `FileShare.None` is skipped: it stays on disk and is listed in `SkippedFiles`, its siblings
  are quarantined, the call returns success;
- a folder whose only file is locked → `Failed("Every file is in use")`, nothing removed, no `.wsq` left behind;
- a path that is not in the manifest is refused; a path that fails `PathGuard` (use a drive root) is refused;
- restore does not overwrite a file that was recreated in the meantime (content stays, `SkippedExisting == 1`);
- `PurgeExpired(TimeSpan.FromDays(7), now)` removes a sweep created 8 days before `now` and keeps one created 6 days before;
- `Restore("../x")` and `Delete("..")` return the not-found result / false without touching the disk.

**Done when:** tests pass; `git diff` touches only the files named above.

---

### Task 3: Snapshot, settings and service wiring (Core)

**Create** `src/WinSweep.Core/Sweep/VolumeSnapshot.cs`, `SweepRunner.cs`; `tests/WinSweep.Core.Tests/SweepRunnerTests.cs`.
**Modify** `Models/OperationRecord.cs`, `Settings/AppSettings.cs`, `Cleanup/CleanupService.cs`, `Cleanup/ProjectArtifactService.cs`.

- `OperationAction`: append `Quarantined` and `Restored` at the end of the enum.
- `AppSettings`: add `public bool UseQuarantineVault { get; set; } = true;`, `public int VaultRetentionDays { get; set; } = 7;`,
  `public bool SnapshotBeforeSweep { get; set; } = true;` with summaries in the style of the others.
- `CleanupService` constructor becomes
  `(OperationLog log, AppSettings settings, Func<string, long, RemovalResult>? remove = null, OperationAction removedAction = OperationAction.Removed)`.
  With `remove == null` behaviour is byte-for-byte what it is today. With a remover, call it instead of
  `RecycleBin.Send/DeletePermanently` and log `removedAction` on success. All existing guard checks stay in front of it.
- `ProjectArtifactService` constructor gains a trailing `OperationAction recycledAction = OperationAction.Recycled`;
  success is logged with that action and the detail `"Moved to the quarantine vault"` when it is `Quarantined`,
  otherwise the existing text.

`VolumeSnapshot.cs`:

```csharp
public static class VolumeSnapshot
{
    public const string NotElevatedMessage =
        "No Windows snapshot was taken because WinSweep is not running as administrator. The quarantine vault can still undo this sweep.";
    public static Task<SnapshotInfo> CreateAsync(bool isElevated, CancellationToken cancellationToken = default);
}
```
Not elevated → `new SnapshotInfo(false, false, NotElevatedMessage)` without starting any process. Elevated → run
`powershell.exe` with `-NoProfile -NonInteractive -Command "(Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName Create -Arguments @{Volume='<SystemDrive>\'; Context='ClientAccessible'}).ReturnValue"`
(system drive from `Environment.GetEnvironmentVariable("SystemDrive")`, default `C:`; it must match `^[A-Za-z]:$`),
timeout 3 minutes. Success = outcome `Applied` and trimmed output `"0"` → `(true, true, "A Windows shadow copy of {drive} was created.")`;
anything else → `(true, false, "Windows could not create a snapshot: {message}. The quarantine vault can still undo this sweep.")`.

`SweepRunner.cs`:

```csharp
public sealed class SweepRunner
{
    public SweepRunner(OperationLog log, AppSettings settings, QuarantineVault? vault = null,
        Func<bool>? isElevated = null, RiskAnalyzer? analyzer = null, string? dryRunManifestPath = null);

    /// Dry run: builds the manifest, saves it to the dry-run path, changes nothing else.
    public SweepManifest Preview(string source, IEnumerable<SweepTarget> targets);

    /// Real sweep: null when the vault is switched off. Otherwise builds the manifest, takes the
    /// snapshot when settings.SnapshotBeforeSweep, and opens the vault session.
    public Task<SweepSession?> BeginAsync(string source, IEnumerable<SweepTarget> targets, CancellationToken cancellationToken = default);

    public IReadOnlyList<SweepManifest> History();     // vault.List()
    public string? LastSweepId { get; }                // newest sweep that still has a Quarantined entry
    public RestoreResult Rollback(string sweepId, CancellationToken cancellationToken = default);
    public bool DeleteSweep(string sweepId);
    public int PurgeExpired();                         // settings.VaultRetentionDays, minimum 1
}
```
Defaults: `vault ?? new QuarantineVault()`, `isElevated ?? CleanupScanner.IsElevated`, `analyzer` built lazily with
`RiskAnalyzer.ForThisMachine()`, `dryRunManifestPath ?? SweepPlanner.DryRunManifestPath`.
`Rollback` calls `vault.Restore` and appends one `OperationRecord` per restored entry
(`Command = "rollback"`, `RunId = sweepId`, `Action = Restored`, `Path`, `SizeBytes`) and one `Failed` record per problem line.
`DeleteSweep` appends one record (`Command = "vault"`, `Action = Removed`, `Path = sweepId`, `Detail = "Quarantined sweep deleted permanently"`).

**Tests** (`SweepRunnerTests`; pass a temp vault, temp log, temp dry-run path, `isElevated: () => false`, a hand-built analyzer):
- `Preview` writes a manifest with `DryRun == true` to the given path and leaves the targets untouched;
- with `UseQuarantineVault = false`, `BeginAsync` returns null;
- **end-to-end:** `BeginAsync` → `new CleanupService(log, settings, session.Quarantine, OperationAction.Quarantined).Execute(candidates, session.Manifest.Id)`
  → `session.Complete()` → files are gone and the log has a `Quarantined` record → `Rollback(id)` → files are back
  byte-identical and the log has a `Restored` record; `LastSweepId` was the id before the rollback and is null after;
- the non-elevated session's manifest has `Snapshot.Attempted == false` and the `NotElevatedMessage`;
- `CleanupService` without a remover still behaves as before (existing `RemovalPathTests` stay green, unchanged);
- `VolumeSnapshot.CreateAsync(false)` returns immediately with the not-elevated message.

**Done when:** all Core tests pass.

---

### Task 4: Feature 1 in the app — dry-run preview, vault sweeps, rollback history, CLI flags (App)

**Modify** `StartupOptions.cs`, `MainWindow.xaml.cs`, `ViewModels/CleanViewModel.cs`, `Views/CleanPage.xaml(.cs)`,
`ViewModels/ProjectArtifactsViewModel.cs`, `Views/ProjectArtifactsPage.xaml(.cs)`, `ViewModels/HistoryViewModel.cs`,
`Views/HistoryPage.xaml(.cs)`, `ViewModels/SettingsViewModel.cs`, `Views/SettingsPage.xaml`.
Read each file fully before changing it and copy its existing patterns (card `Border` with `CardBrush`/`CardStrokeBrush`,
`BoolToVisibilityConverter`, `Tag="{Binding Id}"` + `Click` handlers, `ContentDialog` confirmations).

1. **StartupOptions:** `--dry-run` sets `DryRun = true` and `AutoScan = true`, and sets `Page = "clean"` if no
   `--page` was given. `--rollback <id|last>` sets `RollbackSweepId` (settable, like `MaintenanceTaskId`) and
   `Page = "history"`. Update the class summary and the `Page` doc comment.
2. **MainWindow:** after `InitializeComponent`, fire-and-forget
   `Task.Run(() => new SweepRunner(_log, _settings).PurgeExpired())` inside a try/catch for IO exceptions.
   Pass `_settings` to `HistoryPage` (constructor becomes `HistoryPage(OperationLog log, AppSettings settings)`).
3. **CleanViewModel:**
   - `Task<SweepManifest?> PreviewAsync()` — maps the selected candidates to `SweepTarget(c.Path, c.Name, c.Category, c.SizeBytes)`
     and returns `await Task.Run(() => runner.Preview("clean", targets))`; null + status text when nothing is selected.
   - `RemoveSelectedAsync`: `var session = await runner.BeginAsync("clean", targets);` then
     `session is null ? new CleanupService(_log, _settings) : new CleanupService(_log, _settings, session.Quarantine, OperationAction.Quarantined)`,
     run id = `session?.Manifest.Id ?? <existing 8-char id>`, call `session?.Complete()` in a `finally`.
     Status text in vault mode: `"Moved {n} locations ({size}) to the quarantine vault. Undo it from History within {days} days."`,
     followed by the snapshot message when `session.Manifest.Snapshot` is not `Succeeded` and settings asked for one.
   - Replace `IsUsingRecycleBin` consumers with a `RemovalModeText` property (three cases: vault / Recycle Bin / permanent)
     used by the confirmation dialog.
4. **CleanPage:** add a `Preview (dry run)` button beside the remove button. Its handler shows a `ContentDialog` built in
   code-behind by a small private `BuildPreviewContent(SweepManifest)` method: total size, one line per category
   (`{Category}: {size}`), counts of Safe/Caution/Locked, then the Caution and Locked entries with `Name — RiskReason`
   (in a `ScrollViewer`, max height ~260), and the manifest file path. Primary button `Open manifest`
   (`Process.Start(new ProcessStartInfo(SweepPlanner.DryRunManifestPath) { UseShellExecute = true })`), close button `Close`.
   When `StartupOptions.DryRun` is set, show that dialog automatically after the auto-scan finishes and never remove anything.
5. **ProjectArtifactsViewModel / Page:** the same `PreviewAsync` (source `"purge"`, category `"Project build output"`,
   name `"{ProjectName}\{Name}"`), the same vault session around `ProjectArtifactService`
   (`new ProjectArtifactService(_log, _settings, session.Quarantine, OperationAction.Quarantined)`), a `Preview (dry run)`
   button, and vault-aware status/confirmation text. Put `BuildPreviewContent` in one shared static helper
   (`Views/SweepPreview.cs`) used by both pages — do not duplicate it.
6. **History page — rollback history:** above the existing list add a card `Sweeps you can undo` with an
   `Undo last sweep` button (enabled when `HasUndoableSweep`) and a `ListView` of
   `SweepHistoryItem { Id, WhenText, SourceText, SizeText, StatusText, CanRestore }` rows, each with `Restore`
   (visible when `CanRestore`) and `Delete now` buttons. `StatusText` examples: `"12 locations in quarantine"`,
   `"Restored"`, `"Partly restored — 2 problems"`. Row text also shows the snapshot message when present.
   `HistoryViewModel` gets `(OperationLog log, AppSettings settings)`, builds the rows from `runner.History()`,
   and exposes `Task RestoreAsync(string id)`, `Task UndoLastAsync()`, `void Delete(string id)`; all refresh both lists
   and set `StatusText` (`"Restored {n} locations. {k} files were left alone because they already exist."` /
   the first problem line on failure). `Delete now` asks for confirmation first (permanent, cannot be undone).
   When `StartupOptions.RollbackSweepId` is set, run it once on `Loaded` (`"last"` → `UndoLastAsync`) and clear it.
7. **Settings page:** in the existing toggles card add `Quarantine swept files so they can be restored` (`UseQuarantineVault`),
   `Take a Windows snapshot before a sweep (administrator only)` (`SnapshotBeforeSweep`) and a `NumberBox`
   `Keep quarantined files for (days)` (`VaultRetentionDays`, 1–90), each saved on change exactly like `UseRecycleBin`.

**Verify:** Core tests pass; `build.ps1 -SkipTests` succeeds; launch the built exe with `--page history` and
`--page clean` and confirm both pages render (a screenshot or a note of what was seen in the report). Do not run a real sweep.

---

### Task 5: Project inactivity heuristics (Core)

**Create** `src/WinSweep.Core/Scanning/ProjectActivity.cs`; `tests/WinSweep.Core.Tests/ProjectActivityTests.cs`.
**Modify** `Scanning/ProjectArtifactScanner.cs`, `Models/ProjectArtifact.cs`, `Settings/AppSettings.cs`,
`Cleanup/ProjectArtifactService.cs`, `tests/WinSweep.Core.Tests/ProjectArtifactTests.cs`.

`ProjectActivity.cs`:

```csharp
public static class ProjectActivity
{
    /// The newest of: the last Git commit, and the newest last-write time of a source file outside
    /// build output. Null when neither could be established, which callers must treat as "active".
    public static DateTime? LastActivityUtc(string projectRoot, CancellationToken cancellationToken = default);
    public static DateTime? LastCommitUtc(string projectRoot);          // null: not a repo, no commits, git missing, >1 s
    public static DateTime? LatestSourceWriteUtc(string projectRoot, CancellationToken cancellationToken = default);
}
```
- `LastCommitUtc`: only when `<root>\.git` exists; `git -C <root> log -1 --format=%ct` → `DateTimeOffset.FromUnixTimeSeconds`.
  `ProjectArtifactScanner.HasTrackedFiles` already contains the code that locates and runs git with `ArgumentList`,
  drains both pipes and kills after 1 s: move that into one `internal static string? RunGit(string projectRoot, params string[] args)`
  in `ProjectActivity` (null on any failure or non-zero exit) and make `HasTrackedFiles` call it. Do not keep two copies.
- `LatestSourceWriteUtc`: explicit-stack walk from the root. Skip directories that are reparse points, that
  `ProjectArtifactScanner.IsRecognizedArtifact(name)` accepts, or named `.git .svn .hg .vs .idea vendor packages Pods`.
  A file counts when its extension is one of
  `.ts .tsx .js .jsx .mjs .cjs .py .rs .cs .fs .vb .go .java .kt .c .cc .cpp .h .hpp .swift .rb .php .html .css .scss .vue .svelte .xaml .sql .sh .ps1 .json .toml .yaml .yml .csproj .fsproj .sln`
  or its name is a project marker (`Cargo.toml`, `package.json`, `Makefile`, …, reuse `ProjectArtifactScanner`'s set by
  making it `internal`). Budget: 20 000 files or 2 seconds; when the budget runs out return **null** (unknown ⇒ treated as active).
  Unreadable directories are skipped.

Scanner changes:
- constructor gains a trailing `TimeSpan? projectInactivity = null` (default `TimeSpan.FromDays(30)`).
- a private `Dictionary<string, DateTime?> _activityCache` (case-insensitive), cleared at the start of `Scan`;
  `Revalidate` removes the project's key first so the removal boundary always re-measures.
- in `InspectArtifact`, after the existing Git-tracked check and **before** the existing artifact-age check
  (which stays): look up the project's activity; when `block is null`:
  null → `"This project's recent activity could not be verified, so its build output is kept."`;
  newer than the threshold → `"This project was worked on within the last {days:0} days (last activity {date:d MMM yyyy}), so its build output is kept."`
- `ProjectArtifact` gains `public DateTime? ProjectLastActivityUtc { get; init; }`; `ActivityDisplay` prefers it:
  `"Project last worked on {date:d MMM yyyy}"`. `CopyWithBlock` copies it.
- `AppSettings`: `public int ProjectInactivityDays { get; set; } = 30;`.
- `ProjectArtifactService`: build its revalidation scanner with
  `projectInactivity: TimeSpan.FromDays(Math.Max(1, settings.ProjectInactivityDays))`.

**Existing tests will break** because their fixtures create marker/source files with the current time: age the
project's files in the fixture helper (it already ages the artifact) or pass `projectInactivity: TimeSpan.Zero`
where the test is about something else. Do not weaken any assertion.

**Tests** (`ProjectActivityTests` + additions to `ProjectArtifactTests`):
- `LatestSourceWriteUtc` returns the newest source file time and ignores a newer file inside `node_modules`, `bin` and `.git`;
- it ignores a newer non-source file (`notes.docx`);
- no source files and no repo → `LastActivityUtc` is null;
- scanner: project whose newest source file is 10 days old, artifact 60 days old, threshold 30 → blocked with the
  "worked on within the last 30 days" text and not selected;
- same project with source 45 days old → selectable; with threshold 60 → blocked again (the threshold is honoured);
- project with no measurable activity → blocked with the "could not be verified" text;
- a real git repo test **only if** `git --version` succeeds on the machine (otherwise return early): `git init`, one
  commit made now, all source files aged 90 days → blocked because the commit is recent.

**Done when:** all Core tests pass.

---

### Task 6: Docker leftovers, orphaned game redistributables, AMD shader cache (Core)

**Create** `src/WinSweep.Core/Scanning/DockerScanner.cs`, `Scanning/GameRedistScanner.cs`;
`tests/WinSweep.Core.Tests/DockerScannerTests.cs`, `GameRedistScannerTests.cs`.
**Modify** `Cleanup/CleanupCatalog.cs`, `Cleanup/CleanupScanner.cs`.

**Shader cache:** `D3DSCache`, `NVIDIA\DXCache` and `NVIDIA\GLCache` rules already exist. Add one rule
`amd-shaders` for `%LOCALAPPDATA%\AMD\DxCache`, copied from the `nvidia-shaders` rule (same category, age and risk;
name/description say AMD).

**Docker:**

```csharp
public sealed record DockerItem(string Kind, string Name, int Count, string Reclaimable);

public sealed class DockerScanner
{
    /// Pure: turns `docker system df --format "{{json .}}"` lines plus the dangling image ids into items.
    public static IReadOnlyList<DockerItem> Parse(string systemDfOutput, string danglingImageIds);
    public async Task<(IReadOnlyList<DockerItem> Items, string? Unavailable)> ScanAsync(CancellationToken cancellationToken = default);
    public async Task<MaintenanceResult> PruneAsync(DockerItem item, OperationLog log, string runId, CancellationToken cancellationToken = default);
}
```
- `ScanAsync` runs `docker.exe system df --format "{{json .}}"` (30 s) and `docker.exe images -f dangling=true -q` (30 s).
  If the first does not come back `Applied` → `(empty, "Docker is not installed or its engine is not running.")`.
- `Parse`: each non-empty line is a JSON object with string fields `Type`, `TotalCount`, `Active`, `Reclaimable`
  (e.g. `"1.2GB (45%)"`). Lines that are not JSON are skipped. Items produced:
  - `Type == "Images"` → `("images", "Dangling images", count = non-empty lines of danglingImageIds, "")` — only when count > 0;
  - `Type == "Containers"` → `("containers", "Stopped containers", TotalCount − Active, Reclaimable)` — only when count > 0;
  - `Type == "Build Cache"` → `("build-cache", "Unused build cache", TotalCount − Active, Reclaimable)` — only when
    `Reclaimable` does not start with `"0B"`;
  - `Local Volumes` is ignored on purpose (volumes hold data).
- `PruneAsync`: the command comes from a `switch` on `item.Kind` — `image prune -f`, `container prune -f`,
  `builder prune -f`; any other kind → `MaintenanceResult.Unavailable(...)` without running anything. Timeout 5 minutes.
  Appends one record: `Command = "docker"`, `Action = Completed|Failed`, `Path = item.Kind`, `Detail = "{item.Name}: {result.Message}"`.

**Game redistributables:**

```csharp
public static class GameRedistScanner
{
    public static IReadOnlyList<CleanupCandidate> Scan();   // this machine: Steam libraries + Epic
    public static IReadOnlyList<string> ReadSteamLibraries(string libraryFoldersVdfText);
    public static IReadOnlyList<CleanupCandidate> FindSteamOrphans(IEnumerable<string> libraryRoots);
    public static IReadOnlyList<CleanupCandidate> FindEpicOrphans(string manifestsDirectory);
}
```
- `ReadSteamLibraries`: every `"path"\s+"([^"]+)"` match, with `\\` unescaped to `\`.
- `FindSteamOrphans`: for each library root, `steamapps\appmanifest_*.acf` → the set of `"installdir"\s+"([^"]+)"`
  values (case-insensitive). For each directory in `steamapps\common` **not** in that set: look at its direct
  children and their direct children for directories named `_CommonRedist`, `DirectX` or `vcredist` (case-insensitive,
  not reparse points) and emit one candidate per hit. If there are **no** `.acf` files at all in a library, emit nothing
  for it (an unreadable library must not make every game look uninstalled).
- `FindEpicOrphans`: `*.item` files are JSON with `"InstallLocation"`. Installed set = those paths. For each distinct
  parent directory of an install location, apply the same rule to the sibling directories that are not installed
  locations. No `.item` files → nothing.
- `Scan`: Steam path from `HKCU\Software\Valve\Steam` value `SteamPath` (replace `/` with `\`); libraries = that path
  plus `ReadSteamLibraries(<path>\steamapps\libraryfolders.vdf)`; Epic manifests at
  `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests`. Missing key/file → that store contributes nothing.
- Candidate shape: `Name = "Leftover installers: {game folder name}"`, `Category = "Games"`, `Risk = CleanupRisk.Review`,
  `Description = "{game folder name} is no longer installed. These are setup files it left behind."`,
  size via `DirectorySizer.Measure(path, default, TimeSpan.FromSeconds(2))`, `LastWriteTimeUtc`, **`IsSelected = false`**.
  When `PathGuard.Evaluate(path)` rejects it: `BlockedReason = "Inside a protected system folder. Remove it from the game launcher instead."`
- `CleanupScanner.Scan`: after the rule loop finishes, add `GameRedistScanner.Scan()` results that are not
  `settings.IsProtected`, calling `onFound` for each, inside a try/catch for IO/unauthorized/security exceptions.
  `CleanupService` already re-checks the guard, so a blocked candidate can never be removed even if ticked.

**Tests:**
- `DockerScanner.Parse`: sample with all four types → three items with the right counts and reclaimable text;
  zero dangling ids → no images item; `"Reclaimable":"0B (0%)"` build cache → no item; garbage lines ignored; empty input → empty.
- `PruneAsync` with an unknown kind returns `Unavailable` and writes nothing to a temp log.
- `ReadSteamLibraries` on a realistic vdf with two libraries and escaped backslashes.
- `FindSteamOrphans` on a temp library: installed game with `_CommonRedist` → not flagged; uninstalled game folder with
  `_CommonRedist` and `Engine\vcredist` → two candidates, unselected, category `Games`; uninstalled folder with no
  redist folders → nothing; library without any `.acf` → nothing.
- `FindEpicOrphans` on a temp layout with one installed and one orphaned sibling.

**Done when:** all Core tests pass. Do not call `PruneAsync` with a real kind anywhere.

---

### Task 7: Feature 2 in the app — dormancy setting and Docker card (App)

**Modify** `ViewModels/ProjectArtifactsViewModel.cs`, `Views/ProjectArtifactsPage.xaml(.cs)`.

1. **Dormancy threshold:** the view model's scanner is no longer a `readonly` field created once; build it at the
   start of each `ScanAsync` with `projectInactivity: TimeSpan.FromDays(InactivityDays)`. Add
   `public int InactivityDays` (get = `_settings.ProjectInactivityDays`, set clamps to 1–365, saves settings, raises
   change). On the page add a `NumberBox` labelled `Skip projects worked on in the last (days)` near the scan button,
   two-way bound to it. Artifact rows already show `ActivityDisplay`/`StateText`, which now carry the project activity.
2. **Default scan roots:** read the view model's `GetRoots`. When `ProjectScanRootsConfigured` is false, also suggest
   every one of these that exists: `%USERPROFILE%\source\repos`, `%USERPROFILE%\Projects`, `%USERPROFILE%\repos`,
   `%USERPROFILE%\dev`, `%USERPROFILE%\Documents` — keeping whatever it already suggests, without duplicates.
   They are suggestions shown in the editable list; nothing is scanned until the user presses scan.
3. **Docker card:** a card below the artifact results titled `Docker` with a `Check Docker` button. The view model
   gets `ObservableCollection<DockerItem> DockerItems`, `string DockerStatusText`, `Task CheckDockerAsync()` and
   `Task PruneDockerAsync(DockerItem item)`. Rows show `Name`, `"{Count} item(s)"`, `Reclaimable` and a `Prune` button
   (`Tag="{Binding Kind}"`). The click handler asks for confirmation first — title `Prune {Name}?`, text
   `Docker deletes these permanently. This cannot be undone from WinSweep.` — then prunes, re-runs the check and
   shows `result.Message`. When Docker is unavailable show the `Unavailable` text and no rows. Both methods respect `IsBusy`.

**Verify:** Core tests pass; `build.ps1 -SkipTests` succeeds; launch with `--page projects` and confirm the page renders
with the new NumberBox and Docker card (note what was seen). Press `Check Docker` only; do not prune.
