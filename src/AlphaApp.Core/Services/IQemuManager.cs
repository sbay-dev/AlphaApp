using AlphaApp.Core.Models;

namespace AlphaApp.Core.Services;

/// <summary>
/// إدارة دورة حياة QEMU: إقلاع، تجميد، حفظ حالة، إيقاف
/// </summary>
public interface IQemuManager
{
    /// <summary>تشغيل ضيف QEMU من صورة قرص</summary>
    Task<GuestState> BootGuestAsync(AlphaAppDefinition app, string diskImagePath, CancellationToken ct = default);

    /// <summary>تجميد الضيف عبر QMP (stop)</summary>
    Task<bool> FreezeGuestAsync(GuestState guest, CancellationToken ct = default);

    /// <summary>استئناف الضيف المجمّد عبر QMP (cont)</summary>
    Task<bool> ResumeGuestAsync(GuestState guest, CancellationToken ct = default);

    /// <summary>حفظ حالة VM كاملة (savevm) — ذاكرة + CPU + أجهزة</summary>
    Task<string> SaveVmStateAsync(GuestState guest, string snapshotName, CancellationToken ct = default);

    /// <summary>إيقاف الضيف</summary>
    Task StopGuestAsync(GuestState guest, CancellationToken ct = default);

    /// <summary>إرسال أمر QMP مباشر</summary>
    Task<QmpResponse> SendQmpCommandAsync(int qmpPort, QmpCommand command, CancellationToken ct = default);

    /// <summary>تشغيل ضيف من لقطة مجمّدة (loadvm) — إقلاع فوري</summary>
    Task<GuestState> LaunchFromSnapshotAsync(SnapshotInfo snapshot, CancellationToken ct = default);

    /// <summary>التحقق من جاهزية الضيف عبر فحص المنفذ</summary>
    Task<bool> WaitForGuestReadyAsync(GuestState guest, int timeoutSeconds, CancellationToken ct = default);
}
