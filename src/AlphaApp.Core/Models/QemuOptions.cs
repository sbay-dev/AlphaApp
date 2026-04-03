namespace AlphaApp.Core.Models;

/// <summary>
/// إعدادات QEMU لتشغيل الضيف
/// </summary>
public class QemuOptions
{
    /// <summary>مسار ملف QEMU التنفيذي</summary>
    public string QemuBinaryPath { get; set; } = "qemu-system-x86_64";

    /// <summary>المعمارية (x86_64 / aarch64)</summary>
    public string Architecture { get; set; } = "x86_64";

    /// <summary>مسار تخزين الصور واللقطات</summary>
    public string DataDirectory { get; set; } = "./data";

    /// <summary>حجم الذاكرة الافتراضي بالميغابايت</summary>
    public int DefaultMemoryMB { get; set; } = 256;

    /// <summary>عدد أنوية المعالج</summary>
    public int CpuCores { get; set; } = 1;

    /// <summary>استخدام KVM إن توفر</summary>
    public bool EnableKvm { get; set; } = false;

    /// <summary>منفذ QMP للتحكم</summary>
    public int QmpBasePort { get; set; } = 4444;

    /// <summary>منفذ بدء تمرير المنافذ</summary>
    public int HostPortBase { get; set; } = 10000;

    /// <summary>مهلة انتظار الإقلاع بالثواني</summary>
    public int BootTimeoutSeconds { get; set; } = 120;

    /// <summary>مهلة انتظار جاهزية التطبيق بالثواني</summary>
    public int AppReadyTimeoutSeconds { get; set; } = 60;

    /// <summary>مسار تخزين صور التوزيعات الأساسية</summary>
    public string ImagesDirectory => Path.Combine(DataDirectory, "images");

    /// <summary>مسار تخزين اللقطات</summary>
    public string SnapshotsDirectory => Path.Combine(DataDirectory, "snapshots");

    /// <summary>مسار تخزين تعريفات التطبيقات</summary>
    public string AppsDirectory => Path.Combine(DataDirectory, "apps");
}
