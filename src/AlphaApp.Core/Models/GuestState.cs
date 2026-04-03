namespace AlphaApp.Core.Models;

/// <summary>
/// حالة الضيف الحالية في QEMU
/// </summary>
public class GuestState
{
    public string GuestId { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    public GuestStatus Status { get; set; } = GuestStatus.Stopped;
    public int ProcessId { get; set; }
    public int QmpPort { get; set; }
    public int HostPort { get; set; }
    public int GuestPort { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FrozenAt { get; set; }
    public string? LastError { get; set; }
}

public enum GuestStatus
{
    Stopped,
    Starting,
    Running,
    Freezing,
    Frozen,
    Saving,
    Error
}

/// <summary>
/// نتيجة عملية في الـ pipeline
/// </summary>
public class PipelineResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Error { get; set; }
    public SnapshotInfo? Snapshot { get; set; }
    public TimeSpan Duration { get; set; }
    public List<PipelineStep> Steps { get; set; } = [];
}

public class PipelineStep
{
    public string Name { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// حدث تقدّم Pipeline — يُرسل في الوقت الحقيقي عبر SignalR
/// </summary>
public class BuildProgressEvent
{
    public int StepNumber { get; set; }
    public int TotalSteps { get; set; }
    public string StepName { get; set; } = string.Empty;
    public string Status { get; set; } = "running"; // running, done, error, skipped
    public string? Message { get; set; }
    public double DurationSeconds { get; set; }
    public double OverallPercent { get; set; }
    public string Phase { get; set; } = string.Empty; // publish, snapshot, wasm
}

/// <summary>
/// رسالة QMP (QEMU Machine Protocol)
/// </summary>
public class QmpCommand
{
    public string Execute { get; set; } = string.Empty;
    public Dictionary<string, object>? Arguments { get; set; }
}

public class QmpResponse
{
    public string? Return { get; set; }
    public QmpError? Error { get; set; }
}

public class QmpError
{
    public string Class { get; set; } = string.Empty;
    public string Desc { get; set; } = string.Empty;
}
