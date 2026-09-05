namespace Maki.Core.Entities;

// Instance-wide operational data. Only admin endpoints expose these records.
public class HealthCheckRecord
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Status { get; set; } = "unchecked";
    public string Message { get; set; } = "";
    public string? Url { get; set; }
    public DateTime CheckedAt { get; set; }
    public DateTime ChangedAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string NotifiedStatus { get; set; } = "healthy";
    public bool Acknowledged { get; set; }
}

public class HealthFile
{
    public int Id { get; set; }
    public int RootFolderId { get; set; }
    public int? SeriesId { get; set; }
    public int? ChapterFileId { get; set; }
    public string RelativePath { get; set; } = "";
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
    public long Size { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string? ContentHash { get; set; }
    public int AnalyzerVersion { get; set; }
    public string Status { get; set; } = "pending";
    public string AnalysisJson { get; set; } = "{}";
    public DateTime? AnalyzedAt { get; set; }
    public bool Removed { get; set; }
}

public class HealthFinding
{
    public int Id { get; set; }
    public int FileId { get; set; }
    public string Version { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Severity { get; set; } = "warning";
    public string Message { get; set; } = "";
    public string State { get; set; } = "open";
    public DateTime CreatedAt { get; set; }
}

public class HealthAnalysis
{
    public string Id { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public int AnalyzerVersion { get; set; }
    public string AnalysisJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class HealthFileVersion
{
    public string Id { get; set; } = "";
    public int FileId { get; set; }
    public string? ContentHash { get; set; }
    public long Size { get; set; }
    public DateTime ModifiedAt { get; set; }
    public int AnalyzerVersion { get; set; }
    public string RelativePath { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class HealthScan
{
    public int Id { get; set; }
    public string Status { get; set; } = "pending";
    public int? RootFolderId { get; set; }
    public int? SeriesId { get; set; }
    public string FileIdsJson { get; set; } = "[]";
    public bool Force { get; set; }
    public int Completed { get; set; }
    public int Total { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}

public class HealthOperation
{
    public int Id { get; set; }
    public string Kind { get; set; } = "repair";
    public string Status { get; set; } = "pending";
    public int FileId { get; set; }
    public string Version { get; set; } = "";
    public int? SourceMappingId { get; set; }
    public int UserId { get; set; }
    public string JournalJson { get; set; } = "[]";
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}

public class HealthHistory
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
    public int? FileId { get; set; }
    public int? UserId { get; set; }
}
