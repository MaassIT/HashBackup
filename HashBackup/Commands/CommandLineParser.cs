namespace HashBackup.Commands;

/// <summary>
/// Unterstützte Betriebsarten. Die implizite Backup-Syntax ohne Befehlswort
/// bleibt aus Kompatibilitätsgründen erhalten.
/// </summary>
public enum HashBackupCommand
{
    Backup,
    Verify,
    Restore
}

/// <summary>
/// Normalisierte Kommandozeile für Backup-, Verify- und Restore-Läufe.
/// </summary>
public sealed record CommandLineRequest(
    HashBackupCommand Command,
    string ConfigPath,
    string[] ConfigArguments,
    string MetadataReference = "latest",
    string? Destination = null,
    bool DeepVerify = false,
    bool Rehydrate = false,
    OnlineAccessTier RehydrateTier = OnlineAccessTier.Cool,
    ArchiveRehydratePriority RehydratePriority = ArchiveRehydratePriority.Standard,
    bool Overwrite = false,
    bool DryRun = false);

/// <summary>
/// Parst additive Unterbefehle, ohne bestehende Aufrufe der Form
/// <c>HashBackup config.ini [Optionen]</c> zu verändern.
/// </summary>
public static class CommandLineParser
{
    public static CommandLineRequest Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("Eine Konfigurationsdatei ist erforderlich.");
        }

        var (command, configIndex) = ParseCommand(args[0]);
        if (args.Length <= configIndex || string.IsNullOrWhiteSpace(args[configIndex]))
        {
            throw new ArgumentException($"Für den Befehl {command.ToString().ToLowerInvariant()} ist eine Konfigurationsdatei erforderlich.");
        }

        var configPath = args[configIndex];
        var remainingArguments = args.Skip(configIndex + 1).ToArray();
        if (command == HashBackupCommand.Backup)
        {
            return new CommandLineRequest(command, configPath, remainingArguments);
        }

        return ParseRecoveryCommand(command, configPath, remainingArguments);
    }

    private static (HashBackupCommand Command, int ConfigIndex) ParseCommand(string firstArgument) =>
        firstArgument.ToLowerInvariant() switch
        {
            "backup" => (HashBackupCommand.Backup, 1),
            "verify" => (HashBackupCommand.Verify, 1),
            "restore" => (HashBackupCommand.Restore, 1),
            _ => (HashBackupCommand.Backup, 0)
        };

    private static CommandLineRequest ParseRecoveryCommand(
        HashBackupCommand command,
        string configPath,
        string[] args)
    {
        var metadataReference = "latest";
        string? destination = null;
        var deepVerify = false;
        var rehydrate = false;
        var overwrite = false;
        var dryRun = false;
        var rehydrateTier = OnlineAccessTier.Cool;
        var rehydratePriority = ArchiveRehydratePriority.Standard;
        var configArguments = new List<string>();

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument.ToLowerInvariant())
            {
                case "--metadata":
                case "-m":
                    metadataReference = ReadValue(args, ref index, argument);
                    break;
                case "--destination":
                case "-o":
                    destination = ReadValue(args, ref index, argument);
                    break;
                case "--deep":
                    deepVerify = true;
                    break;
                case "--rehydrate":
                    rehydrate = true;
                    break;
                case "--overwrite":
                    overwrite = true;
                    break;
                case "--dry-run":
                case "-d":
                    dryRun = true;
                    break;
                case "--rehydrate-tier":
                    rehydrateTier = ParseRehydrateTier(ReadValue(args, ref index, argument));
                    break;
                case "--rehydrate-priority":
                    rehydratePriority = ParseRehydratePriority(ReadValue(args, ref index, argument));
                    break;
                default:
                    configArguments.Add(argument);
                    break;
            }
        }

        if (command == HashBackupCommand.Restore && string.IsNullOrWhiteSpace(destination))
        {
            throw new ArgumentException("Der Restore-Befehl benötigt --destination <Verzeichnis>.");
        }

        return new CommandLineRequest(
            command,
            configPath,
            configArguments.ToArray(),
            metadataReference,
            destination,
            deepVerify,
            rehydrate,
            rehydrateTier,
            rehydratePriority,
            overwrite,
            dryRun);
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
        {
            throw new ArgumentException($"Für {option} fehlt ein Wert.");
        }

        index++;
        return args[index];
    }

    private static OnlineAccessTier ParseRehydrateTier(string value) =>
        value.ToLowerInvariant() switch
        {
            "hot" => OnlineAccessTier.Hot,
            "cool" => OnlineAccessTier.Cool,
            "cold" => OnlineAccessTier.Cold,
            _ => throw new ArgumentException("--rehydrate-tier muss hot, cool oder cold sein.")
        };

    private static ArchiveRehydratePriority ParseRehydratePriority(string value) =>
        value.ToLowerInvariant() switch
        {
            "standard" => ArchiveRehydratePriority.Standard,
            "high" => ArchiveRehydratePriority.High,
            _ => throw new ArgumentException("--rehydrate-priority muss standard oder high sein.")
        };
}
