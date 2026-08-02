namespace Ppki.Domain;

public enum DocumentKind
{
    LaporanAkhir,
    Skripsi,
    Tesis,
    Disertasi
}

public enum DocumentStatus
{
    Active,
    Archived
}

public enum AuditJobStatus
{
    Queued,
    Processing,
    Completed,
    Failed,
    Cancelled
}

public enum FindingStatus
{
    Open,
    Fixed,
    Ignored,
    ManualReview
}

public enum RuleSeverity
{
    Error,
    Warning,
    Info
}

public enum FixMode
{
    Auto,
    Confirm,
    Manual,
    Report
}
