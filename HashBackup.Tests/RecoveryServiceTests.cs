using HashBackup.Recovery;
using HashBackup.Storage;

namespace HashBackup.Tests;

/// <summary>
/// Prüft den vollständigen Restore-/Verify-Ablauf einschließlich Archive-Tier,
/// Rehydration, atomarer Veröffentlichung und Hashvalidierung.
/// </summary>
public sealed class RecoveryServiceTests : IDisposable
{
    private const string ContentHash = "9a0364b9e99bb480dd25e1f0284c8555";
    private const string ObjectPath = "9/a/0/9a0364b9e99bb480dd25e1f0284c8555.txt";
    private readonly string _temporaryDirectory =
        Path.Combine(Path.GetTempPath(), $"hashbackup-recovery-tests-{Guid.NewGuid():N}");

    public RecoveryServiceTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task VerifyAsync_AcceptsArchivedObjectFromTrustedPropertiesWithoutDownloading()
    {
        var metadataFile = await CreateMetadataAsync();
        var backend = new FakeReadableBackend();
        backend.Add(ObjectPath, "content", StorageObjectAvailability.Archived);
        var service = CreateService(backend);

        var result = await service.VerifyAsync(
            new RecoveryOptions(metadataFile, Destination: null, DeepVerify: false));

        Assert.Equal(RecoveryStatus.Success, result.Status);
        Assert.Equal(0, backend.DownloadCount);
        Assert.Equal(0, backend.RehydrationCount);
    }

    /// <summary>
    /// Legacy-Blobs besitzen teilweise kein vom Storage geliefertes Content-MD5.
    /// Eine flache Prüfung darf sie nach erfolgreicher Existenz-/Größenprüfung nicht
    /// als beschädigt melden, muss den fehlenden kryptografischen Nachweis aber
    /// separat ausweisen und darf das Archive-Objekt nicht herunterladen.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_ReportsLegacyObjectWithoutContentMd5AsStructurallyVerified()
    {
        var metadataFile = await CreateMetadataAsync();
        var backend = new FakeReadableBackend();
        backend.Add(
            ObjectPath,
            "content",
            StorageObjectAvailability.Archived,
            includeContentHash: false);
        var service = CreateService(backend);

        var result = await service.VerifyAsync(
            new RecoveryOptions(metadataFile, Destination: null, DeepVerify: false));

        Assert.Equal(RecoveryStatus.Success, result.Status);
        Assert.Equal(0, result.VerifiedFiles);
        Assert.Equal(1, result.StructurallyVerifiedFiles);
        Assert.Equal(0, result.FailedFiles);
        Assert.Equal(0, backend.DownloadCount);
    }

    /// <summary>
    /// Bei einer tiefen Prüfung wird ein fehlendes Storage-Content-MD5 nicht
    /// benötigt, weil HashBackup den heruntergeladenen Inhalt selbst validiert.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_DeeplyVerifiesOnlineObjectWithoutContentMd5()
    {
        var metadataFile = await CreateMetadataAsync();
        var backend = new FakeReadableBackend();
        backend.Add(
            ObjectPath,
            "content",
            StorageObjectAvailability.Online,
            includeContentHash: false);
        var service = CreateService(backend);

        var result = await service.VerifyAsync(
            new RecoveryOptions(metadataFile, Destination: null, DeepVerify: true));

        Assert.Equal(RecoveryStatus.Success, result.Status);
        Assert.Equal(1, result.VerifiedFiles);
        Assert.Equal(0, result.StructurallyVerifiedFiles);
        Assert.Equal(1, backend.DownloadCount);
    }

    /// <summary>
    /// Fehlendes Storage-Content-MD5 kann kostenfrei gegen die noch vorhandene
    /// lokale Quelldatei geprüft werden. Dabei darf kein Archive-Download erfolgen.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_VerifiesLegacyObjectAgainstLocalSourceWithoutDownloading()
    {
        var sourceDirectory = Path.Combine(_temporaryDirectory, "source-verified");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "photo.txt"), "content");
        var metadataFile = await CreateMetadataAsync(directory: sourceDirectory);
        var reportPath = Path.Combine(_temporaryDirectory, "legacy-report.csv");
        var backend = new FakeReadableBackend();
        backend.Add(ObjectPath, "content", StorageObjectAvailability.Archived, includeContentHash: false);
        var service = CreateService(backend, sourceDirectory);

        var result = await service.VerifyAsync(
            new RecoveryOptions(
                metadataFile,
                Destination: null,
                VerifySource: true,
                OnlyMissingContentMd5: true,
                ReportPath: reportPath));

        Assert.Equal(RecoveryStatus.Success, result.Status);
        Assert.Equal(1, result.LocalSourceVerifiedFiles);
        Assert.Equal(0, result.StructurallyVerifiedFiles);
        Assert.Equal(0, result.FailedFiles);
        Assert.Equal(0, backend.DownloadCount);
        var report = await File.ReadAllTextAsync(reportPath);
        var expectedSha256 = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("content")));
        Assert.Contains("LocalSourceVerified", report);
        Assert.Contains(expectedSha256, report);
    }

    /// <summary>
    /// Eine lokal veränderte Datei darf den fehlenden Storage-Nachweis nicht
    /// ersetzen und muss den Verify-Lauf fehlschlagen lassen.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_FailsSourceVerificationWhenLocalContentChanged()
    {
        var sourceDirectory = Path.Combine(_temporaryDirectory, "source-changed");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "photo.txt"), "changed");
        var metadataFile = await CreateMetadataAsync(directory: sourceDirectory);
        var backend = new FakeReadableBackend();
        backend.Add(ObjectPath, "content", StorageObjectAvailability.Archived, includeContentHash: false);
        var service = CreateService(backend, sourceDirectory);

        var result = await service.VerifyAsync(
            new RecoveryOptions(metadataFile, Destination: null, VerifySource: true));

        Assert.Equal(RecoveryStatus.Failed, result.Status);
        Assert.Equal(0, result.LocalSourceVerifiedFiles);
        Assert.Equal(1, result.FailedFiles);
        Assert.Equal(0, backend.DownloadCount);
    }

    /// <summary>
    /// Ein nach der Sicherung eingeschleuster Symlink darf die lokale Quellprüfung
    /// nicht aus der konfigurierten Quelle heraus umleiten.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_RejectsSymlinkDuringLocalSourceVerification()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var sourceDirectory = Path.Combine(_temporaryDirectory, "source-symlink");
        var outsideFile = Path.Combine(_temporaryDirectory, "outside.txt");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(outsideFile, "content");
        File.CreateSymbolicLink(Path.Combine(sourceDirectory, "photo.txt"), outsideFile);
        var metadataFile = await CreateMetadataAsync(directory: sourceDirectory);
        var backend = new FakeReadableBackend();
        backend.Add(ObjectPath, "content", StorageObjectAvailability.Archived, includeContentHash: false);
        var service = CreateService(backend, sourceDirectory);

        var result = await service.VerifyAsync(
            new RecoveryOptions(metadataFile, Destination: null, VerifySource: true));

        Assert.Equal(RecoveryStatus.Failed, result.Status);
        Assert.Equal(0, result.LocalSourceVerifiedFiles);
        Assert.Equal(1, result.FailedFiles);
        Assert.Equal(0, backend.DownloadCount);
    }

    /// <summary>
    /// Dateinamen aus den Metadaten dürfen beim Öffnen des CSV-Berichts in einer
    /// Tabellenkalkulation nicht als Formel ausgeführt werden.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_ReportNeutralizesSpreadsheetFormulaFilename()
    {
        var sourceDirectory = Path.Combine(_temporaryDirectory, "source-formula-report");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "=formula.txt"), "content");
        var metadataFile = await CreateMetadataAsync(directory: sourceDirectory, fileName: "=formula.txt");
        var reportPath = Path.Combine(_temporaryDirectory, "formula-report.csv");
        var backend = new FakeReadableBackend();
        backend.Add(ObjectPath, "content", StorageObjectAvailability.Archived, includeContentHash: false);
        var service = CreateService(backend, sourceDirectory);

        var result = await service.VerifyAsync(
            new RecoveryOptions(metadataFile, Destination: null, VerifySource: true, ReportPath: reportPath));

        Assert.Equal(RecoveryStatus.Success, result.Status);
        Assert.Contains("'=formula.txt", await File.ReadAllTextAsync(reportPath));
    }

    /// <summary>
    /// Der Legacy-Filter überspringt Objekte mit vorhandenem, passendem
    /// Storage-Content-MD5, damit eine gezielte Prüfung keine Archive-Downloads
    /// oder unnötigen lokalen Datei-I/O auslöst.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_LegacyFilterSkipsObjectWithContentMd5()
    {
        var metadataFile = await CreateMetadataAsync();
        var backend = new FakeReadableBackend();
        backend.Add(ObjectPath, "content", StorageObjectAvailability.Archived);
        var service = CreateService(backend);

        var result = await service.VerifyAsync(
            new RecoveryOptions(
                metadataFile,
                Destination: null,
                VerifySource: true,
                OnlyMissingContentMd5: true));

        Assert.Equal(RecoveryStatus.Success, result.Status);
        Assert.Equal(0, result.VerifiedFiles);
        Assert.Equal(0, result.LocalSourceVerifiedFiles);
        Assert.Equal(0, backend.DownloadCount);
    }

    [Fact]
    public async Task VerifyAsync_DeepVerificationRequestsRehydrationAndReturnsPending()
    {
        var metadataFile = await CreateMetadataAsync();
        var backend = new FakeReadableBackend();
        backend.Add(ObjectPath, "content", StorageObjectAvailability.Archived);
        var service = CreateService(backend);

        var result = await service.VerifyAsync(
            new RecoveryOptions(
                metadataFile,
                Destination: null,
                DeepVerify: true,
                Rehydrate: true,
                RehydrateTier: OnlineAccessTier.Cool,
                RehydratePriority: ArchiveRehydratePriority.Standard));

        Assert.Equal(RecoveryStatus.RehydrationPending, result.Status);
        Assert.Equal(1, backend.RehydrationCount);
        Assert.Equal(0, backend.DownloadCount);
    }

    [Fact]
    public async Task VerifyAsync_BatchesMultipleRehydrationRequests()
    {
        var metadataFile = Path.Combine(_temporaryDirectory, "metadata-batch.csv");
        await File.WriteAllTextAsync(
            metadataFile,
            """
            EOF

            Filename,Hash,Extension,Size,Modified Time,InQueue
            dir >> /data/bilder
            photo.txt,9a0364b9e99bb480dd25e1f0284c8555,.txt,7,134123456789000000,
            other.bin,795f3202b17cb6bc3d4b771d8c6c9eaf,.bin,5,134123456789000000,
            """);
        var backend = new FakeReadableBackend();
        backend.Add(ObjectPath, "content", StorageObjectAvailability.Archived);
        backend.Add(
            "7/9/5/795f3202b17cb6bc3d4b771d8c6c9eaf.bin",
            "other",
            StorageObjectAvailability.Archived);
        var service = CreateService(backend);

        var result = await service.VerifyAsync(
            new RecoveryOptions(metadataFile, Destination: null, DeepVerify: true, Rehydrate: true));

        Assert.Equal(RecoveryStatus.RehydrationPending, result.Status);
        Assert.Equal(2, backend.RehydrationCount);
        Assert.Equal(1, backend.RehydrationBatchCount);
    }

    [Fact]
    public async Task RestoreAsync_DoesNotCreatePartialRestoreWhileArchiveIsPending()
    {
        var metadataFile = await CreateMetadataAsync();
        var destination = Path.Combine(_temporaryDirectory, "restore-pending");
        var backend = new FakeReadableBackend();
        backend.Add(ObjectPath, "content", StorageObjectAvailability.Archived);
        var service = CreateService(backend);

        var result = await service.RestoreAsync(
            new RecoveryOptions(
                metadataFile,
                destination,
                Rehydrate: true));

        Assert.Equal(RecoveryStatus.RehydrationPending, result.Status);
        Assert.Equal(1, backend.RehydrationCount);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task RestoreAsync_DoesNotRehydrateWithoutExplicitFlag()
    {
        var metadataFile = await CreateMetadataAsync();
        var destination = Path.Combine(_temporaryDirectory, "restore-no-rehydrate");
        var backend = new FakeReadableBackend();
        backend.Add(ObjectPath, "content", StorageObjectAvailability.Archived);
        var service = CreateService(backend);

        var result = await service.RestoreAsync(new RecoveryOptions(metadataFile, destination));

        Assert.Equal(RecoveryStatus.RehydrationPending, result.Status);
        Assert.Equal(0, backend.RehydrationCount);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task RestoreAsync_DownloadsVerifiesAndPublishesOnlineObject()
    {
        var metadataFile = await CreateMetadataAsync();
        var destination = Path.Combine(_temporaryDirectory, "restore-online");
        var backend = new FakeReadableBackend();
        backend.Add(ObjectPath, "content", StorageObjectAvailability.Online);
        var service = CreateService(backend);

        var result = await service.RestoreAsync(new RecoveryOptions(metadataFile, destination));

        Assert.Equal(RecoveryStatus.Success, result.Status);
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(destination, "photo.txt")));
    }

    [Fact]
    public async Task RestoreAsync_RecreatesLegacyPythonEmptyFileWithoutStorageObject()
    {
        var metadataFile = Path.Combine(_temporaryDirectory, "metadata-legacy-empty.csv");
        await File.WriteAllTextAsync(
            metadataFile,
            """
            EOF

            Filename,Hash,Extension,Size,Modified Time,InQueue
            dir >> /data/bilder
            marker.empty,0,.empty,0,1750000000.0,
            """);
        var destination = Path.Combine(_temporaryDirectory, "restore-legacy-empty");
        var backend = new FakeReadableBackend();
        var service = CreateService(backend);

        var result = await service.RestoreAsync(new RecoveryOptions(metadataFile, destination));

        Assert.Equal(RecoveryStatus.Success, result.Status);
        Assert.Equal(0, new FileInfo(Path.Combine(destination, "marker.empty")).Length);
        Assert.Equal(0, backend.DownloadCount);
    }

    [Fact]
    public async Task RestoreAsync_UsesActualDeduplicatedObjectWhenExtensionDiffers()
    {
        var metadataFile = await CreateMetadataAsync(fileName: "photo.jpg", extension: ".jpg");
        var destination = Path.Combine(_temporaryDirectory, "restore-deduplicated");
        var backend = new FakeReadableBackend();
        backend.Add("9/a/0/9a0364b9e99bb480dd25e1f0284c8555.txt", "content", StorageObjectAvailability.Online);
        var service = CreateService(backend);

        var result = await service.RestoreAsync(new RecoveryOptions(metadataFile, destination));

        Assert.Equal(RecoveryStatus.Success, result.Status);
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(destination, "photo.jpg")));
    }

    [Fact]
    public async Task RestoreAsync_DoesNotPublishCorruptedDownload()
    {
        var metadataFile = await CreateMetadataAsync();
        var destination = Path.Combine(_temporaryDirectory, "restore-corrupt");
        var backend = new FakeReadableBackend();
        backend.Add(
            ObjectPath,
            "corrupt",
            StorageObjectAvailability.Online,
            advertisedHash: ContentHash,
            advertisedLength: 7);
        var service = CreateService(backend);

        var result = await service.RestoreAsync(new RecoveryOptions(metadataFile, destination));

        Assert.Equal(RecoveryStatus.Failed, result.Status);
        Assert.False(File.Exists(Path.Combine(destination, "photo.txt")));
    }

    [Fact]
    public async Task RestoreAsync_RejectsSymlinkedDirectoryBelowDestination()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var metadataFile = await CreateMetadataAsync(directory: "/data/bilder/album");
        var destination = Path.Combine(_temporaryDirectory, "restore-symlink");
        var outside = Path.Combine(_temporaryDirectory, "outside");
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(destination, "album"), outside);
        var backend = new FakeReadableBackend();
        backend.Add(ObjectPath, "content", StorageObjectAvailability.Online);
        var service = CreateService(backend);

        var result = await service.RestoreAsync(new RecoveryOptions(metadataFile, destination));

        Assert.Equal(RecoveryStatus.Failed, result.Status);
        Assert.False(File.Exists(Path.Combine(outside, "photo.txt")));
    }

    [Fact]
    public async Task RestoreAsync_RejectsSymbolicLinkAsDestinationRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var metadataFile = await CreateMetadataAsync();
        var destination = Path.Combine(_temporaryDirectory, "restore-root-link");
        var outside = Path.Combine(_temporaryDirectory, "outside-root-link");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(destination, outside);
        var backend = new FakeReadableBackend();
        backend.Add(ObjectPath, "content", StorageObjectAvailability.Online);
        var service = CreateService(backend);

        var result = await service.RestoreAsync(new RecoveryOptions(metadataFile, destination));

        Assert.Equal(RecoveryStatus.Failed, result.Status);
        Assert.False(File.Exists(Path.Combine(outside, "photo.txt")));
    }

    private RecoveryService CreateService(FakeReadableBackend backend, string sourceFolder = "/data/bilder") =>
        new(backend, targetDirectoryDepth: 3, sourceFolders: [sourceFolder]);

    private async Task<string> CreateMetadataAsync(
        string directory = "/data/bilder",
        string fileName = "photo.txt",
        string extension = ".txt")
    {
        var metadataFile = Path.Combine(_temporaryDirectory, $"metadata-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(
            metadataFile,
            $$"""
            EOF

            Filename,Hash,Extension,Size,Modified Time,InQueue
            dir >> {{directory}}
            {{fileName}},9a0364b9e99bb480dd25e1f0284c8555,{{extension}},7,134123456789000000,
            """);
        return metadataFile;
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private sealed class FakeReadableBackend : IReadableStorageBackend
    {
        private readonly Dictionary<string, FakeObject> _objects = new(StringComparer.Ordinal);

        public int DownloadCount { get; private set; }

        public int RehydrationCount { get; private set; }

        public int RehydrationBatchCount { get; private set; }

        public void Add(
            string objectPath,
            string content,
            StorageObjectAvailability availability,
            string? advertisedHash = null,
            long? advertisedLength = null,
            bool includeContentHash = true)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            _objects[objectPath] = new FakeObject(
                bytes,
                availability,
                includeContentHash
                    ? advertisedHash ?? Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(bytes))
                    : null,
                advertisedLength ?? bytes.LongLength);
        }

        public Task<string?> FindLatestMetadataAsync(string jobName, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<StorageObjectInfo> InspectObjectAsync(string objectPath, CancellationToken ct = default)
        {
            if (!_objects.TryGetValue(objectPath, out var storedObject))
            {
                return Task.FromResult(StorageObjectInfo.Missing(objectPath));
            }

            return Task.FromResult(
                new StorageObjectInfo(
                    objectPath,
                    storedObject.Availability,
                    storedObject.Length,
                    storedObject.Hash,
                    storedObject.Availability == StorageObjectAvailability.Archived ? "Archive" : "Cool",
                    ArchiveStatus: null));
        }

        public Task<IReadOnlyDictionary<string, StorageObjectInfo>> IndexContentObjectsAsync(
            IEnumerable<string> expectedHashes,
            CancellationToken ct = default)
        {
            var expected = expectedHashes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = _objects
                .Where(item => expected.Contains(Path.GetFileNameWithoutExtension(item.Key)))
                .ToDictionary(
                    item => Path.GetFileNameWithoutExtension(item.Key),
                    item => new StorageObjectInfo(
                        item.Key,
                        item.Value.Availability,
                        item.Value.Length,
                        item.Value.Hash,
                        item.Value.Availability == StorageObjectAvailability.Archived ? "Archive" : "Cool",
                        ArchiveStatus: null),
                    StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<IReadOnlyDictionary<string, StorageObjectInfo>>(result);
        }

        public async Task DownloadObjectAsync(string objectPath, string destinationPath, CancellationToken ct = default)
        {
            DownloadCount++;
            await File.WriteAllBytesAsync(destinationPath, _objects[objectPath].Content, ct);
        }

        public Task RequestRehydrationAsync(
            IReadOnlyCollection<StorageObjectInfo> objects,
            OnlineAccessTier targetTier,
            ArchiveRehydratePriority priority,
            CancellationToken ct = default)
        {
            RehydrationCount += objects.Count;
            RehydrationBatchCount++;
            return Task.CompletedTask;
        }

        private sealed record FakeObject(
            byte[] Content,
            StorageObjectAvailability Availability,
            string? Hash,
            long Length);
    }
}
