namespace AlphaApp.Web.Models;

public class ErrorViewModel
{
    public string? RequestId { get; set; }
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}

public class DashboardViewModel
{
    public int TotalApps { get; set; }
    public int TotalSnapshots { get; set; }
    public int ReadySnapshots { get; set; }
    public List<AppSummary> RecentApps { get; set; } = [];
}

public class AppSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusCss { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class CreateAppViewModel
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string BaseDistro { get; set; } = "alpine";
    public string Architecture { get; set; } = "x86_64";

    /// <summary>مسار مجلد المشروع المصدري (يُنشر تلقائياً)</summary>
    public string? ProjectSourcePath { get; set; }

    /// <summary>مسار النشر (يُملأ تلقائياً بعد dotnet publish)</summary>
    public string? PublishedAppPath { get; set; }

    /// <summary>أمر التشغيل (يُكتشف تلقائياً)</summary>
    public string? EntryCommand { get; set; }
    public int GuestPort { get; set; } = 5000;
    public int MemoryMB { get; set; } = 256;

    /// <summary>حزم مقترحة (مفصولة بفاصلة)</summary>
    public string? Packages { get; set; }
}
