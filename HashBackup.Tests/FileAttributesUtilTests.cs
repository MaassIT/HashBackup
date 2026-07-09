using HashBackup.Utils;

namespace HashBackup.Tests;

/// <summary>
/// Prüft die plattformübergreifende xattr-/ADS-Abstraktion, auf der die
/// inkrementelle Backup-Erkennung basiert.
/// </summary>
public sealed class FileAttributesUtilTests : IDisposable
{
    private readonly string _filePath = Path.Combine(
        Path.GetTempPath(),
        $"hashbackup-xattr-test-{Guid.NewGuid():N}.txt");

    [Fact]
    public void GetAttribute_ReturnsValuesLargerThanLegacyBuffer()
    {
        File.WriteAllText(_filePath, "test");
        var expected = new string('x', 256);

        FileAttributesUtil.SetAttribute(_filePath, "user.hashbackup_test_value", expected);
        FileAttributesUtil.ClearCache();

        Assert.Equal(expected, FileAttributesUtil.GetAttribute(_filePath, "user.hashbackup_test_value"));
    }

    public void Dispose()
    {
        FileAttributesUtil.ClearCache();
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }
}
