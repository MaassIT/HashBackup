using HashBackup.Storage;

namespace HashBackup.Tests;

/// <summary>
/// Stellt sicher, dass ein manipuliertes Zielsegment keine Dateien außerhalb des
/// konfigurierten lokalen Backup-Verzeichnisses schreiben kann.
/// </summary>
public sealed class LocalStorageBackendSecurityTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"hashbackup-tests-{Guid.NewGuid():N}");

    public LocalStorageBackendSecurityTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task UploadToDestinationAsync_RejectsPathTraversal()
    {
        var destinationRoot = Path.Combine(_temporaryDirectory, "backup");
        var sourceFile = Path.Combine(_temporaryDirectory, "source.txt");
        var escapedPath = Path.Combine(_temporaryDirectory, "escaped.txt");
        await File.WriteAllTextAsync(sourceFile, "test");

        var backend = new LocalStorageBackend(destinationRoot);
        var result = await backend.UploadToDestinationAsync(sourceFile, "../../escaped.txt", "098f6bcd4621d373cade4e832627b4f6");

        Assert.False(result.Success);
        Assert.False(File.Exists(escapedPath));
    }

    [Fact]
    public async Task UploadToDestinationAsync_RejectsCorruptedExistingContent()
    {
        // Content-addressed targets are immutable. An existing file whose bytes do not
        // match its hash must never be accepted as a successful backup.
        var destinationRoot = Path.Combine(_temporaryDirectory, "backup-corrupt");
        var destinationPath = "0/9/8/098f6bcd4621d373cade4e832627b4f6.txt";
        var existingFile = Path.Combine(destinationRoot, destinationPath);
        var sourceFile = Path.Combine(_temporaryDirectory, "source-valid.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(existingFile)!);
        await File.WriteAllTextAsync(existingFile, "corrupted");
        await File.WriteAllTextAsync(sourceFile, "test");

        var backend = new LocalStorageBackend(destinationRoot);
        var result = await backend.UploadToDestinationAsync(
            sourceFile,
            destinationPath,
            "098f6bcd4621d373cade4e832627b4f6");

        Assert.False(result.Success);
        Assert.Equal("corrupted", await File.ReadAllTextAsync(existingFile));
    }

    [Fact]
    public async Task FetchHashesAsync_ExcludesCorruptedContent()
    {
        // Safe mode must not trust a hash-looking filename when the stored bytes no longer
        // match it, otherwise the source would incorrectly be marked as backed up.
        var destinationRoot = Path.Combine(_temporaryDirectory, "backup-safe-mode");
        var existingFile = Path.Combine(
            destinationRoot,
            "0/9/8/098f6bcd4621d373cade4e832627b4f6.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(existingFile)!);
        await File.WriteAllTextAsync(existingFile, "corrupted");

        var backend = new LocalStorageBackend(destinationRoot);
        var hashes = await backend.FetchHashesAsync();

        Assert.DoesNotContain("098f6bcd4621d373cade4e832627b4f6", hashes.Keys);
    }

    [Fact]
    public async Task UploadToDestinationAsync_RejectsSymlinkedDestinationDirectory()
    {
        // Lexical path checks alone do not stop a symlink below the configured root from
        // redirecting writes into an arbitrary directory.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var destinationRoot = Path.Combine(_temporaryDirectory, "backup-symlink");
        var escapedRoot = Path.Combine(_temporaryDirectory, "outside");
        var sourceFile = Path.Combine(_temporaryDirectory, "source-symlink.txt");
        Directory.CreateDirectory(destinationRoot);
        Directory.CreateDirectory(escapedRoot);
        Directory.CreateSymbolicLink(Path.Combine(destinationRoot, "0"), escapedRoot);
        await File.WriteAllTextAsync(sourceFile, "test");

        var backend = new LocalStorageBackend(destinationRoot);
        var result = await backend.UploadToDestinationAsync(
            sourceFile,
            "0/9/8/098f6bcd4621d373cade4e832627b4f6.txt",
            "098f6bcd4621d373cade4e832627b4f6");

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(escapedRoot, "9/8/098f6bcd4621d373cade4e832627b4f6.txt")));
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }
}
