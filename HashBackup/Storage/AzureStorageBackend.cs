using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Core;
using Azure.Storage;

namespace HashBackup.Storage
{
    public class AzureStorageBackend : IStorageBackend
    {
        private readonly BlobContainerClient _containerClient;
        private static readonly TimeSpan DefaultUploadTimeout = TimeSpan.FromMinutes(30);
        private readonly string _accountKey;

        public AzureStorageBackend(string accountName, string accountKey, string container)
        {
            _accountKey = accountKey;
            
            // Angepasste Client-Optionen für größere Dateien
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
                clientOptions
            );
            _containerClient = blobServiceClient.GetBlobContainerClient(container);
            
            // Registriere den Schlüssel sofort beim Erstellen der Instanz
            RegisterSensitiveData();
        }
        
        /// <summary>
        /// Registriert sensible Daten dieses Backends, die in Logs und Ausgaben maskiert werden sollen
        /// </summary>
        public void RegisterSensitiveData()
        {
            // Azure Storage Key als Secret registrieren
            Utils.SensitiveDataManager.RegisterSecret(_accountKey);
            
            Log.Debug("Azure Storage Backend: Secret-Daten registriert");
        }

        public async Task<Dictionary<string, long>> FetchHashesAsync(CancellationToken ct = default)
        {
            var hashes = new Dictionary<string, long>();
            await foreach (var blob in _containerClient.GetBlobsAsync(cancellationToken: ct))
            {
                try
                {
                    if (blob.Properties.ContentHash is { Length: > 0 } && blob.Properties.ContentLength > 0)
                    {
                        var hashHex = Convert.ToHexStringLower(blob.Properties.ContentHash);
                        hashes[hashHex] = blob.Properties.ContentLength ?? 0;
                    }
                    else if (blob.Properties.ContentLength > 0)
                    {
                        var name = Path.GetFileNameWithoutExtension(blob.Name);
                        hashes[name] = blob.Properties.ContentLength ?? 0;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Fehler beim Verarbeiten von Blob {BlobName}", blob.Name);
                }
            }
            Log.Information("{Count} Hashes aus Azure-Container geladen", hashes.Count);
            return hashes;
        }

        public async Task<bool> UploadToDestinationAsync(string filePath, string destinationPath, string fileHash, bool isImportant = false, CancellationToken ct = default)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var blobClient = _containerClient.GetBlobClient(destinationPath);
                await using var fileStream = File.OpenRead(filePath);
                
                var options = new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders
                    {
                        ContentHash = StringToByteArray(fileHash)
                    },
                    // Optimierte Übertragungsoptionen für große Dateien
                    TransferOptions = new StorageTransferOptions
                    {
                        // Für große Dateien wie Videos: mehr Parallele Verbindungen
                        MaximumConcurrency = fileInfo.Length > 100 * 1024 * 1024 ? 8 : 4,
                        // Bei großen Dateien können wir größere Blöcke verwenden
                        MaximumTransferSize = fileInfo.Length > 100 * 1024 * 1024 ? 8 * 1024 * 1024 : 4 * 1024 * 1024
                    }
                };
                
                // Bei wichtigen Dateien (wie Metadaten) explizit Hot/Cool Tier statt Archive verwenden
                if (isImportant)
                {
                    options.AccessTier = AccessTier.Cold;
                    Log.Information("Wichtige Datei wird im Cold-Tier hochgeladen: {DestinationPath}", destinationPath);
                }
                else if (fileInfo.Length > 1024 * 1024 * 1024) // Dateien >1GB
                {
                    Log.Information("Große Datei ({Size:N2} MB) wird hochgeladen: {FilePath}", 
                        fileInfo.Length / (1024.0 * 1024.0), filePath);
                }
                
                await blobClient.UploadAsync(fileStream, options, ct);
                Log.Debug("Hochgeladen: {FilePath} ({FileHash})", filePath, fileHash);
                return true;
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobAlreadyExists)
            {
                Log.Information("{FilePath} ({FileHash}) existiert bereits", filePath, fileHash);
                return true;
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == "BlobArchived")
            {
                Log.Information("{FilePath} ({FileHash}) existiert bereits (archiviert)", filePath, fileHash);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Fehler beim Hochladen von {FilePath} nach {DestinationPath}", filePath, destinationPath);
                return false;
            }
        }

        private static byte[] StringToByteArray(string hex)
        {
            var numberChars = hex.Length;
            var bytes = new byte[numberChars / 2];
            for (var i = 0; i < numberChars; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            return bytes;
        }
    }
}
