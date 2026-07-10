using System.Buffers;
using System.Security.Cryptography;

namespace HashBackup.Recovery;

internal enum SourceVerificationStatus
{
    LocalSourceVerified,
    SourceMissing,
    SourceSizeMismatch,
    SourceHashMismatch,
    SourceChangedDuringRead,
    UnsafeSourcePath,
    SourceReadFailed
}

internal sealed record SourceVerificationResult(
    SourceVerificationStatus Status,
    string Detail,
    string? CalculatedMd5 = null,
    string? CalculatedSha256 = null);

/// <summary>
/// Bestätigt den erwarteten Katalog-Hash gegen eine noch vorhandene lokale
/// Quelldatei. Die Prüfung liest ausschließlich und schreibt weder xattrs noch
/// andere Dateimetadaten.
/// </summary>
internal sealed class SourceVerificationService(IReadOnlyList<string> sourceFolders)
{
    private readonly IReadOnlyList<string> _sourceFolders = sourceFolders
        .Select(Path.GetFullPath)
        .OrderByDescending(path => path.Length)
        .ToList();

    public async Task<SourceVerificationResult> VerifyAsync(
        BackupMetadataEntry entry,
        CancellationToken ct)
    {
        string sourcePath;
        try
        {
            sourcePath = ResolveSafeSourcePath(entry);
        }
        catch (InvalidDataException ex)
        {
            return new SourceVerificationResult(SourceVerificationStatus.UnsafeSourcePath, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SourceVerificationResult(
                SourceVerificationStatus.SourceReadFailed,
                $"Lokaler Quellpfad konnte nicht geprüft werden: {ex.Message}");
        }

        try
        {
            var before = new FileInfo(sourcePath);
            if (!before.Exists)
            {
                return new SourceVerificationResult(
                    SourceVerificationStatus.SourceMissing,
                    "Lokale Quelldatei fehlt.");
            }

            if (before.Length != entry.Size)
            {
                return new SourceVerificationResult(
                    SourceVerificationStatus.SourceSizeMismatch,
                    $"Lokale Größe weicht ab (erwartet {entry.Size}, vorhanden {before.Length}).");
            }

            var lengthBefore = before.Length;
            var modifiedBefore = before.LastWriteTimeUtc;
            await using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (File.GetAttributes(sourcePath).HasFlag(FileAttributes.ReparsePoint))
            {
                return new SourceVerificationResult(
                    SourceVerificationStatus.UnsafeSourcePath,
                    "Lokale Quelldatei wurde vor dem Hashen auf einen symbolischen Link umgeleitet.");
            }

            var (actualMd5, actualSha256) = await CalculateHashesAsync(stream, ct);

            before.Refresh();
            if (!before.Exists || before.Length != lengthBefore || before.LastWriteTimeUtc != modifiedBefore)
            {
                return new SourceVerificationResult(
                    SourceVerificationStatus.SourceChangedDuringRead,
                    "Lokale Quelldatei wurde während der Prüfung verändert.",
                    actualMd5,
                    actualSha256);
            }

            if (!string.Equals(actualMd5, entry.Hash, StringComparison.OrdinalIgnoreCase))
            {
                return new SourceVerificationResult(
                    SourceVerificationStatus.SourceHashMismatch,
                    "Lokaler Inhalt stimmt nicht mit dem erwarteten Katalog-Hash überein.",
                    actualMd5,
                    actualSha256);
            }

            return new SourceVerificationResult(
                SourceVerificationStatus.LocalSourceVerified,
                "Lokale Größe und selbst berechneter MD5 stimmen mit dem Katalog überein; SHA-256 wurde als zusätzlicher Nachweis erfasst.",
                actualMd5,
                actualSha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SourceVerificationResult(
                SourceVerificationStatus.SourceReadFailed,
                $"Lokale Quelldatei konnte nicht gelesen werden: {ex.Message}");
        }
    }

    private static async Task<(string Md5, string Sha256)> CalculateHashesAsync(
        Stream stream,
        CancellationToken ct)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(4 * 1024 * 1024);
        try
        {
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                md5.AppendData(buffer, 0, bytesRead);
                sha256.AppendData(buffer, 0, bytesRead);
            }

            return (
                Convert.ToHexStringLower(md5.GetHashAndReset()),
                Convert.ToHexStringLower(sha256.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private string ResolveSafeSourcePath(BackupMetadataEntry entry)
    {
        var candidate = Path.GetFullPath(Path.Combine(entry.DirectoryPath, entry.FileName));
        var sourceRoot = _sourceFolders.FirstOrDefault(root => IsContainedPath(root, candidate));
        if (sourceRoot == null)
        {
            throw new InvalidDataException("Metadatenpfad liegt außerhalb der konfigurierten Quellen.");
        }

        var relativePath = Path.GetRelativePath(sourceRoot, candidate);
        var currentPath = sourceRoot;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
            {
                continue;
            }

            if (File.GetAttributes(currentPath).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Lokaler Quellpfad enthält einen symbolischen Link.");
            }
        }

        return candidate;
    }

    private static bool IsContainedPath(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }
}
