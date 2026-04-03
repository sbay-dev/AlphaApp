namespace AlphaApp.Core.Models;

/// <summary>
/// بيانات وصفية عن لقطة VM مجمّدة
/// </summary>
public class SnapshotInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string AppId { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;

    /// <summary>مسار ملف القرص (qcow2)</summary>
    public string DiskImagePath { get; set; } = string.Empty;

    /// <summary>مسار ملف حالة الذاكرة</summary>
    public string MemoryStatePath { get; set; } = string.Empty;

    /// <summary>مسار ملف الـ kernel</summary>
    public string KernelPath { get; set; } = string.Empty;

    /// <summary>مسار ملف initramfs</summary>
    public string InitramfsPath { get; set; } = string.Empty;

    /// <summary>الحجم الكلي بالبايتات</summary>
    public long TotalSizeBytes { get; set; }

    /// <summary>SHA256 للتحقق من السلامة</summary>
    public string Checksum { get; set; } = string.Empty;

    /// <summary>هل اللقطة جاهزة للتشغيل في qemu-wasm؟</summary>
    public bool IsWasmReady { get; set; }

    /// <summary>المعمارية المستهدفة</summary>
    public string Architecture { get; set; } = "x86_64";

    /// <summary>حجم الذاكرة بالميغابايت</summary>
    public int MemoryMB { get; set; } = 256;

    /// <summary>المنفذ المُمرَّر من الضيف</summary>
    public int GuestPort { get; set; } = 5000;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public SnapshotStatus Status { get; set; } = SnapshotStatus.Creating;
}

public enum SnapshotStatus
{
    Creating,
    Ready,
    WasmConverted,
    Failed
}
