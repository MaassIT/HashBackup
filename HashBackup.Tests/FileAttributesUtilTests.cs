using System.Runtime.Versioning;
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

    /// <summary>
    /// Git speichert lose Objekte auf macOS üblicherweise schreibgeschützt (0444).
    /// HashBackup muss seine xattrs trotzdem dauerhaft setzen können, ohne den
    /// ursprünglichen Dateimodus zu verändern.
    /// </summary>
    [MacOsFact]
    [SupportedOSPlatform("macos")]
    public void SetAttribute_PersistsValueOnReadOnlyMacOsFile_AndRestoresMode()
    {
        File.WriteAllText(_filePath, "test");
        const UnixFileMode readOnlyMode =
            UnixFileMode.UserRead |
            UnixFileMode.GroupRead |
            UnixFileMode.OtherRead;
        File.SetUnixFileMode(_filePath, readOnlyMode);

        FileAttributesUtil.SetAttribute(_filePath, "user.hashbackup_test_value", "persisted");
        FileAttributesUtil.ClearCache();

        Assert.Equal("persisted", FileAttributesUtil.GetAttribute(_filePath, "user.hashbackup_test_value"));
        Assert.Equal(readOnlyMode, File.GetUnixFileMode(_filePath));
    }

    /// <summary>
    /// Auch wenn der zweite xattr-Aufruf nach dem temporären chmod fehlschlägt,
    /// muss der ursprüngliche 0444-Modus wiederhergestellt werden.
    /// </summary>
    [MacOsFact]
    [SupportedOSPlatform("macos")]
    public void SetAttribute_RestoresReadOnlyMode_WhenRetryFails()
    {
        File.WriteAllText(_filePath, "test");
        const UnixFileMode readOnlyMode =
            UnixFileMode.UserRead |
            UnixFileMode.GroupRead |
            UnixFileMode.OtherRead;
        File.SetUnixFileMode(_filePath, readOnlyMode);

        // FinderInfo erwartet auf macOS eine feste binäre Struktur. Ein einzelnes
        // Byte führt nach dem 0444-Retry reproduzierbar zu ERANGE.
        FileAttributesUtil.SetAttribute(_filePath, "com.apple.FinderInfo", "x");

        Assert.Equal(readOnlyMode, File.GetUnixFileMode(_filePath));
    }

    /// <summary>
    /// xattrs eines Datei-Symlinks dürfen auf Unix-Systemen nicht versehentlich
    /// auf dessen Ziel geschrieben werden. Das Ziel kann außerhalb des
    /// Backup-Baums liegen.
    /// </summary>
    [UnixFact]
    public void SetAttribute_OnUnixSymlink_DoesNotModifyTarget()
    {
        File.WriteAllText(_filePath, "target");
        var symlinkPath = $"{_filePath}-link";
        File.CreateSymbolicLink(symlinkPath, _filePath);

        try
        {
            FileAttributesUtil.SetAttribute(symlinkPath, "user.hashbackup_test_value", "on-link");
            FileAttributesUtil.ClearCache();

            if (OperatingSystem.IsMacOS())
            {
                // Darwin erlaubt benutzerdefinierte xattrs direkt am Symlink.
                Assert.Equal("on-link", FileAttributesUtil.GetAttribute(symlinkPath, "user.hashbackup_test_value"));
            }

            Assert.Null(FileAttributesUtil.GetAttribute(_filePath, "user.hashbackup_test_value"));
        }
        finally
        {
            File.Delete(symlinkPath);
        }
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

/// <summary>
/// Markiert einen Test unter xUnit 2 sichtbar als übersprungen, wenn der
/// benötigte macOS-Kernelpfad auf dem Runner nicht verfügbar ist.
/// </summary>
public sealed class MacOsFactAttribute : FactAttribute
{
    public MacOsFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "Dieser xattr-Rechte-Test benötigt macOS.";
        }
    }
}

/// <summary>
/// Begrenzt Symlink-xattr-Tests auf die beiden unterstützten Unix-Plattformen.
/// </summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            Skip = "Dieser Symlink-xattr-Test benötigt macOS oder Linux.";
        }
    }
}
