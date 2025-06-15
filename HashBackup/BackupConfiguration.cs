namespace HashBackup;

/// <summary>
/// Kapselt alle Konfigurationseinstellungen für einen Backup-Job
/// </summary>
public class BackupConfiguration
{
    /// <summary>
    /// Typ des Backup-Backends (z.B. "local", "azure")
    /// </summary>
    public required string BackupType { get; init; }
    
    /// <summary>
    /// Liste der zu sichernden Quellordner
    /// </summary>
    public required List<string> SourceFolders { get; init; }
    
    /// <summary>
    /// Pfad zur Lock-Datei, um parallele Ausführungen zu verhindern
    /// </summary>
    public required string LockFilePath { get; init; }
    
    /// <summary>
    /// Pfad zur Metadaten-Datei
    /// </summary>
    public required string MetadataFile { get; init; }
    
    /// <summary>
    /// Anzahl der parallelen Uploads
    /// </summary>
    public int ParallelUploads { get; init; } = 1;
    
    /// <summary>
    /// Safe-Mode: Prüft die Existenz der Dateien im Ziel vor dem Upload
    /// </summary>
    public bool SafeMode { get; init; }
    
    /// <summary>
    /// Dry-Run: Führt keine tatsächlichen Änderungen durch
    /// </summary>
    public bool DryRun { get; init; }
    
    /// <summary>
    /// Maximale Anzahl an Wiederholungen bei fehlgeschlagenen Uploads
    /// </summary>
    public int MaxRetries { get; init; } = 3;
    
    /// <summary>
    /// Verzögerung in Sekunden zwischen Wiederholungsversuchen
    /// </summary>
    public int RetryDelay { get; init; } = 5;
    
    /// <summary>
    /// Name des Backup-Jobs
    /// </summary>
    public required string JobName { get; init; }
    
    /// <summary>
    /// Tiefe der Zielverzeichnisstruktur
    /// </summary>
    public int TargetDirDepth { get; init; } = 3;
    
    /// <summary>
    /// Liste der zu ignorierenden Dateien
    /// </summary>
    public List<string> IgnoredFiles { get; init; } = new();
}
