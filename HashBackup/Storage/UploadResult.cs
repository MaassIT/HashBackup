namespace HashBackup.Storage;

/// <summary>
/// Beschreibt das Ergebnis eines einzelnen Upload-Versuchs.
/// </summary>
public sealed record UploadResult(bool Success, bool SourceWasModified)
{
    /// <summary>
    /// Der Upload wurde vollständig und mit der erwarteten Content-MD5-Prüfung abgeschlossen.
    /// </summary>
    public static UploadResult Successful { get; } = new(true, false);

    /// <summary>
    /// Der Upload ist fehlgeschlagen und darf nach der konfigurierten Retry-Strategie wiederholt werden.
    /// </summary>
    public static UploadResult Failed { get; } = new(false, false);

    /// <summary>
    /// Die Quelle hat sich nach der Hash-Berechnung verändert. Ein Retry mit dem alten
    /// Content-Hash wäre unsicher und wird deshalb bis zum nächsten vollständigen Lauf verschoben.
    /// </summary>
    public static UploadResult SourceChanged { get; } = new(false, true);
}

/// <summary>
/// Validiert die als Content-Adresse verwendeten MD5-Hashes, bevor sie in einen Zielpfad gelangen.
/// </summary>
internal static class ContentHashValidator
{
    private const int Md5HexLength = 32;

    public static bool IsMd5Hash(string? value) =>
        value is { Length: Md5HexLength } && value.All(Uri.IsHexDigit);
}
