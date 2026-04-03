using AlphaApp.Core.Models;

namespace AlphaApp.Core.Services;

/// <summary>
/// خدمة اللقطات: تنسيق عملية Boot → Freeze → Export الكاملة
/// </summary>
public interface ISnapshotService
{
    /// <summary>تنفيذ pipeline كامل: بناء → إقلاع → انتظار → تجميد → حفظ → تصدير</summary>
    Task<PipelineResult> CreateSnapshotAsync(AlphaAppDefinition app, CancellationToken ct = default);

    /// <summary>تنفيذ pipeline مع مؤشر تقدّم حي</summary>
    Task<PipelineResult> CreateSnapshotAsync(AlphaAppDefinition app, IProgress<BuildProgressEvent>? progress, CancellationToken ct = default);

    /// <summary>استرجاع معلومات لقطة موجودة</summary>
    Task<SnapshotInfo?> GetSnapshotAsync(string snapshotId, CancellationToken ct = default);

    /// <summary>قائمة جميع اللقطات</summary>
    Task<List<SnapshotInfo>> ListSnapshotsAsync(CancellationToken ct = default);

    /// <summary>حذف لقطة</summary>
    Task<bool> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default);
}
