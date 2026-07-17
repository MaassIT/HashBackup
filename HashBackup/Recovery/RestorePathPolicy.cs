namespace HashBackup.Recovery;

/// <summary>
/// Kapselt die plattformübergreifende Zuordnung historischer Quellpfade in ein
/// isoliertes Restore-Ziel und schützt alle darunterliegenden Pfadkomponenten.
/// </summary>
internal sealed class RestorePathPolicy(IReadOnlyList<string> sourceFolders)
{
    public string ResolveDestinationPath(BackupMetadataEntry entry, string destinationRoot)
    {
        var normalizedDestination = Path.GetFullPath(destinationRoot);
        var entryPath = Path.GetFullPath(Path.Combine(entry.DirectoryPath, entry.FileName));
        var configuredSources = sourceFolders
            .Select(Path.GetFullPath)
            .OrderByDescending(source => source.Length)
            .ToList();
        var matchingSource = configuredSources.FirstOrDefault(source => IsContainedPath(source, entryPath));

        string relativePath;
        if (matchingSource != null)
        {
            relativePath = Path.GetRelativePath(matchingSource, entryPath);
            if (configuredSources.Count > 1)
            {
                var sourceIndex = configuredSources.IndexOf(matchingSource) + 1;
                var sourceName = SanitizePathSegment(new DirectoryInfo(matchingSource).Name);
                relativePath = Path.Combine($"source-{sourceIndex}-{sourceName}", relativePath);
            }
        }
        else
        {
            var safeOriginalPath = entryPath
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(':', '_');
            relativePath = Path.Combine("_unmapped", safeOriginalPath);
        }

        var candidate = Path.GetFullPath(Path.Combine(normalizedDestination, relativePath));
        if (!IsContainedPath(normalizedDestination, candidate))
        {
            throw new InvalidDataException($"Restore-Pfad verlässt das Zielverzeichnis: {entry.FileName}");
        }

        return candidate;
    }

    public void EnsureSafeDestinationPath(
        string destinationRoot,
        string destinationPath,
        bool allowFinalSymbolicLink = false)
    {
        var normalizedRoot = Path.GetFullPath(destinationRoot);
        var normalizedDestination = Path.GetFullPath(destinationPath);
        if (!IsContainedPath(normalizedRoot, normalizedDestination))
        {
            throw new InvalidDataException($"Restore-Pfad verlässt das Zielverzeichnis: {destinationPath}");
        }

        var relativePath = Path.GetRelativePath(normalizedRoot, normalizedDestination);
        var currentPath = normalizedRoot;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
            {
                continue;
            }

            if (File.GetAttributes(currentPath).HasFlag(FileAttributes.ReparsePoint) &&
                !(allowFinalSymbolicLink && string.Equals(currentPath, normalizedDestination, StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"Restore-Pfad enthält einen symbolischen Link: {currentPath}");
            }
        }
    }

    public bool PrepareDestinationRoot(string destinationRoot)
    {
        var normalizedRoot = Path.GetFullPath(destinationRoot);
        try
        {
            var directoryInfo = new DirectoryInfo(normalizedRoot);
            if (directoryInfo.LinkTarget != null ||
                (directoryInfo.Exists && directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                Log.Error("Restore-Zielverzeichnis darf kein symbolischer Link sein: {DestinationRoot}", normalizedRoot);
                return false;
            }

            if (File.Exists(normalizedRoot) && !Directory.Exists(normalizedRoot))
            {
                Log.Error("Restore-Ziel ist eine Datei und kein Verzeichnis: {DestinationRoot}", normalizedRoot);
                return false;
            }

            if (!directoryInfo.Exists)
            {
                if (OperatingSystem.IsWindows())
                {
                    Directory.CreateDirectory(normalizedRoot);
                }
                else
                {
                    Directory.CreateDirectory(
                        normalizedRoot,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

                directoryInfo.Refresh();
            }

            // Recheck after creation closes the common race where a link is inserted
            // between the first inspection and Directory.CreateDirectory.
            if (directoryInfo.LinkTarget != null || directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Log.Error("Restore-Zielverzeichnis wurde auf einen symbolischen Link umgeleitet: {DestinationRoot}", normalizedRoot);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Restore-Zielverzeichnis konnte nicht sicher vorbereitet werden: {DestinationRoot}", normalizedRoot);
            return false;
        }
    }

    private static bool IsContainedPath(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "root" : sanitized;
    }
}
