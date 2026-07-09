namespace HashBackup.Storage;

/// <summary>
/// Speichert Content-adressierte Dateien in einem lokalen Zielverzeichnis.
/// </summary>
public sealed class LocalStorageBackend : IStorageBackend
{
    private readonly string _destinationRoot;

    public LocalStorageBackend(string path)
    {
        _destinationRoot = Path.GetFullPath(path);
    }

    public void RegisterSensitiveData()
    {
        Log.Debug("Lokales Storage Backend: Keine sensiblen Daten zu registrieren");
    }

    public async Task<Dictionary<string, long>> FetchHashesAsync(CancellationToken ct = default)
    {
        var hashes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(_destinationRoot))
        {
            return hashes;
        }

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var file in Directory.EnumerateFiles(_destinationRoot, "*", enumerationOptions))
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileNameWithoutExtension(file);

            if (!ContentHashValidator.IsMd5Hash(fileName))
            {
                continue;
            }

            try
            {
                var actualHash = await CalculateMd5Async(file, ct);
                if (!string.Equals(actualHash, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Error(
                        "Lokales Backup ist beschädigt und wird im Safe-Mode nicht als vorhanden gewertet: {FilePath}",
                        file);
                    continue;
                }

                hashes[fileName] = new FileInfo(file).Length;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Lokales Backup konnte im Safe-Mode nicht verifiziert werden: {FilePath}", file);
            }
        }

        return hashes;
    }

    public async Task<UploadResult> UploadToDestinationAsync(
        string filePath,
        string destinationPath,
        string fileHash,
        bool isImportant = false,
        CancellationToken ct = default)
    {
        if (!ContentHashValidator.IsMd5Hash(fileHash))
        {
            Log.Error("Ungültiger Content-MD5 für {FilePath}; lokale Sicherung wird nicht ausgeführt", filePath);
            return UploadResult.Failed;
        }

        string? temporaryDestination = null;

        try
        {
            var localDestination = ResolveDestinationPath(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(localDestination)!);

            if (File.Exists(localDestination))
            {
                return await VerifyExistingDestinationAsync(localDestination, fileHash, ct);
            }

            // Copy into the destination directory and publish atomically. A process crash or
            // source mutation must never leave a partial file at the content-addressed path.
            temporaryDestination = $"{localDestination}.tmp-{Guid.NewGuid():N}";
            await using (var sourceStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                useAsync: true))
            await using (var destinationStream = new FileStream(
                temporaryDestination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: true))
            {
                await sourceStream.CopyToAsync(destinationStream, ct);
                await destinationStream.FlushAsync(ct);
            }

            var copiedHash = await CalculateMd5Async(temporaryDestination, ct);
            if (!string.Equals(copiedHash, fileHash, StringComparison.OrdinalIgnoreCase))
            {
                var currentSourceHash = await CalculateMd5Async(filePath, ct);
                if (!string.Equals(currentSourceHash, fileHash, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning(
                        "Quelle wurde während der lokalen Sicherung geändert: {FilePath}",
                        filePath);
                    return UploadResult.SourceChanged;
                }

                Log.Error(
                    "Integritätsprüfung der lokalen Kopie fehlgeschlagen: {LocalDest}",
                    localDestination);
                return UploadResult.Failed;
            }

            File.Move(temporaryDestination, localDestination, overwrite: false);
            temporaryDestination = null;

            Log.Debug(
                isImportant ? "Wichtige Datei lokal gespeichert: {LocalDest}" : "Lokal gespeichert: {LocalDest}",
                localDestination);
            return await Task.FromResult(UploadResult.Successful);
        }
        catch (ArgumentException ex)
        {
            Log.Warning(ex, "Unsicherer lokaler Zielpfad wurde abgelehnt: {DestinationPath}", destinationPath);
            return UploadResult.Failed;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == 0x50)
        {
            // A concurrent worker/process may have published the same content after our
            // initial existence check. Accept it only after validating its bytes.
            var localDestination = ResolveDestinationPath(destinationPath);
            return await VerifyExistingDestinationAsync(localDestination, fileHash, ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim lokalen Kopieren von {FilePath} nach {DestinationPath}", filePath, destinationPath);
            return UploadResult.Failed;
        }
        finally
        {
            if (temporaryDestination != null)
            {
                try
                {
                    File.Delete(temporaryDestination);
                }
                catch (Exception cleanupException)
                {
                    Log.Warning(cleanupException, "Temporäre lokale Sicherungsdatei konnte nicht entfernt werden: {TemporaryDestination}", temporaryDestination);
                }
            }
        }
    }

    private static async Task<UploadResult> VerifyExistingDestinationAsync(
        string localDestination,
        string expectedHash,
        CancellationToken ct)
    {
        var existingHash = await CalculateMd5Async(localDestination, ct);
        if (string.Equals(existingHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            Log.Information("{LocalDestination} existiert bereits und wurde verifiziert", localDestination);
            return UploadResult.Successful;
        }

        Log.Error(
            "Vorhandenes lokales Backup ist beschädigt oder kollidiert mit dem erwarteten Hash: {LocalDestination}",
            localDestination);
        return UploadResult.Failed;
    }

    private static async Task<string> CalculateMd5Async(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            useAsync: true);
        return Convert.ToHexStringLower(await System.Security.Cryptography.MD5.HashDataAsync(stream, ct));
    }

    private string ResolveDestinationPath(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath) || Path.IsPathRooted(destinationPath))
        {
            throw new ArgumentException("Der Zielpfad muss relativ und nicht leer sein.", nameof(destinationPath));
        }

        var candidate = Path.GetFullPath(Path.Combine(_destinationRoot, destinationPath));
        var relativePath = Path.GetRelativePath(_destinationRoot, candidate);

        if (relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("Der Zielpfad verlässt das konfigurierte Backup-Verzeichnis.", nameof(destinationPath));
        }

        RejectSymbolicLinkSegments(relativePath, candidate, destinationPath);

        return candidate;
    }

    private void RejectSymbolicLinkSegments(
        string relativePath,
        string candidate,
        string originalDestinationPath)
    {
        // Path.GetFullPath only performs a lexical containment check. Reject links below
        // the configured root so an attacker cannot redirect generated hash directories
        // (or the final file) outside the backup tree.
        var currentPath = _destinationRoot;
        var relativeDirectory = Path.GetDirectoryName(relativePath);
        if (!string.IsNullOrEmpty(relativeDirectory))
        {
            foreach (var segment in relativeDirectory.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                currentPath = Path.Combine(currentPath, segment);
                if ((Directory.Exists(currentPath) || File.Exists(currentPath)) &&
                    File.GetAttributes(currentPath).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new ArgumentException(
                        "Der lokale Zielpfad enthält einen symbolischen Link.",
                        nameof(originalDestinationPath));
                }
            }
        }

        if ((File.Exists(candidate) || Directory.Exists(candidate)) &&
            File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ArgumentException(
                "Das lokale Sicherungsziel ist ein symbolischer Link.",
                nameof(originalDestinationPath));
        }
    }
}
