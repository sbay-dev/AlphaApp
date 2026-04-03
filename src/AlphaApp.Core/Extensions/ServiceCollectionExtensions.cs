using AlphaApp.Core.Models;
using AlphaApp.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AlphaApp.Core.Extensions;

/// <summary>
/// تسجيل خدمات AlphaApp في DI container
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// إضافة خدمات AlphaApp الوسيطة
    /// </summary>
    public static IServiceCollection AddAlphaApp(this IServiceCollection services, Action<QemuOptions>? configure = null)
    {
        // تهيئة الخيارات
        if (configure != null)
            services.Configure(configure);
        else
            services.Configure<QemuOptions>(_ => { });

        // تسجيل الخدمات
        services.AddSingleton<IQemuManager, QemuManager>();
        services.AddSingleton<IImageManager, ImageManager>();
        services.AddSingleton<ISnapshotService, SnapshotService>();
        services.AddSingleton<WasmBridgeService>();
        services.AddSingleton<ProjectAnalyzer>();

        return services;
    }
}
