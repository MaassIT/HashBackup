using System.Text;

namespace HashBackup.Recovery;

internal sealed record VerificationReportRow(
    string DirectoryPath,
    string FileName,
    string Hash,
    long Size,
    string StorageObjectPath,
    string StorageAvailability,
    string? StorageContentMd5,
    string? LocalCalculatedMd5,
    string? LocalCalculatedSha256,
    string Result,
    string Detail)
{
    public static VerificationReportRow Create(
        BackupMetadataEntry entry,
        StorageObjectInfo storageObject,
        string result,
        string? detail,
        string? localCalculatedMd5 = null,
        string? localCalculatedSha256 = null) =>
        new(
            entry.DirectoryPath,
            entry.FileName,
            entry.Hash,
            entry.Size,
            storageObject.ObjectPath,
            storageObject.Availability.ToString(),
            storageObject.ContentHash,
            localCalculatedMd5,
            localCalculatedSha256,
            result,
            detail ?? string.Empty);
}

/// <summary>
/// Schreibt einen detaillierten Verify-Nachweis atomar. Auf Unix ist die Datei
/// nur für den aktuellen Benutzer lesbar, da sie Quellpfade enthalten kann.
/// </summary>
internal static class VerificationReportWriter
{
    private const string Header =
        "Directory,Filename,ExpectedHash,Size,StorageObject,StorageAvailability,StorageContentMd5,LocalCalculatedMd5,LocalCalculatedSha256,Result,Detail";

    public static async Task WriteAsync(
        string reportPath,
        IReadOnlyCollection<VerificationReportRow> rows,
        CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(reportPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("Verify-Bericht benötigt ein gültiges Zielverzeichnis.");
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        var temporaryPath = $"{fullPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            var content = new StringBuilder(Header).AppendLine();
            foreach (var row in rows)
            {
                content
                    .Append(Csv(row.DirectoryPath)).Append(',')
                    .Append(Csv(row.FileName)).Append(',')
                    .Append(Csv(row.Hash)).Append(',')
                    .Append(row.Size).Append(',')
                    .Append(Csv(row.StorageObjectPath)).Append(',')
                    .Append(Csv(row.StorageAvailability)).Append(',')
                    .Append(Csv(row.StorageContentMd5 ?? string.Empty)).Append(',')
                    .Append(Csv(row.LocalCalculatedMd5 ?? string.Empty)).Append(',')
                    .Append(Csv(row.LocalCalculatedSha256 ?? string.Empty)).Append(',')
                    .Append(Csv(row.Result)).Append(',')
                    .Append(Csv(row.Detail))
                    .AppendLine();
            }

            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            };
            if (!OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(temporaryPath, fileOptions))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(content.ToString().AsMemory(), ct);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            Log.Information("Verify-Bericht atomar geschrieben: {ReportPath} ({RowCount} Einträge)", fullPath, rows.Count);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string Csv(string value)
    {
        // Tabellenkalkulationen interpretieren führende Formelzeichen auch in
        // gequoteten CSV-Feldern. Ein Apostroph erzwingt Textdarstellung und
        // verhindert Formula Injection über Dateinamen oder Verzeichnisse.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n')
        {
            value = $"'{value}";
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }
}
