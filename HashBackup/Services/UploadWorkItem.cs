namespace HashBackup.Services;

/// <summary>
/// Immutable snapshot of a file at the point at which its content hash was selected.
/// Keeping this snapshot with the upload prevents a later source modification from
/// being marked as successfully backed up.
/// </summary>
public sealed record UploadWorkItem(
    string FilePath,
    string DestinationPath,
    string FileHash,
    string HashMtime,
    long ExpectedLength,
    int TryCount = 0);
