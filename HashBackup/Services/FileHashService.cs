namespace HashBackup.Services;

/// <summary>
/// Dienst zur Berechnung und Verwaltung von Datei-Hashes
/// </summary>
public class FileHashService
{
    /// <summary>
    /// Berechnet den MD5-Hash einer Datei
    /// </summary>
    /// <param name="filePath">Pfad zur Datei</param>
    /// <param name="ct">Abbruch-Token</param>
    /// <returns>MD5-Hash der Datei oder ein spezielles Format für symbolische Links</returns>
    public async Task<string> CalculateMd5Async(string filePath, CancellationToken ct = default)
    {
        // Prüfe, ob es sich um einen symbolischen Link handelt
        if (File.GetAttributes(filePath).HasFlag(FileAttributes.ReparsePoint))
        {
            // Bei symbolischen Links erfassen wir das tatsächliche Ziel
            string targetPath;
            try {
                // ResolveLinkTarget gibt den tatsächlichen Zielpfad des Symlinks zurück
                targetPath = File.ResolveLinkTarget(filePath, false)?.FullName ?? "Ziel nicht verfügbar";
                Log.Debug("Symbolischer Link erkannt: {FilePath} -> {TargetPath}", filePath, targetPath);
            }
            catch (Exception ex) {
                Log.Warning(ex, "Konnte das Ziel des symbolischen Links {FilePath} nicht auflösen", filePath);
                targetPath = "Ziel nicht verfügbar";
            }
            
            // Zielpfad Base64-kodieren, um ihn sicher im Hash zu speichern
            var targetPathBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(targetPath));
            // "SYM-" als Prefix für symbolische Links, gefolgt von Base64-kodiertem Zielpfad
            return $"SYM-{targetPathBase64}";
        }
        
        // Normale Dateien wie bisher behandeln
        using var md5 = System.Security.Cryptography.MD5.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = await md5.ComputeHashAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Berechnet Hashes für eine Liste von Dateien parallel
    /// </summary>
    /// <param name="files">Liste von DateiInfos, die die Dateien und ihre Attribute enthalten</param>
    /// <param name="maxDegreeOfParallelism">Maximale Anzahl paralleler Hash-Berechnungen</param>
    /// <param name="ct">Abbruch-Token</param>
    public async Task CalculateHashesInParallelAsync(
        ConcurrentDictionary<string, (FileInfo Info, string? Hash, string? HashMtime, string? BackupMtime, string BackupMtimeAttr)> files,
        int maxDegreeOfParallelism,
        CancellationToken ct = default)
    {
        var filesToHash = files.Where(kvp => 
            string.IsNullOrEmpty(kvp.Value.HashMtime) || 
            kvp.Value.HashMtime != FileAttributesUtil.DateTimeToUnixTimestamp(kvp.Value.Info.LastWriteTimeUtc) || 
            string.IsNullOrEmpty(kvp.Value.Hash)
        ).Select(kvp => kvp.Key).ToList();

        var hashTasks = new List<Task>();
        var hashSemaphore = new SemaphoreSlim(maxDegreeOfParallelism);
        var hashesComputed = 0;
        
        foreach (var filePath in filesToHash)
        {
            await hashSemaphore.WaitAsync(ct);
            
            hashTasks.Add(Task.Run(async () =>
            {
                try
                {
                    var (info, _, _, backupMtime, backupMtimeAttr) = files[filePath];
                    Log.Debug("Berechne Hash für Datei {FilePath} (letzte Änderung: {LastWriteTime})", filePath, info.LastWriteTimeUtc);
                    
                    var fileHash = await CalculateMd5Async(filePath, ct);
                    FileAttributesUtil.SetAttribute(filePath, "user.md5_hash_value", fileHash);
                    // Unix-Timestamp im Python-Format speichern anstatt FileTime
                    FileAttributesUtil.SetAttribute(filePath, "user.md5_hash_mtime", 
                        FileAttributesUtil.DateTimeToUnixTimestamp(info.LastWriteTimeUtc));
                    
                    // Aktualisiere den Hash in unserer Dictionary
                    files[filePath] = (info, fileHash, 
                        FileAttributesUtil.DateTimeToUnixTimestamp(info.LastWriteTimeUtc), 
                        backupMtime, backupMtimeAttr);
                    
                    Interlocked.Increment(ref hashesComputed);
                    if (hashesComputed % 100 == 0)
                    {
                        Log.Information("{Count}/{Total} Hashes berechnet ({Percent:F1}%)",
                            hashesComputed, filesToHash.Count, (float)hashesComputed / filesToHash.Count * 100);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Fehler bei der Hash-Berechnung für {FilePath}", filePath);
                }
                finally
                {
                    hashSemaphore.Release();
                }
            }, ct));
        }

        // Warten auf Abschluss der Hash-Berechnung
        if (hashTasks.Any())
        {
            Log.Information("Warte auf Abschluss der parallelen Hash-Berechnung...");
            await Task.WhenAll(hashTasks);
            Log.Information("Hash-Berechnung abgeschlossen für {Count} Dateien", hashesComputed);
        }
        else
        {
            Log.Information("Keine neuen Hashes zu berechnen");
        }
    }
}
