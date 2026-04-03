using AlphaApp.Core.Models;

namespace AlphaApp.Core.Services;

/// <summary>
/// إدارة صور التوزيعات الأساسية (Alpine Linux)
/// </summary>
public interface IImageManager
{
    /// <summary>تحميل صورة التوزيعة الأساسية</summary>
    Task<string> DownloadBaseImageAsync(string distro, string arch, CancellationToken ct = default);

    /// <summary>بناء صورة قرص مخصصة تحتوي التطبيق</summary>
    Task<string> BuildAppImageAsync(AlphaAppDefinition app, CancellationToken ct = default);

    /// <summary>التحقق من وجود الصورة محلياً</summary>
    bool ImageExists(string distro, string arch);

    /// <summary>الحصول على قائمة الصور المتوفرة</summary>
    List<string> ListAvailableImages();
}
