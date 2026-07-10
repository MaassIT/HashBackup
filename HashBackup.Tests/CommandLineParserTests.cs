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

    /// <summary>
    /// Die lokale Quellprüfung und der Bericht sind additive Verify-Optionen und
    /// dürfen nicht als Konfigurationsüberschreibungen weitergereicht werden.
    /// </summary>
    [Fact]
    public void Parse_ReadsSourceVerificationAndReportOptions()
    {
        var request = CommandLineParser.Parse(
        [
            "verify",
            "config.ini",
            "--verify-source",
            "--only-missing-content-md5",
            "--report",
            "/tmp/legacy-report.csv"
        ]);

        Assert.True(request.VerifySource);
        Assert.True(request.OnlyMissingContentMd5);
        Assert.Equal("/tmp/legacy-report.csv", request.ReportPath);
        Assert.Empty(request.ConfigArguments);
    }

    /// <summary>
    /// Eine lokale Quellprüfung darf nicht versehentlich mit Archive-Download
    /// oder Rehydration kombiniert werden, weil dies die Kostenfreiheit und die
    /// Bedeutung des Nachweises unklar machen würde.
    /// </summary>
    [Fact]
    public void Parse_RejectsSourceVerificationCombinedWithDeepVerification()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CommandLineParser.Parse(["verify", "config.ini", "--verify-source", "--deep"]));

        Assert.Contains("nicht mit --deep", exception.Message, StringComparison.Ordinal);
    }
}
