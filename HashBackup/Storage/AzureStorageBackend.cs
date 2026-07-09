using Azure;
using Azure.Core;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace HashBackup.Storage;

/// <summary>
/// Speichert Content-adressierte Dateien in Azure Blob Storage und lässt Azure die
/// Content-MD5 jeder Übertragung validieren.
/// </summary>
public sealed class AzureStorageBackend : IStorageBackend, IReadableStorageBackend
{
    private static readonly TimeSpan DefaultUploadTimeout = TimeSpan.FromMinutes(30);
    private readonly BlobContainerClient _containerClient;
    private readonly BlobBatchClient _batchClient;
    private readonly string _accountKey;
    private readonly AccessTier _dataAccessTier;

    public AzureStorageBackend(string accountName, string accountKey, string container, AccessTier dataAccessTier)
    {
        _accountKey = accountKey;
        _dataAccessTier = dataAccessTier;

        var clientOptions = new BlobClientOptions
        {
            Retry =
            {
                MaxRetries = 5,
                NetworkTimeout = DefaultUploadTimeout,
                Delay = TimeSpan.FromSeconds(4),
                MaxDelay = TimeSpan.FromMinutes(2),
                Mode = RetryMode.Exponential
            }
        };

        var blobServiceClient = new BlobServiceClient(
            new Uri($"https://{accountName}.blob.core.windows.net"),
            new StorageSharedKeyCredential(accountName, accountKey),
            clientOptions);

        _containerClient = blobServiceClient.GetBlobContainerClient(container);
        _batchClient = new BlobBatchClient(blobServiceClient);
        RegisterSensitiveData();
    }

    public void RegisterSensitiveData()
    {
        SensitiveDataManager.RegisterSecret(_accountKey);
        Log.Debug("Azure Storage Backend: Secret-Daten registriert");
    }

    public async Task<Dictionary<string, long>> FetchHashesAsync(CancellationToken ct = default)
    {
        var hashes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        await foreach (var blob in _containerClient.GetBlobsAsync(cancellationToken: ct))
        {
            try
            {
                // Only content-addressed data objects belong in the deduplication index.
                // Metadata blobs also carry Content-MD5 but do not exist at the restore path.
                var fileName = Path.GetFileNameWithoutExtension(blob.Name);
                if (!ContentHashValidator.IsMd5Hash(fileName))
                {
                    continue;
                }

                if (blob.Properties.ContentHash is { Length: > 0 })
                {
                    var storedHash = Convert.ToHexStringLower(blob.Properties.ContentHash);
                    if (!string.Equals(storedHash, fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Error(
                            "Azure-Blob besitzt einen abweichenden Content-MD5 und wird nicht als gültiges Backup gewertet: {BlobName}",
                            blob.Name);
                        continue;
                    }

                    hashes[fileName] = blob.Properties.ContentLength ?? 0;
                    continue;
                }

                // Alte, von HashBackup erzeugte Blobs ohne Content-MD5 werden anhand ihres
                // Dateinamens erkannt. Andere Blobs wie Metadaten werden bewusst ignoriert.
                hashes[fileName] = blob.Properties.ContentLength ?? 0;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Fehler beim Verarbeiten von Blob {BlobName}", blob.Name);
            }
        }

        Log.Information("{Count} Hashes aus Azure-Container geladen", hashes.Count);
        return hashes;
    }

    public async Task<string?> FindLatestMetadataAsync(string jobName, CancellationToken ct = default)
    {
        var prefix = $"metadata/{jobName}/";
        string? latest = null;
        await foreach (var blob in _containerClient.GetBlobsAsync(
                           BlobTraits.None,
                           BlobStates.None,
                           prefix,
                           ct))
        {
            if (!blob.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (latest == null || string.CompareOrdinal(blob.Name, latest) > 0)
            {
                latest = blob.Name;
            }
        }

        return latest;
    }

    public async Task<StorageObjectInfo> InspectObjectAsync(
        string objectPath,
        CancellationToken ct = default)
    {
        try
        {
            var properties = (await _containerClient.GetBlobClient(objectPath).GetPropertiesAsync(cancellationToken: ct)).Value;
            var accessTier = properties.AccessTier?.ToString();
            var archiveStatus = properties.ArchiveStatus?.ToString();
            var availability = GetAvailability(accessTier, archiveStatus);
            var contentHash = properties.ContentHash is { Length: > 0 }
                ? Convert.ToHexStringLower(properties.ContentHash)
                : null;

            return new StorageObjectInfo(
                objectPath,
                availability,
                properties.ContentLength,
                contentHash,
                accessTier,
                archiveStatus);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return StorageObjectInfo.Missing(objectPath);
        }
    }

    public async Task<IReadOnlyDictionary<string, StorageObjectInfo>> IndexContentObjectsAsync(
        IEnumerable<string> expectedHashes,
        CancellationToken ct = default)
    {
        var expected = expectedHashes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, StorageObjectInfo>(StringComparer.OrdinalIgnoreCase);
        if (expected.Count == 0)
        {
            return result;
        }

        await foreach (var blob in _containerClient.GetBlobsAsync(cancellationToken: ct))
        {
            var hash = Path.GetFileNameWithoutExtension(blob.Name);
            if (!expected.Contains(hash) || !ContentHashValidator.IsMd5Hash(hash))
            {
                continue;
            }

            var accessTier = blob.Properties.AccessTier?.ToString();
            var archiveStatus = blob.Properties.ArchiveStatus?.ToString();
            var contentHash = blob.Properties.ContentHash is { Length: > 0 }
                ? Convert.ToHexStringLower(blob.Properties.ContentHash)
                : null;
            var candidate = new StorageObjectInfo(
                blob.Name,
                GetAvailability(accessTier, archiveStatus),
                blob.Properties.ContentLength,
                contentHash,
                accessTier,
                archiveStatus);

            if (!result.TryGetValue(hash, out var existing) ||
                (!string.Equals(existing.ContentHash, hash, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(candidate.ContentHash, hash, StringComparison.OrdinalIgnoreCase)))
            {
                result[hash] = candidate;
            }
        }

        return result;
    }

    public async Task DownloadObjectAsync(
        string objectPath,
        string destinationPath,
        CancellationToken ct = default)
    {
        await _containerClient.GetBlobClient(objectPath).DownloadToAsync(destinationPath, ct);
    }

    public async Task RequestRehydrationAsync(
        IReadOnlyCollection<StorageObjectInfo> objects,
        OnlineAccessTier targetTier,
        ArchiveRehydratePriority priority,
        CancellationToken ct = default)
    {
        var azureTier = targetTier switch
        {
            OnlineAccessTier.Hot => AccessTier.Hot,
            OnlineAccessTier.Cool => AccessTier.Cool,
            OnlineAccessTier.Cold => AccessTier.Cold,
            _ => throw new ArgumentOutOfRangeException(nameof(targetTier), targetTier, null)
        };
        var azurePriority = priority switch
        {
            ArchiveRehydratePriority.Standard => RehydratePriority.Standard,
            ArchiveRehydratePriority.High => RehydratePriority.High,
            _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, null)
        };

        var blobUris = new List<Uri>();
        foreach (var inspection in objects)
        {
            if (inspection.Availability == StorageObjectAvailability.Missing)
            {
                throw new FileNotFoundException("Azure-Blob für Rehydration nicht gefunden.", inspection.ObjectPath);
            }

            if (inspection.Availability == StorageObjectAvailability.Online)
            {
                continue;
            }

            if (inspection.Availability == StorageObjectAvailability.Rehydrating &&
                !ArchiveStatusMatchesTier(inspection.ArchiveStatus, targetTier))
            {
                throw new InvalidOperationException(
                    $"Blob {inspection.ObjectPath} wird bereits in ein anderes Online-Tier rehydriert ({inspection.ArchiveStatus}).");
            }

            // Calling SetAccessTier again with High upgrades a pending Standard request.
            // Repeating Standard is unnecessary and would only add an extra transaction.
            if (inspection.Availability == StorageObjectAvailability.Rehydrating &&
                priority == ArchiveRehydratePriority.Standard)
            {
                continue;
            }

            blobUris.Add(_containerClient.GetBlobClient(inspection.ObjectPath).Uri);
        }

        // Azure Blob Batch accepts at most 256 subrequests per request.
        foreach (var batch in blobUris.Chunk(256))
        {
            var responses = await _batchClient.SetBlobsAccessTierAsync(
                batch,
                azureTier,
                azurePriority,
                ct);
            var failedResponses = responses.Where(response => response.Status >= 400).ToList();
            if (failedResponses.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{failedResponses.Count} von {responses.Length} Azure-Rehydrationsanfragen im Batch sind fehlgeschlagen.");
            }
        }
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
            Log.Error("Ungültiger Content-MD5 für {FilePath}; Upload wird nicht ausgeführt", filePath);
            return UploadResult.Failed;
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            var blobClient = _containerClient.GetBlobClient(destinationPath);
            await using var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                useAsync: true);

            var options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    // Azure validates this value against the bytes actually received. It must
                    // never be removed: a mismatch prevents a wrong payload under this hash.
                    ContentHash = Convert.FromHexString(fileHash)
                },
                TransferOptions = new StorageTransferOptions
                {
                    MaximumConcurrency = fileInfo.Length > 100 * 1024 * 1024 ? 8 : 4,
                    MaximumTransferSize = fileInfo.Length > 100 * 1024 * 1024 ? 8 * 1024 * 1024 : 4 * 1024 * 1024
                }
            };

            if (isImportant)
            {
                options.AccessTier = AccessTier.Cold;
                Log.Information("Wichtige Datei wird im Cold-Tier hochgeladen: {DestinationPath}", destinationPath);
            }
            else
            {
                options.AccessTier = _dataAccessTier;

                if (fileInfo.Length > 1024 * 1024 * 1024)
                {
                    Log.Information("Große Datei ({Size:N2} MB) wird hochgeladen: {FilePath}",
                        fileInfo.Length / (1024.0 * 1024.0), filePath);
                }
            }

            await blobClient.UploadAsync(fileStream, options, ct);
            Log.Debug("Hochgeladen: {FilePath} ({FileHash})", filePath, fileHash);
            return UploadResult.Successful;
        }
        catch (RequestFailedException ex) when (string.Equals(ex.ErrorCode, "Md5Mismatch", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleMd5MismatchAsync(filePath, fileHash, ex, ct);
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobAlreadyExists)
        {
            Log.Information("{FilePath} ({FileHash}) existiert bereits", filePath, fileHash);
            return UploadResult.Successful;
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == "BlobArchived")
        {
            Log.Information("{FilePath} ({FileHash}) existiert bereits (archiviert)", filePath, fileHash);
            return UploadResult.Successful;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Hochladen von {FilePath} nach {DestinationPath}", filePath, destinationPath);
            return UploadResult.Failed;
        }
    }

    private static async Task<UploadResult> HandleMd5MismatchAsync(
        string filePath,
        string expectedHash,
        RequestFailedException exception,
        CancellationToken ct)
    {
        try
        {
            await using var currentStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                useAsync: true);
            var currentHash = Convert.ToHexStringLower(
                await System.Security.Cryptography.MD5.HashDataAsync(currentStream, ct));

            if (!string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning(
                    "Quelle wurde nach der Hash-Berechnung geändert: {FilePath}. Der Upload wird aus Integritätsgründen erst im nächsten vollständigen Lauf wiederholt.",
                    filePath);
                return UploadResult.SourceChanged;
            }

            Log.Warning(
                exception,
                "Azure meldet einen MD5-Fehler für {FilePath}, obwohl die Quelle aktuell den erwarteten Hash hat. Der Upload darf regulär wiederholt werden.",
                filePath);
            return UploadResult.Failed;
        }
        catch (Exception hashException)
        {
            Log.Warning(
                hashException,
                "MD5-Fehler für {FilePath} konnte nicht gegen die aktuelle Quelle verifiziert werden. Der Upload darf regulär wiederholt werden.",
                filePath);
            return UploadResult.Failed;
        }
    }

    /// <summary>
    /// Converts the documented Azure configuration value into the Azure SDK tier type.
    /// </summary>
    public static AccessTier ParseAccessTier(string? configuredTier) =>
        configuredTier?.Trim().ToUpperInvariant() switch
        {
            "HOT" => AccessTier.Hot,
            "COOL" => AccessTier.Cool,
            "COLD" => AccessTier.Cold,
            "ARCHIVE" => AccessTier.Archive,
            _ => throw new ArgumentException(
                "AZURE:STORAGE_TIER muss Hot, Cool, Cold oder Archive sein.",
                nameof(configuredTier))
        };

    private static bool ArchiveStatusMatchesTier(string? archiveStatus, OnlineAccessTier targetTier) =>
        archiveStatus?.Contains(targetTier.ToString(), StringComparison.OrdinalIgnoreCase) == true;

    private static StorageObjectAvailability GetAvailability(string? accessTier, string? archiveStatus) =>
        !string.IsNullOrWhiteSpace(archiveStatus)
            ? StorageObjectAvailability.Rehydrating
            : string.Equals(accessTier, "Archive", StringComparison.OrdinalIgnoreCase)
                ? StorageObjectAvailability.Archived
                : StorageObjectAvailability.Online;
}
