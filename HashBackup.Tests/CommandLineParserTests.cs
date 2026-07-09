using HashBackup.Commands;
using HashBackup.Storage;

namespace HashBackup.Tests;

/// <summary>
/// Schützt die bisherige CLI und beschreibt die additive Syntax für Backup,
/// Verify und Restore.
/// </summary>
public sealed class CommandLineParserTests
{
    [Fact]
    public void Parse_PreservesLegacyBackupInvocation()
    {
        var request = CommandLineParser.Parse(["config.ini", "-sm"]);

        Assert.Equal(HashBackupCommand.Backup, request.Command);
        Assert.Equal("config.ini", request.ConfigPath);
        Assert.Equal(["-sm"], request.ConfigArguments);
    }

    [Fact]
    public void Parse_ReadsRestoreOptionsWithoutPassingThemToBackupConfiguration()
    {
        var request = CommandLineParser.Parse(
        [
            "restore",
            "config.ini",
            "--metadata",
            "latest",
            "--destination",
            "/restore",
            "--rehydrate",
            "--rehydrate-tier",
            "cool",
            "--rehydrate-priority",
            "high",
            "--overwrite"
        ]);

        Assert.Equal(HashBackupCommand.Restore, request.Command);
        Assert.Equal("latest", request.MetadataReference);
        Assert.Equal("/restore", request.Destination);
        Assert.True(request.Rehydrate);
        Assert.Equal(OnlineAccessTier.Cool, request.RehydrateTier);
        Assert.Equal(ArchiveRehydratePriority.High, request.RehydratePriority);
        Assert.True(request.Overwrite);
        Assert.Empty(request.ConfigArguments);
    }
}
