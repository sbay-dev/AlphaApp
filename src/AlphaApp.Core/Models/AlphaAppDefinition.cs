namespace AlphaApp.Core.Models;

/// <summary>
/// تعريف تطبيق ألفا — يصف التطبيق المستهدف وتوزيعته
/// </summary>
public class AlphaAppDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string BaseDistro { get; set; } = "alpine";
    public string Architecture { get; set; } = "x86_64";

    /// <summary>مسار التطبيق المنشور (dotnet publish output)</summary>
    public string PublishedAppPath { get; set; } = string.Empty;

    /// <summary>الأمر الذي يشغّل التطبيق داخل الضيف</summary>
    public string EntryCommand { get; set; } = string.Empty;

    /// <summary>المنفذ الذي يستمع عليه التطبيق داخل الضيف</summary>
    public int GuestPort { get; set; } = 5000;

    /// <summary>حزم إضافية تُثبت في التوزيعة</summary>
    public List<string> Packages { get; set; } = [];

    /// <summary>متغيرات البيئة</summary>
    public Dictionary<string, string> Environment { get; set; } = [];

    /// <summary>حجم الذاكرة بالميغابايت</summary>
    public int MemoryMB { get; set; } = 256;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public AlphaAppStatus Status { get; set; } = AlphaAppStatus.Created;
}

public enum AlphaAppStatus
{
    Created,
    Building,
    Booting,
    Running,
    Freezing,
    Frozen,
    Exported,
    Failed
}
