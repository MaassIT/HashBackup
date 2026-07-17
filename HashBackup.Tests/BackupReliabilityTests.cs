using HashBackup.Services;
using HashBackup.Storage;
using HashBackup.Utils;

namespace HashBackup.Tests;

/// <summary>
/// Prüft, dass HashBackup bei dynamischen Quellen keine inkonsistenten Inhalte unter
/// einem veralteten Content-Hash speichert und auch leere Dateien sichert.
/// </summary>
public sealed class BackupReliabilityTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"hashbackup-tests-{Guid.NewGuid():N}");

    public BackupReliabilityTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task UploadFilesAsync_DoesNotRetry_WhenSourceChangedSinceHashing()
    {
        // A changed source must be deferred to the next complete backup run. Reusing the
        // original Content-MD5 would otherwise cause the same Azure Md5Mismatch repeatedly.
        var backend = new SourceChangedBackend();
        var filePath = CreateFile("changed.txt", "original content");
        var fileInfo = new FileInfo(filePath);
        var queue = new ConcurrentQueue<UploadWorkItem>();
        queue.Enqueue(new UploadWorkItem(
            filePath,
            "a/b/c/0123456789abcdef0123456789abcdef.txt",
            "0123456789abcdef0123456789abcdef",
            FileAttributesUtil.DateTimeToUnixTimestamp(fileInfo.LastWriteTimeUtc),
            fileInfo.Length));

        var coordinator = new UploadCoordinator(backend, parallelUploads: 1, maxRetries: 5, retryDelay: 0, dryRun: false, jobName: "test");

        await coordinator.UploadFilesAsync(queue);

        Assert.Equal(1, backend.UploadCallCount);
    }

    [Fact]
    public async Task UploadFilesAsync_PreservesConfiguredRetryCount()
    {
        // MAX_RETRIES has historically meant additional retries after the initial upload.
        // Keeping that semantic avoids silently reducing resilience for existing configs.
        var backend = new AlwaysFailingBackend();
        var filePath = CreateFile("retry.txt", "content");
        var fileInfo = new FileInfo(filePath);
        var queue = new ConcurrentQueue<UploadWorkItem>();
        queue.Enqueue(new UploadWorkItem(
            filePath,
            "9/a/0/9a0364b9e99bb480dd25e1f0284c8555.txt",
            "9a0364b9e99bb480dd25e1f0284c8555",
            FileAttributesUtil.DateTimeToUnixTimestamp(fileInfo.LastWriteTimeUtc),
            fileInfo.Length));

        var coordinator = new UploadCoordinator(
            backend,
            parallelUploads: 1,
            maxRetries: 2,
            retryDelay: 0,
            dryRun: false,
            jobName: "test");

        await coordinator.UploadFilesAsync(queue);

        Assert.Equal(3, backend.UploadCallCount);
        Assert.Equal(1, coordinator.GetFailedFileCount());
    }

    [Fact]
    public async Task UploadFilesAsync_DoesNotMarkChangedSourceVersionAsBackedUp()
    {
        // A source can change immediately after Azure accepted the originally hashed bytes.
        // In that case the newer mtime must not be marked as backed up.
        var backend = new SourceMutatingBackend();
        var filePath = CreateFile("changes-after-upload.txt", "original content");
        var fileInfo = new FileInfo(filePath);
        var originalHashMtime = FileAttributesUtil.DateTimeToUnixTimestamp(fileInfo.LastWriteTimeUtc);
        var queue = new ConcurrentQueue<UploadWorkItem>();
        queue.Enqueue(new UploadWorkItem(
            filePath,
            "a/b/c/0123456789abcdef0123456789abcdef.txt",
            "0123456789abcdef0123456789abcdef",
            originalHashMtime,
            fileInfo.Length));

        var coordinator = new UploadCoordinator(
            backend,
            parallelUploads: 1,
            maxRetries: 3,
            retryDelay: 0,
            dryRun: false,
            jobName: "test");

        await coordinator.UploadFilesAsync(queue);

        Assert.Equal(1, coordinator.GetFailedFileCount());
        Assert.Null(FileAttributesUtil.GetAttribute(filePath, "user.test_backup_mtime"));
    }

    [Fact]
    public async Task RunAsync_QueuesAndUploads_EmptyFile()
    {
        // Empty files carry semantic information (for example marker files) and must not be
        // silently omitted from a backup.
        var sourceDirectory = Path.Combine(_temporaryDirectory, "source");
        Directory.CreateDirectory(sourceDirectory);
        var emptyFile = Path.Combine(sourceDirectory, "marker.empty");
        await File.WriteAllBytesAsync(emptyFile, []);

        var backend = new RecordingBackend();
        var configuration = new BackupConfiguration
        {
            BackupType = "test",
            SourceFolders = [sourceDirectory],
            LockFilePath = Path.Combine(_temporaryDirectory, "lock"),
            MetadataFile = Path.Combine(_temporaryDirectory, "metadata.csv"),
            ParallelUploads = 1,
            SafeMode = false,
            DryRun = false,
            MaxRetries = 1,
            RetryDelay = 0,
            JobName = "test",
            TargetDirDepth = 3
        };

        await new BackupJob(backend, configuration).RunAsync();

        var upload = Assert.Single(backend.Uploads, upload => upload.FilePath == emptyFile);
        Assert.Equal(emptyFile, upload.FilePath);
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", upload.FileHash);
    }

    [Fact]
    public async Task RunAsync_DryRun_DoesNotWriteOrUploadMetadata()
    {
        // A dry run is used as a production preflight and must not alter either
        // the configured metadata file or its remote destination.
        var sourceDirectory = Path.Combine(_temporaryDirectory, "dry-run-source");
        var metadataFile = Path.Combine(_temporaryDirectory, "dry-run-metadata.csv");
        Directory.CreateDirectory(sourceDirectory);
        var sourceFile = Path.Combine(sourceDirectory, "file.txt");
        await File.WriteAllTextAsync(sourceFile, "content");

        var backend = new RecordingBackend();
        var configuration = new BackupConfiguration
        {
            BackupType = "test",
            SourceFolders = [sourceDirectory],
            LockFilePath = Path.Combine(_temporaryDirectory, "dry-run-lock"),
            MetadataFile = metadataFile,
            ParallelUploads = 1,
            SafeMode = false,
            DryRun = true,
            MaxRetries = 1,
            RetryDelay = 0,
            JobName = "test",
            TargetDirDepth = 3
        };

        await new BackupJob(backend, configuration).RunAsync();

        Assert.False(File.Exists(metadataFile));
        Assert.Empty(backend.Uploads);
        Assert.Null(FileAttributesUtil.GetAttribute(sourceFile, "user.md5_hash_value"));
    }

    [Fact]
    public async Task RunAsync_Throws_WhenMetadataUploadFails()
    {
        // The metadata file is required for a reliable restore. A failed metadata upload
        // must therefore make the complete backup run fail with a non-zero process result.
        var sourceDirectory = Path.Combine(_temporaryDirectory, "metadata-failure-source");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "file.txt"), "content");

        var configuration = new BackupConfiguration
        {
            BackupType = "test",
            SourceFolders = [sourceDirectory],
            LockFilePath = Path.Combine(_temporaryDirectory, "metadata-failure-lock"),
            MetadataFile = Path.Combine(_temporaryDirectory, "metadata-failure.csv"),
            ParallelUploads = 1,
            SafeMode = false,
            DryRun = false,
            MaxRetries = 1,
            RetryDelay = 0,
            JobName = "test",
            TargetDirDepth = 3
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new BackupJob(new MetadataFailingBackend(), configuration).RunAsync());
    }

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_temporaryDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private sealed class SourceChangedBackend : IStorageBackend
    {
        public int UploadCallCount { get; private set; }

        public Task<Dictionary<string, long>> FetchHashesAsync(CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, long>());

        public Task<UploadResult> UploadToDestinationAsync(string filePath, string destinationPath, string fileHash, bool isImportant = false, CancellationToken ct = default)
        {
            UploadCallCount++;
            return Task.FromResult(UploadResult.SourceChanged);
        }

        public void RegisterSensitiveData()
        {
        }
    }

    private sealed class AlwaysFailingBackend : IStorageBackend
    {
        public int UploadCallCount { get; private set; }

        public Task<Dictionary<string, long>> FetchHashesAsync(CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, long>());

        public Task<UploadResult> UploadToDestinationAsync(
            string filePath,
            string destinationPath,
            string fileHash,
            bool isImportant = false,
            CancellationToken ct = default)
        {
            UploadCallCount++;
            return Task.FromResult(UploadResult.Failed);
        }

        public void RegisterSensitiveData()
        {
        }
    }

    private sealed class RecordingBackend : IStorageBackend
    {
        public List<(string FilePath, string DestinationPath, string FileHash)> Uploads { get; } = [];

        public Task<Dictionary<string, long>> FetchHashesAsync(CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, long>());

        public Task<UploadResult> UploadToDestinationAsync(string filePath, string destinationPath, string fileHash, bool isImportant = false, CancellationToken ct = default)
        {
            Uploads.Add((filePath, destinationPath, fileHash));
            return Task.FromResult(UploadResult.Successful);
        }

        public void RegisterSensitiveData()
        {
        }
    }

    private sealed class SourceMutatingBackend : IStorageBackend
    {
        public Task<Dictionary<string, long>> FetchHashesAsync(CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, long>());

        public async Task<UploadResult> UploadToDestinationAsync(
            string filePath,
            string destinationPath,
            string fileHash,
            bool isImportant = false,
            CancellationToken ct = default)
        {
            await File.AppendAllTextAsync(filePath, " changed", ct);
            File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddSeconds(2));
            return UploadResult.Successful;
        }

        public void RegisterSensitiveData()
        {
        }
    }

    private sealed class MetadataFailingBackend : IStorageBackend
    {
        public Task<Dictionary<string, long>> FetchHashesAsync(CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, long>());

        public Task<UploadResult> UploadToDestinationAsync(
            string filePath,
            string destinationPath,
            string fileHash,
            bool isImportant = false,
            CancellationToken ct = default) =>
            Task.FromResult(isImportant ? UploadResult.Failed : UploadResult.Successful);

        public void RegisterSensitiveData()
        {
        }
    }
}
