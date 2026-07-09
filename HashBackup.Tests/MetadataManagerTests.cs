using HashBackup.Services;

namespace HashBackup.Tests;

/// <summary>
/// Verifiziert, dass die Metadatendatei mit dem historischen Python-CSV-Format
/// kompatibel bleibt und Sonderzeichen in Dateinamen korrekt behandelt.
/// </summary>
public sealed class MetadataManagerTests : IDisposable
{
    private readonly string _temporaryDirectory =
        Path.Combine(Path.GetTempPath(), $"hashbackup-metadata-tests-{Guid.NewGuid():N}");

    public MetadataManagerTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task GenerateMetadataCsvAsync_QuotesFilenameContainingComma()
    {
        var filePath = Path.Combine(_temporaryDirectory, "report, final.txt");
        await File.WriteAllTextAsync(filePath, "content");
        var fileInfo = new FileInfo(filePath);
        var manager = new MetadataManager(
            Path.Combine(_temporaryDirectory, "metadata.csv"),
            configDoku: [],
            jobName: "test",
            dryRun: true);

        var lines = await manager.GenerateMetadataCsvAsync(
            new Dictionary<string, (FileInfo Info, string Hash, bool UploadRequired)>
            {
                [filePath] = (fileInfo, "9a0364b9e99bb480dd25e1f0284c8555", false)
            });

        Assert.Contains(
            lines,
            line => line.StartsWith("\"report, final.txt\",", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }
}
