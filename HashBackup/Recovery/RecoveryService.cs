using System.Security.Cryptography;

namespace HashBackup.Recovery;

public enum RecoveryStatus
{
    Success = 0,
    Failed = 1,
    RehydrationPending = 2
}

/// <summary>
/// Optionen für Verify und Restore. Rehydration ist absichtlich opt-in, weil
/// sie Azure-Abrufkosten und je nach Priorität zusätzliche Kosten verursacht.
/// </summary>
public sealed record RecoveryOptions(
    string MetadataReference,
    string? Destination,
    bool DeepVerify = false,
    bool Rehydrate = false,
    OnlineAccessTier RehydrateTier = OnlineAccessTier.Cool,
    ArchiveRehydratePriority RehydratePriority = ArchiveRehydratePriority.Standard,
    bool Overwrite = false,
    bool DryRun = false);

public sealed record RecoveryResult(
    RecoveryStatus Status,
    int VerifiedFiles,
    int RestoredFiles,
    int PendingFiles,
    int FailedFiles);

/// <summary>
/// Führt eigenschaftsbasierte und tiefe Integritätsprüfungen sowie sichere,
/// atomare Wiederherstellungen aus.
/// </summary>
public sealed class RecoveryService(
    IReadableStorageBackend backend,
    int targetDirectoryDepth,
    IReadOnlyList<string> sourceFolders,
    string jobName = "Default")
{
    private readonly RestorePathPolicy _pathPolicy = new(sourceFolders);

    public async Task<RecoveryResult> VerifyAsync(
        RecoveryOptions options,
        CancellationToken ct = default)
    {
        var loadedCatalog = await LoadCatalogAsync(options, ct);
        if (loadedCatalog.Result != null)
        {
            return loadedCatalog.Result;
        }

        var verifiedFiles = 0;
        var pendingFiles = 0;
        var failedFiles = 0;
        var rehydrationRequests = new List<StorageObjectInfo>();
        var regularEntries = loadedCatalog.Catalog!.Entries
            .Where(entry => !entry.IsSymbolicLink && !entry.IsLegacyEmptyFile)
            .ToList();
        var contentIndex = await backend.IndexContentObjectsAsync(regularEntries.Select(entry => entry.Hash), ct);

        foreach (var entry in loadedCatalog.Catalog.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsSymbolicLink)
            {
                _ = entry.GetSymbolicLinkTarget();
                verifiedFiles++;
                continue;
            }

            if (entry.IsLegacyEmptyFile)
            {
                verifiedFiles++;
                continue;
            }

            var expectedObjectPath = entry.GetObjectPath(targetDirectoryDepth)!;
            var inspection = contentIndex.GetValueOrDefault(entry.Hash) ?? StorageObjectInfo.Missing(expectedObjectPath);
            var objectPath = inspection.ObjectPath;
            if (!ValidateStoredProperties(entry, inspection, options.DeepVerify, out var propertyError))
            {
                Log.Error("Verify fehlgeschlagen für {FileName}: {Reason}", entry.FileName, propertyError);
                failedFiles++;
                continue;
            }

            if (options.DeepVerify && inspection.Availability != StorageObjectAvailability.Online)
            {
                QueueRehydrationIfEnabled(inspection, options, rehydrationRequests);
                pendingFiles++;
                continue;
            }

            if (options.DeepVerify)
            {
                if (!await DownloadAndValidateAsync(
                        entry,
                        objectPath,
                        destinationPath: null,
                        destinationRoot: null,
                        overwrite: false,
                        ct))
                {
                    failedFiles++;
                    continue;
                }
            }

            verifiedFiles++;
        }

        await SubmitRehydrationRequestsAsync(rehydrationRequests, options, ct);

        var status = DetermineStatus(failedFiles, pendingFiles);
        LogRecoverySummary("Verify", status, verifiedFiles, 0, pendingFiles, failedFiles);
        return new RecoveryResult(status, verifiedFiles, 0, pendingFiles, failedFiles);
    }

    public async Task<RecoveryResult> RestoreAsync(
        RecoveryOptions options,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.Destination))
        {
            throw new ArgumentException("Für Restore ist ein Zielverzeichnis erforderlich.", nameof(options));
        }

        var loadedCatalog = await LoadCatalogAsync(options, ct);
        if (loadedCatalog.Result != null)
        {
            return loadedCatalog.Result;
        }

        var regularEntries = loadedCatalog.Catalog!.Entries
            .Where(entry => !entry.IsSymbolicLink && !entry.IsLegacyEmptyFile)
            .ToList();
        var symlinkEntries = loadedCatalog.Catalog.Entries.Where(entry => entry.IsSymbolicLink).ToList();
        var legacyEmptyEntries = loadedCatalog.Catalog.Entries.Where(entry => entry.IsLegacyEmptyFile).ToList();
        var contentIndex = await backend.IndexContentObjectsAsync(regularEntries.Select(entry => entry.Hash), ct);
        var failedFiles = 0;
        var pendingFiles = 0;
        var rehydrationRequests = new List<StorageObjectInfo>();

        // Phase 1 prüft alle Objekte und stößt optional Rehydrationen an. Solange
        // irgendein Blob offline ist, wird bewusst noch kein Teil-Restore erzeugt.
        foreach (var entry in regularEntries)
        {
            ct.ThrowIfCancellationRequested();
            var expectedObjectPath = entry.GetObjectPath(targetDirectoryDepth)!;
            var inspection = contentIndex.GetValueOrDefault(entry.Hash) ?? StorageObjectInfo.Missing(expectedObjectPath);
            if (!ValidateStoredProperties(entry, inspection, deepVerificationPlanned: true, out var propertyError))
            {
                Log.Error("Restore-Vorprüfung fehlgeschlagen für {FileName}: {Reason}", entry.FileName, propertyError);
                failedFiles++;
                continue;
            }

            if (inspection.Availability != StorageObjectAvailability.Online)
            {
                QueueRehydrationIfEnabled(inspection, options, rehydrationRequests);
                pendingFiles++;
            }
        }

        await SubmitRehydrationRequestsAsync(rehydrationRequests, options, ct);

        if (failedFiles > 0 || pendingFiles > 0)
        {
            var preflightStatus = DetermineStatus(failedFiles, pendingFiles);
            LogRecoverySummary("Restore-Vorprüfung", preflightStatus, 0, 0, pendingFiles, failedFiles);
            return new RecoveryResult(preflightStatus, 0, 0, pendingFiles, failedFiles);
        }

        if (options.DryRun)
        {
            Log.Information(
                "[DRY RUN] Restore-Vorprüfung erfolgreich; würde {FileCount} Einträge nach {Destination} wiederherstellen.",
                loadedCatalog.Catalog.Entries.Count,
                options.Destination);
            return new RecoveryResult(RecoveryStatus.Success, loadedCatalog.Catalog.Entries.Count, 0, 0, 0);
        }

        if (!_pathPolicy.PrepareDestinationRoot(options.Destination))
        {
            return new RecoveryResult(RecoveryStatus.Failed, 0, 0, 0, 1);
        }

        var restoredFiles = 0;
        var verifiedFiles = 0;
        foreach (var entry in regularEntries)
        {
            ct.ThrowIfCancellationRequested();
            var objectPath = contentIndex[entry.Hash].ObjectPath;
            var destinationPath = _pathPolicy.ResolveDestinationPath(entry, options.Destination);
            if (await DownloadAndValidateAsync(
                    entry,
                    objectPath,
                    destinationPath,
                    options.Destination,
                    options.Overwrite,
                    ct))
            {
                restoredFiles++;
                verifiedFiles++;
            }
            else
            {
                failedFiles++;
            }
        }

        foreach (var entry in legacyEmptyEntries)
        {
            ct.ThrowIfCancellationRequested();
            if (RestoreLegacyEmptyFile(entry, options.Destination, options.Overwrite))
            {
                restoredFiles++;
                verifiedFiles++;
            }
            else
            {
                failedFiles++;
            }
        }

        // Symlinks werden zuletzt angelegt, damit sie während regulärer Datei-Restores
        // nicht als umleitende Pfadkomponente missbraucht werden können.
        foreach (var entry in symlinkEntries)
        {
            ct.ThrowIfCancellationRequested();
            if (RestoreSymbolicLink(entry, options.Destination, options.Overwrite))
            {
                restoredFiles++;
                verifiedFiles++;
            }
            else
            {
                failedFiles++;
            }
        }

        var status = DetermineStatus(failedFiles, pendingFiles: 0);
        LogRecoverySummary("Restore", status, verifiedFiles, restoredFiles, 0, failedFiles);
        return new RecoveryResult(status, verifiedFiles, restoredFiles, 0, failedFiles);
    }

    private async Task<(BackupMetadataCatalog? Catalog, RecoveryResult? Result)> LoadCatalogAsync(
        RecoveryOptions options,
        CancellationToken ct)
    {
        if (!string.Equals(options.MetadataReference, "latest", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(options.MetadataReference))
        {
            await using var localStream = new FileStream(
                options.MetadataReference,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);
            using var reader = new StreamReader(localStream);
            return (await BackupMetadataCatalog.ParseAsync(reader, ct), null);
        }

        var metadataObjectPath = string.Equals(options.MetadataReference, "latest", StringComparison.OrdinalIgnoreCase)
            ? await backend.FindLatestMetadataAsync(jobName, ct)
            : options.MetadataReference;

        if (string.IsNullOrWhiteSpace(metadataObjectPath))
        {
            Log.Error("Keine Backup-Metadatendatei gefunden.");
            return (null, new RecoveryResult(RecoveryStatus.Failed, 0, 0, 0, 1));
        }

        var inspection = await backend.InspectObjectAsync(metadataObjectPath, ct);
        if (inspection.Availability == StorageObjectAvailability.Missing)
        {
            Log.Error("Metadatenobjekt wurde nicht gefunden: {MetadataObjectPath}", metadataObjectPath);
            return (null, new RecoveryResult(RecoveryStatus.Failed, 0, 0, 0, 1));
        }

        if (inspection.Availability != StorageObjectAvailability.Online)
        {
            var rehydrationRequests = new List<StorageObjectInfo>();
            QueueRehydrationIfEnabled(inspection, options, rehydrationRequests);
            await SubmitRehydrationRequestsAsync(rehydrationRequests, options, ct);
            Log.Warning("Metadatenobjekt ist noch nicht online verfügbar: {MetadataObjectPath}", metadataObjectPath);
            return (null, new RecoveryResult(RecoveryStatus.RehydrationPending, 0, 0, 1, 0));
        }

        var temporaryMetadata = Path.Combine(Path.GetTempPath(), $"hashbackup-metadata-{Guid.NewGuid():N}.csv");
        try
        {
            await backend.DownloadObjectAsync(metadataObjectPath, temporaryMetadata, ct);
            if (inspection.ContentHash != null &&
                !await FileMatchesHashAsync(temporaryMetadata, inspection.ContentHash, ct))
            {
                Log.Error("Heruntergeladene Metadatendatei hat einen abweichenden Content-Hash.");
                return (null, new RecoveryResult(RecoveryStatus.Failed, 0, 0, 0, 1));
            }

            await using var stream = new FileStream(
                temporaryMetadata,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);
            using var reader = new StreamReader(stream);
            return (await BackupMetadataCatalog.ParseAsync(reader, ct), null);
        }
        finally
        {
            File.Delete(temporaryMetadata);
        }
    }

    private static void QueueRehydrationIfEnabled(
        StorageObjectInfo inspection,
        RecoveryOptions options,
        ICollection<StorageObjectInfo> requests)
    {
        if (!options.Rehydrate)
        {
            Log.Warning(
                "Objekt {ObjectPath} ist {Availability}. Mit --rehydrate kann die kostenpflichtige Rehydration angefordert werden.",
                inspection.ObjectPath,
                inspection.Availability);
            return;
        }

        if (options.DryRun)
        {
            Log.Information(
                "[DRY RUN] Würde {ObjectPath} nach {TargetTier} mit Priorität {Priority} rehydrieren.",
                inspection.ObjectPath,
                options.RehydrateTier,
                options.RehydratePriority);
            return;
        }

        requests.Add(inspection);
    }

    private async Task SubmitRehydrationRequestsAsync(
        IReadOnlyCollection<StorageObjectInfo> requests,
        RecoveryOptions options,
        CancellationToken ct)
    {
        if (requests.Count == 0)
        {
            return;
        }

        await backend.RequestRehydrationAsync(
            requests,
            options.RehydrateTier,
            options.RehydratePriority,
            ct);
        Log.Information(
            "Rehydration für {ObjectCount} Objekte angefordert oder aktualisiert: Ziel={TargetTier}, Priorität={Priority}.",
            requests.Count,
            options.RehydrateTier,
            options.RehydratePriority);
    }

    private static bool ValidateStoredProperties(
        BackupMetadataEntry entry,
        StorageObjectInfo inspection,
        bool deepVerificationPlanned,
        out string? error)
    {
        if (inspection.Availability == StorageObjectAvailability.Missing)
        {
            error = "Objekt fehlt im Backup-Speicher.";
            return false;
        }

        if (inspection.Length != entry.Size)
        {
            error = $"Größe abweichend (erwartet {entry.Size}, vorhanden {inspection.Length?.ToString() ?? "unbekannt"}).";
            return false;
        }

        if (inspection.ContentHash == null)
        {
            if (deepVerificationPlanned)
            {
                error = null;
                return true;
            }

            error = "Storage enthält keinen prüfbaren Content-MD5; --deep ist erforderlich.";
            return false;
        }

        if (!string.Equals(inspection.ContentHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Content-MD5 abweichend (erwartet {entry.Hash}, vorhanden {inspection.ContentHash}).";
            return false;
        }

        error = null;
        return true;
    }

    private async Task<bool> DownloadAndValidateAsync(
        BackupMetadataEntry entry,
        string objectPath,
        string? destinationPath,
        string? destinationRoot,
        bool overwrite,
        CancellationToken ct)
    {
        var temporaryPath = destinationPath == null
            ? Path.Combine(Path.GetTempPath(), $"hashbackup-verify-{Guid.NewGuid():N}.tmp")
            : $"{destinationPath}.hashbackup-{Guid.NewGuid():N}.tmp";

        try
        {
            if (destinationPath != null)
            {
                _pathPolicy.EnsureSafeDestinationPath(destinationRoot!, destinationPath);
                if (File.Exists(destinationPath))
                {
                    if (await FileMatchesHashAsync(destinationPath, entry.Hash, ct))
                    {
                        Log.Information("Restore-Ziel ist bereits korrekt: {DestinationPath}", destinationPath);
                        return true;
                    }

                    if (!overwrite)
                    {
                        Log.Error("Restore-Ziel existiert mit abweichendem Inhalt; --overwrite fehlt: {DestinationPath}", destinationPath);
                        return false;
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            }

            await backend.DownloadObjectAsync(objectPath, temporaryPath, ct);
            var fileInfo = new FileInfo(temporaryPath);
            if (fileInfo.Length != entry.Size || !await FileMatchesHashAsync(temporaryPath, entry.Hash, ct))
            {
                Log.Error("Heruntergeladenes Objekt hat eine abweichende Größe oder Prüfsumme: {ObjectPath}", objectPath);
                return false;
            }

            if (destinationPath == null)
            {
                return true;
            }

            File.Move(temporaryPath, destinationPath, overwrite);
            if (entry.GetModifiedTimeUtc() is { } modifiedTimeUtc)
            {
                File.SetLastWriteTimeUtc(destinationPath, modifiedTimeUtc);
            }

            Log.Information("Wiederhergestellt: {DestinationPath}", destinationPath);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "Fehler beim Prüfen/Wiederherstellen von {ObjectPath}", objectPath);
            return false;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private bool RestoreSymbolicLink(BackupMetadataEntry entry, string destinationRoot, bool overwrite)
    {
        try
        {
            var destinationPath = _pathPolicy.ResolveDestinationPath(entry, destinationRoot);
            _pathPolicy.EnsureSafeDestinationPath(destinationRoot, destinationPath, allowFinalSymbolicLink: true);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            var target = entry.GetSymbolicLinkTarget();
            if (string.IsNullOrWhiteSpace(target) || string.Equals(target, "Ziel nicht verfügbar", StringComparison.Ordinal))
            {
                Log.Error("Symlink-Ziel ist nicht wiederherstellbar: {DestinationPath}", destinationPath);
                return false;
            }

            var existingInfo = new FileInfo(destinationPath);
            var existingTarget = existingInfo.LinkTarget;
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath) || existingTarget != null)
            {
                if (string.Equals(existingTarget, target, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!overwrite)
                {
                    Log.Error("Restore-Ziel für Symlink existiert; --overwrite fehlt: {DestinationPath}", destinationPath);
                    return false;
                }

                File.Delete(destinationPath);
            }

            File.CreateSymbolicLink(destinationPath, target);
            Log.Information("Symbolischer Link wiederhergestellt: {DestinationPath} -> {Target}", destinationPath, target);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Symbolischer Link konnte nicht wiederhergestellt werden: {FileName}", entry.FileName);
            return false;
        }
    }

    private bool RestoreLegacyEmptyFile(BackupMetadataEntry entry, string destinationRoot, bool overwrite)
    {
        var destinationPath = _pathPolicy.ResolveDestinationPath(entry, destinationRoot);
        var temporaryPath = $"{destinationPath}.hashbackup-{Guid.NewGuid():N}.tmp";
        try
        {
            _pathPolicy.EnsureSafeDestinationPath(destinationRoot, destinationPath);
            if (File.Exists(destinationPath))
            {
                if (new FileInfo(destinationPath).Length == 0)
                {
                    return true;
                }

                if (!overwrite)
                {
                    Log.Error("Restore-Ziel existiert mit Inhalt; --overwrite fehlt: {DestinationPath}", destinationPath);
                    return false;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using (File.Create(temporaryPath))
            {
            }

            File.Move(temporaryPath, destinationPath, overwrite);
            if (entry.GetModifiedTimeUtc() is { } modifiedTimeUtc)
            {
                File.SetLastWriteTimeUtc(destinationPath, modifiedTimeUtc);
            }

            Log.Information("Leere Legacy-Datei wiederhergestellt: {DestinationPath}", destinationPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Leere Legacy-Datei konnte nicht wiederhergestellt werden: {FileName}", entry.FileName);
            return false;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static async Task<bool> FileMatchesHashAsync(
        string filePath,
        string expectedHash,
        CancellationToken ct)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            useAsync: true);
        var actualHash = Convert.ToHexStringLower(await MD5.HashDataAsync(stream, ct));
        return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static RecoveryStatus DetermineStatus(int failedFiles, int pendingFiles) =>
        failedFiles > 0
            ? RecoveryStatus.Failed
            : pendingFiles > 0
                ? RecoveryStatus.RehydrationPending
                : RecoveryStatus.Success;

    private static void LogRecoverySummary(
        string operation,
        RecoveryStatus status,
        int verifiedFiles,
        int restoredFiles,
        int pendingFiles,
        int failedFiles)
    {
        Log.Information(
            "{Operation} abgeschlossen: Status={Status}, geprüft={VerifiedFiles}, wiederhergestellt={RestoredFiles}, Rehydration ausstehend={PendingFiles}, fehlgeschlagen={FailedFiles}",
            operation,
            status,
            verifiedFiles,
            restoredFiles,
            pendingFiles,
            failedFiles);
    }
}
