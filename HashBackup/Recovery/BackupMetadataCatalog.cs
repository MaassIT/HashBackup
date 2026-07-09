using System.Globalization;
using System.Text;

namespace HashBackup.Recovery;

/// <summary>
/// Ein Eintrag aus der historisch kompatiblen Backup-Metadaten-CSV.
/// </summary>
public sealed record BackupMetadataEntry(
    string DirectoryPath,
    string FileName,
    string Hash,
    string Extension,
    long Size,
    string ModifiedTime)
{
    public bool IsSymbolicLink => Hash.StartsWith("SYM-", StringComparison.Ordinal);

    public bool IsLegacyEmptyFile => Hash == "0" && Size == 0;

    public string? GetObjectPath(int targetDirectoryDepth)
    {
        if (IsSymbolicLink || IsLegacyEmptyFile)
        {
            return null;
        }

        if (!ContentHashValidator.IsMd5Hash(Hash))
        {
            throw new InvalidDataException($"Ungültiger Content-Hash für {FileName}.");
        }

        var directory = string.Join('/', Hash.Take(Math.Min(targetDirectoryDepth, Hash.Length)));
        return $"{directory}/{Hash}{Extension}";
    }

    public string GetSymbolicLinkTarget()
    {
        if (!IsSymbolicLink)
        {
            throw new InvalidOperationException("Der Metadateneintrag ist kein symbolischer Link.");
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(Hash[4..]));
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"Ungültiges Symlink-Ziel für {FileName}.", ex);
        }
    }

    public DateTime? GetModifiedTimeUtc()
    {
        if (long.TryParse(ModifiedTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileTime) &&
            fileTime > 10_000_000_000)
        {
            try
            {
                return DateTime.FromFileTimeUtc(fileTime);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        if (double.TryParse(ModifiedTime, NumberStyles.Float, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            try
            {
                return DateTime.UnixEpoch.AddSeconds(unixSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }
}

/// <summary>
/// Parser für Metadaten aus dem C#- und dem historischen Python-Tool.
/// </summary>
public sealed class BackupMetadataCatalog
{
    private const string Header = "Filename,Hash,Extension,Size,Modified Time,InQueue";
    private const string DirectoryMarker = "dir >> ";

    private BackupMetadataCatalog(IReadOnlyList<BackupMetadataEntry> entries)
    {
        Entries = entries;
    }

    public IReadOnlyList<BackupMetadataEntry> Entries { get; }

    public static async Task<BackupMetadataCatalog> ParseAsync(
        TextReader reader,
        CancellationToken ct = default)
    {
        var entries = new List<BackupMetadataEntry>();
        var headerFound = false;
        string? currentDirectory = null;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (!headerFound)
            {
                headerFound = string.Equals(line.TrimStart('\uFEFF'), Header, StringComparison.Ordinal);
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith(DirectoryMarker, StringComparison.Ordinal))
            {
                currentDirectory = line[DirectoryMarker.Length..];
                if (string.IsNullOrWhiteSpace(currentDirectory))
                {
                    throw new InvalidDataException("Leerer Verzeichnismarker in der Metadatendatei.");
                }

                continue;
            }

            if (currentDirectory == null)
            {
                throw new InvalidDataException("Dateieintrag ohne vorherigen Verzeichnismarker.");
            }

            var record = line;
            while (!IsCompleteCsvRecord(record))
            {
                var continuation = await reader.ReadLineAsync(ct)
                    ?? throw new InvalidDataException("Unvollständiger gequoteter CSV-Eintrag.");
                record += $"\n{continuation}";
            }

            entries.Add(ParseEntry(currentDirectory, ParseCsvRecord(record)));
        }

        if (!headerFound)
        {
            throw new InvalidDataException("Metadaten-CSV enthält keinen unterstützten Header.");
        }

        return new BackupMetadataCatalog(entries);
    }

    private static BackupMetadataEntry ParseEntry(string directory, IReadOnlyList<string> fields)
    {
        if (fields.Count < 6)
        {
            throw new InvalidDataException("Metadaten-CSV enthält einen unvollständigen Dateieintrag.");
        }

        var fileName = fields[0];
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName is "." or ".." ||
            fileName.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new InvalidDataException($"Unsicherer Dateiname in Metadaten: {fileName}");
        }

        if (!long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) || size < 0)
        {
            throw new InvalidDataException($"Ungültige Dateigröße für {fileName}.");
        }

        var hash = fields[1];
        if (!ContentHashValidator.IsMd5Hash(hash) &&
            !hash.StartsWith("SYM-", StringComparison.Ordinal) &&
            !(hash == "0" && size == 0))
        {
            throw new InvalidDataException($"Ungültiger Hash in Metadaten für {fileName}.");
        }

        if (hash.StartsWith("SYM-", StringComparison.Ordinal))
        {
            try
            {
                _ = Convert.FromBase64String(hash[4..]);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException($"Ungültiger Symlink-Hash für {fileName}.", ex);
            }
        }

        var extension = fields[2];
        if (extension.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new InvalidDataException($"Unsichere Dateierweiterung für {fileName}.");
        }

        return new BackupMetadataEntry(directory, fileName, hash, extension, size, fields[4]);
    }

    private static bool IsCompleteCsvRecord(string value)
    {
        var insideQuotes = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '"')
            {
                continue;
            }

            if (insideQuotes && index + 1 < value.Length && value[index + 1] == '"')
            {
                index++;
                continue;
            }

            insideQuotes = !insideQuotes;
        }

        return !insideQuotes;
    }

    private static IReadOnlyList<string> ParseCsvRecord(string record)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var insideQuotes = false;

        for (var index = 0; index < record.Length; index++)
        {
            var character = record[index];
            if (character == '"')
            {
                if (insideQuotes && index + 1 < record.Length && record[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    insideQuotes = !insideQuotes;
                }

                continue;
            }

            if (character == ',' && !insideQuotes)
            {
                fields.Add(field.ToString());
                field.Clear();
                continue;
            }

            field.Append(character);
        }

        if (insideQuotes)
        {
            throw new InvalidDataException("Nicht abgeschlossener CSV-Quote.");
        }

        fields.Add(field.ToString());
        return fields;
    }
}
