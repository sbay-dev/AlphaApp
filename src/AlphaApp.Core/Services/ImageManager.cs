using System.Diagnostics;
using System.Security.Cryptography;
using AlphaApp.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlphaApp.Core.Services;

public class ImageManager : IImageManager
{
    private readonly QemuOptions _options;
    private readonly ILogger<ImageManager> _logger;

    // alpine-virt: أصغر ISO قابلة للإقلاع (~67MB) مع نواة مخصصة للآلات الافتراضية
    private static readonly Dictionary<string, string> AlpineUrls = new()
    {
        ["x86_64"] = "https://dl-cdn.alpinelinux.org/alpine/v3.23/releases/x86_64/alpine-virt-3.23.3-x86_64.iso",
        ["aarch64"] = "https://dl-cdn.alpinelinux.org/alpine/v3.23/releases/aarch64/alpine-virt-3.23.3-aarch64.iso"
    };

    public ImageManager(IOptions<QemuOptions> options, ILogger<ImageManager> logger)
    {
        _options = options.Value;
        _logger = logger;
        Directory.CreateDirectory(_options.ImagesDirectory);
        Directory.CreateDirectory(_options.AppsDirectory);
    }

    public async Task<string> DownloadBaseImageAsync(string distro, string arch, CancellationToken ct = default)
    {
        var imagePath = Path.Combine(_options.ImagesDirectory, $"{distro}-{arch}.iso");
        if (File.Exists(imagePath))
        {
            _logger.LogInformation("📦 صورة {Distro}-{Arch} موجودة محلياً", distro, arch);
            return imagePath;
        }

        if (!AlpineUrls.TryGetValue(arch, out var url))
            throw new NotSupportedException($"المعمارية {arch} غير مدعومة");

        _logger.LogInformation("⬇️ تحميل {Distro}-{Arch} من {Url}...", distro, arch, url);

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = File.Create(imagePath);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;

        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (totalBytes > 0 && downloaded % (1024 * 1024) < buffer.Length)
                _logger.LogInformation("  📥 {Downloaded:F1}MB / {Total:F1}MB",
                    downloaded / 1048576.0, totalBytes / 1048576.0);
        }

        _logger.LogInformation("✅ صورة {Distro}-{Arch} محفوظة: {Path}", distro, arch, imagePath);
        return imagePath;
    }

    public async Task<string> BuildAppImageAsync(AlphaAppDefinition app, CancellationToken ct = default)
    {
        _logger.LogInformation("🔧 بناء صورة قرص لتطبيق {Name}...", app.Name);

        var appDir = Path.Combine(_options.AppsDirectory, app.Id);
        Directory.CreateDirectory(appDir);

        var diskPath = Path.Combine(appDir, $"{app.Id}.qcow2");

        // إنشاء صورة qcow2
        await RunCommandAsync("qemu-img", ["create", "-f", "qcow2", diskPath, "2G"], ct);
        _logger.LogInformation("  💿 صورة قرص: {Path}", diskPath);

        // إنشاء سكربت التهيئة
        var initScript = GenerateInitScript(app);
        var initScriptPath = Path.Combine(appDir, "alpha-init.sh");
        await File.WriteAllTextAsync(initScriptPath, initScript, ct);

        // إنشاء سكربت cloud-init / setup
        var setupScript = GenerateSetupScript(app);
        var setupScriptPath = Path.Combine(appDir, "alpha-setup.sh");
        await File.WriteAllTextAsync(setupScriptPath, setupScript, ct);

        _logger.LogInformation("✅ صورة التطبيق جاهزة: {Path}", diskPath);
        return diskPath;
    }

    public bool ImageExists(string distro, string arch) =>
        File.Exists(Path.Combine(_options.ImagesDirectory, $"{distro}-{arch}.iso"));

    public List<string> ListAvailableImages() =>
        Directory.Exists(_options.ImagesDirectory)
            ? Directory.GetFiles(_options.ImagesDirectory, "*.iso").Select(Path.GetFileName).ToList()!
            : [];

    private string GenerateInitScript(AlphaAppDefinition app)
    {
        var envLines = string.Join("\n", app.Environment.Select(e => $"export {e.Key}=\"{e.Value}\""));
        var packagesLine = app.Packages.Count > 0 ? $"apk add --no-cache {string.Join(" ", app.Packages)}" : "";

        return $$"""
            #!/bin/sh
            # AlphaApp Init Script — {{app.Name}}
            # يُنفَّذ تلقائياً عند الإقلاع

            set -e

            echo "🚀 AlphaApp: بدء تهيئة {{app.Name}}..."

            # تثبيت حزم إضافية
            {{packagesLine}}

            # متغيرات البيئة
            {{envLines}}
            export ASPNETCORE_URLS="http://0.0.0.0:{{app.GuestPort}}"
            export DOTNET_RUNNING_IN_CONTAINER=true

            # تشغيل التطبيق
            echo "▶️ تشغيل التطبيق..."
            cd /app
            {{app.EntryCommand}} &

            echo "✅ AlphaApp: {{app.Name}} يعمل على المنفذ {{app.GuestPort}}"
            """;
    }

    private string GenerateSetupScript(AlphaAppDefinition app)
    {
        return $$"""
            #!/bin/sh
            # AlphaApp Setup Script — إعداد التوزيعة لتطبيق {{app.Name}}
            set -e

            echo "📦 إعداد Alpine Linux لـ {{app.Name}}..."

            # تحديث المستودعات
            apk update

            # تثبيت .NET Runtime
            apk add --no-cache dotnet10-runtime icu-libs libgcc libstdc++

            # تثبيت الحزم الإضافية
            {{(app.Packages.Count > 0 ? $"apk add --no-cache {string.Join(" ", app.Packages)}" : "# لا حزم إضافية")}}

            # نسخ التطبيق
            mkdir -p /app
            cp -r /mnt/app/* /app/
            chmod +x /app/{{Path.GetFileName(app.EntryCommand)}} 2>/dev/null || true

            # إعداد الإقلاع التلقائي
            cp /mnt/alpha-init.sh /etc/local.d/alpha-app.start
            chmod +x /etc/local.d/alpha-app.start
            rc-update add local default

            echo "✅ الإعداد مكتمل"
            """;
    }

    private async Task RunCommandAsync(string command, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"فشل تشغيل: {command}");
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"{command} فشل (exit {proc.ExitCode}): {err}");
        }
    }
}
