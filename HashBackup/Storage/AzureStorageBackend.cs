using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace HashBackup.Storage
{
    public class AzureStorageBackend : IStorageBackend
    {
        private readonly BlobContainerClient _containerClient;

        public AzureStorageBackend(string accountName, string accountKey, string container)
        {
            var blobServiceClient = new BlobServiceClient(
                new Uri($"https://{accountName}.blob.core.windows.net"),
                new Azure.Storage.StorageSharedKeyCredential(accountName, accountKey)
            );
            _containerClient = blobServiceClient.GetBlobContainerClient(container);
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
                var blobClient = _containerClient.GetBlobClient(destinationPath);
                await using var fileStream = File.OpenRead(filePath);
                var options = new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders
                    {
                        ContentHash = StringToByteArray(fileHash)
                    }
                };
                
                // Bei wichtigen Dateien (wie Metadaten) explizit Hot/Cool Tier statt Archive verwenden
                if (isImportant)
                {
                    options.AccessTier = AccessTier.Cold;
                    Log.Information("Wichtige Datei wird im Cold-Tier hochgeladen: {DestinationPath}", destinationPath);
                }
                
                await blobClient.UploadAsync(fileStream, options, ct);
                Log.Information("Hochgeladen: {DestinationPath}", filePath);
                return true;
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobAlreadyExists)
            {
                Log.Information("{DestinationPath} existiert bereits", destinationPath);
                return true;
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == "BlobArchived")
            {
                Log.Information("{DestinationPath} existiert bereits (archiviert)", destinationPath);
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
