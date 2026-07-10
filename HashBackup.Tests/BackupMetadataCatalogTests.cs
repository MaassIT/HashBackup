using HashBackup.Recovery;

namespace HashBackup.Tests;

/// <summary>
/// Stellt sicher, dass Restore und Verify sowohl das etablierte Metadatenformat
/// als auch korrekt gequotete CSV-Felder zuverlässig lesen.
/// </summary>
public sealed class BackupMetadataCatalogTests
{
    [Fact]
    public async Task ParseAsync_ReadsDirectoryMarkersAndQuotedFilenames()
    {
        const string metadata = """
            Backup ausgeführt am: 2026-07-10 12:00:00
            EOF

            Filename,Hash,Extension,Size,Modified Time,InQueue
            dir >> /data/bilder
            "report, final.txt",9a0364b9e99bb480dd25e1f0284c8555,.txt,7,134123456789000000,
            """;

        var catalog = await BackupMetadataCatalog.ParseAsync(new StringReader(metadata));

        var entry = Assert.Single(catalog.Entries);
        Assert.Equal("/data/bilder", entry.DirectoryPath);
        Assert.Equal("report, final.txt", entry.FileName);
        Assert.Equal("9/a/0/9a0364b9e99bb480dd25e1f0284c8555.txt", entry.GetObjectPath(targetDirectoryDepth: 3));
    }

    [Fact]
    public async Task ParseAsync_RejectsFilenameTraversal()
    {
        const string metadata = """
            EOF

            Filename,Hash,Extension,Size,Modified Time,InQueue
            dir >> /data/bilder
            ../escape.txt,9a0364b9e99bb480dd25e1f0284c8555,.txt,7,134123456789000000,
            """;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => BackupMetadataCatalog.ParseAsync(new StringReader(metadata)));
    }

    [Fact]
    public async Task ParseAsync_RecoversLegacyUnquotedFilenameContainingComma()
    {
        // Older C# metadata wrote raw filenames. Recover the record from its five
        // stable trailing columns so existing production catalogs remain restorable.
        const string metadata = """
            EOF

            Filename,Hash,Extension,Size,Modified Time,InQueue
            dir >> /data/bilder
            Essen, Sommer.jpg,9a0364b9e99bb480dd25e1f0284c8555,.jpg,7,134123456789000000,
            """;

        var catalog = await BackupMetadataCatalog.ParseAsync(new StringReader(metadata));

        Assert.Equal("Essen, Sommer.jpg", Assert.Single(catalog.Entries).FileName);
    }
}
