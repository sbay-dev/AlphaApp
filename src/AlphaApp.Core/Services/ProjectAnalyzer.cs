using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AlphaApp.Core.Models;
using Microsoft.Extensions.Logging;

namespace AlphaApp.Core.Services;

/// <summary>
/// خدمة ذكية لتحليل مشاريع .NET: اكتشاف تلقائي لأمر التشغيل والحزم والنشر
/// </summary>
public class ProjectAnalyzer
{
    private readonly ILogger<ProjectAnalyzer> _logger;

    public ProjectAnalyzer(ILogger<ProjectAnalyzer> logger) => _logger = logger;

    /// <summary>
    /// تحليل مجلد المشروع واكتشاف كل المعلومات المطلوبة تلقائياً
    /// </summary>
    public async Task<ProjectAnalysis> AnalyzeAsync(string projectPath, CancellationToken ct = default)
    {
        _logger.LogInformation("🔍 تحليل المشروع: {Path}", projectPath);
        var analysis = new ProjectAnalysis { ProjectPath = projectPath };

        // البحث عن ملف .csproj
        var csprojFile = FindCsprojFile(projectPath);
        if (csprojFile == null)
        {
            analysis.Errors.Add($"لم يتم العثور على ملف .csproj في {projectPath}");
            return analysis;
        }

        analysis.CsprojPath = csprojFile;
        analysis.ProjectName = Path.GetFileNameWithoutExtension(csprojFile);
        _logger.LogInformation("  📄 مشروع: {Name}", analysis.ProjectName);

        // تحليل .csproj
        var csproj = XDocument.Load(csprojFile);
        AnalyzeCsproj(csproj, analysis);

        // اكتشاف NuGet packages
        AnalyzePackages(csproj, analysis);

        // اقتراح حزم Alpine
        SuggestAlpinePackages(analysis);

        // اكتشاف أمر التشغيل
        ResolveEntryCommand(analysis);

        // اكتشاف المنفذ
        await DetectPortAsync(projectPath, analysis, ct);

        _logger.LogInformation("  ✅ التحليل مكتمل: {Type} | {Framework} | المنفذ {Port}",
            analysis.ProjectType, analysis.TargetFramework, analysis.DetectedPort);

        return analysis;
    }

    /// <summary>
    /// نشر المشروع تلقائياً (dotnet publish)
    /// </summary>
    public async Task<PublishResult> PublishAsync(string projectPath, string? runtime = null, CancellationToken ct = default)
    {
        var csprojFile = FindCsprojFile(projectPath);
        if (csprojFile == null)
            return new PublishResult { Success = false, Error = "لم يتم العثور على ملف .csproj" };

        var projectName = Path.GetFileNameWithoutExtension(csprojFile);
        var outputDir = Path.Combine(projectPath, "bin", "AlphaApp-publish");

        _logger.LogInformation("📦 نشر المشروع {Name}...", projectName);

        var args = new List<string>
        {
            "publish", csprojFile,
            "-c", "Release",
            "-o", outputDir,
            "--nologo"
        };

        if (!string.IsNullOrEmpty(runtime))
            args.AddRange(["-r", runtime, "--self-contained", "false"]);

        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = projectPath
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        _logger.LogInformation("  ⚙️ dotnet {Args}", string.Join(" ", args));

        using var proc = Process.Start(psi);
        if (proc == null)
            return new PublishResult { Success = false, Error = "فشل تشغيل dotnet publish" };

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        sw.Stop();

        if (proc.ExitCode != 0)
        {
            _logger.LogError("  ❌ فشل النشر: {Error}", stderr);
            return new PublishResult
            {
                Success = false,
                Error = $"dotnet publish فشل (exit {proc.ExitCode}): {stderr}",
                Output = stdout
            };
        }

        _logger.LogInformation("  ✅ النشر مكتمل في {Duration:F1}s → {Output}", sw.Elapsed.TotalSeconds, outputDir);
        return new PublishResult
        {
            Success = true,
            OutputPath = outputDir,
            Duration = sw.Elapsed,
            Output = stdout
        };
    }

    private string? FindCsprojFile(string path)
    {
        if (File.Exists(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return path;

        if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path, "*.csproj", SearchOption.TopDirectoryOnly);
            if (files.Length == 1) return files[0];

            // إذا كان هناك أكثر من csproj، نبحث عن الملف الذي يحتوي Web SDK
            if (files.Length > 1)
            {
                foreach (var f in files)
                {
                    var content = File.ReadAllText(f);
                    if (content.Contains("Microsoft.NET.Sdk.Web"))
                        return f;
                }
                return files[0];
            }

            // بحث في المستوى الأعمق
            files = Directory.GetFiles(path, "*.csproj", SearchOption.AllDirectories);
            if (files.Length > 0)
            {
                var webProject = files.FirstOrDefault(f =>
                    File.ReadAllText(f).Contains("Microsoft.NET.Sdk.Web"));
                return webProject ?? files[0];
            }
        }

        return null;
    }

    private void AnalyzeCsproj(XDocument csproj, ProjectAnalysis analysis)
    {
        var props = csproj.Descendants("PropertyGroup");

        // SDK type
        var sdk = csproj.Root?.Attribute("Sdk")?.Value ?? "";
        analysis.IsWebProject = sdk.Contains("Web");
        analysis.ProjectType = sdk switch
        {
            "Microsoft.NET.Sdk.Web" => ProjectType.Web,
            "Microsoft.NET.Sdk.Worker" => ProjectType.Worker,
            "Microsoft.NET.Sdk.BlazorWebAssembly" => ProjectType.Blazor,
            _ when csproj.Descendants("OutputType").Any(e =>
                e.Value.Equals("Exe", StringComparison.OrdinalIgnoreCase)) => ProjectType.Console,
            _ => ProjectType.Library
        };

        // Target Framework
        analysis.TargetFramework = csproj.Descendants("TargetFramework").FirstOrDefault()?.Value ?? "net10.0";

        // Assembly name
        analysis.AssemblyName = csproj.Descendants("AssemblyName").FirstOrDefault()?.Value
                                ?? analysis.ProjectName;

        // Output type
        analysis.OutputType = csproj.Descendants("OutputType").FirstOrDefault()?.Value ?? "Library";

        _logger.LogInformation("  📋 نوع: {Type} | SDK: {Sdk} | TFM: {Tfm}",
            analysis.ProjectType, sdk, analysis.TargetFramework);
    }

    private void AnalyzePackages(XDocument csproj, ProjectAnalysis analysis)
    {
        var packages = csproj.Descendants("PackageReference")
            .Select(p => new NuGetPackageInfo
            {
                Name = p.Attribute("Include")?.Value ?? "",
                Version = p.Attribute("Version")?.Value ?? ""
            })
            .Where(p => !string.IsNullOrEmpty(p.Name))
            .ToList();

        analysis.NuGetPackages = packages;
        _logger.LogInformation("  📦 {Count} حزم NuGet", packages.Count);
    }

    private void SuggestAlpinePackages(ProjectAnalysis analysis)
    {
        var packages = new HashSet<string>();
        var reasons = new Dictionary<string, string>();

        // .NET Runtime دائماً مطلوب
        if (analysis.TargetFramework.Contains("10"))
        {
            packages.Add("dotnet10-runtime");
            reasons["dotnet10-runtime"] = "تشغيل تطبيقات .NET 10";
        }

        // تحليل NuGet packages واقتراح ما يناسبها
        foreach (var pkg in analysis.NuGetPackages)
        {
            var name = pkg.Name.ToLowerInvariant();

            // ICU — مطلوب للعولمة
            if (name.Contains("localization") || name.Contains("globalization") ||
                name.Contains("culture") || name.Contains("aspnetcore"))
            {
                packages.Add("icu-libs");
                reasons["icu-libs"] = $"مطلوب لـ {pkg.Name} (العولمة)";
            }

            // SQLite
            if (name.Contains("sqlite"))
            {
                packages.Add("sqlite-libs");
                reasons["sqlite-libs"] = $"مطلوب لـ {pkg.Name}";
            }

            // EF Core
            if (name.Contains("entityframework"))
            {
                packages.Add("icu-libs");
                reasons.TryAdd("icu-libs", $"مطلوب لـ {pkg.Name}");
            }

            // Redis
            if (name.Contains("redis") || name.Contains("stackexchange"))
            {
                packages.Add("redis");
                reasons["redis"] = $"مطلوب لـ {pkg.Name}";
            }

            // ML / ONNX
            if (name.Contains("onnx") || name.Contains("ml."))
            {
                packages.Add("libstdc++");
                packages.Add("libgcc");
                reasons["libstdc++"] = $"مطلوب لـ {pkg.Name} (مكتبات C++)";
                reasons["libgcc"] = $"مطلوب لـ {pkg.Name}";
            }

            // Puppeteer / Browser
            if (name.Contains("puppeteer"))
            {
                packages.Add("chromium");
                packages.Add("nss");
                reasons["chromium"] = $"مطلوب لـ {pkg.Name} (متصفح headless)";
            }

            // Docker
            if (name.Contains("docker"))
            {
                packages.Add("docker-cli");
                reasons["docker-cli"] = $"مطلوب لـ {pkg.Name}";
            }

            // SignalR / WebSockets
            if (name.Contains("signalr"))
            {
                packages.Add("icu-libs");
                reasons.TryAdd("icu-libs", $"مطلوب لـ {pkg.Name}");
            }

            // Cryptography
            if (name.Contains("cryptography") || name.Contains("security"))
            {
                packages.Add("openssl");
                reasons["openssl"] = $"مطلوب لـ {pkg.Name}";
            }

            // gRPC
            if (name.Contains("grpc"))
            {
                packages.Add("libstdc++");
                reasons.TryAdd("libstdc++", $"مطلوب لـ {pkg.Name}");
            }
        }

        // حزم أساسية لأي تطبيق ASP.NET Core
        if (analysis.IsWebProject)
        {
            packages.Add("icu-libs");
            packages.Add("libgcc");
            packages.Add("libstdc++");
            packages.Add("curl");
            reasons.TryAdd("icu-libs", "مطلوب لـ ASP.NET Core (العولمة)");
            reasons.TryAdd("libgcc", "مكتبات C الأساسية");
            reasons.TryAdd("libstdc++", "مكتبات C++ الأساسية");
            reasons.TryAdd("curl", "أداة شبكة (فحص الجاهزية)");
        }

        analysis.SuggestedPackages = packages.Select(p => new SuggestedPackage
        {
            Name = p,
            Reason = reasons.GetValueOrDefault(p, ""),
            IsRequired = p.Contains("dotnet") || p == "icu-libs" || p == "libgcc"
        }).OrderByDescending(p => p.IsRequired).ToList();

        _logger.LogInformation("  💡 {Count} حزمة Alpine مقترحة", analysis.SuggestedPackages.Count);
    }

    private void ResolveEntryCommand(ProjectAnalysis analysis)
    {
        var dllName = $"{analysis.AssemblyName}.dll";

        analysis.EntryCommand = analysis.ProjectType switch
        {
            ProjectType.Web => $"dotnet /app/{dllName}",
            ProjectType.Worker => $"dotnet /app/{dllName}",
            ProjectType.Console => $"dotnet /app/{dllName}",
            ProjectType.Blazor => $"dotnet /app/{dllName}",
            _ => $"dotnet /app/{dllName}"
        };

        analysis.EntryCommandExplanation = analysis.ProjectType switch
        {
            ProjectType.Web => $"تشغيل خادم الويب: {dllName}",
            ProjectType.Worker => $"تشغيل خدمة الخلفية: {dllName}",
            ProjectType.Console => $"تشغيل التطبيق: {dllName}",
            _ => $"تشغيل: {dllName}"
        };

        _logger.LogInformation("  🎯 أمر التشغيل: {Cmd}", analysis.EntryCommand);
    }

    private async Task DetectPortAsync(string projectPath, ProjectAnalysis analysis, CancellationToken ct)
    {
        analysis.DetectedPort = 5000; // افتراضي ASP.NET Core

        // فحص launchSettings.json
        var launchSettings = Path.Combine(projectPath, "Properties", "launchSettings.json");
        if (File.Exists(launchSettings))
        {
            var content = await File.ReadAllTextAsync(launchSettings, ct);
            var portMatch = Regex.Match(content, @"http://[^:]+:(\d+)");
            if (portMatch.Success && int.TryParse(portMatch.Groups[1].Value, out var port))
            {
                analysis.DetectedPort = port;
                analysis.PortSource = "launchSettings.json";
                return;
            }
        }

        // فحص appsettings.json
        var appSettings = Path.Combine(projectPath, "appsettings.json");
        if (File.Exists(appSettings))
        {
            var content = await File.ReadAllTextAsync(appSettings, ct);
            var portMatch = Regex.Match(content, @"""Url"":\s*""http://[^:]+:(\d+)""", RegexOptions.IgnoreCase);
            if (portMatch.Success && int.TryParse(portMatch.Groups[1].Value, out var port))
            {
                analysis.DetectedPort = port;
                analysis.PortSource = "appsettings.json";
                return;
            }
        }

        // فحص Program.cs
        var programCs = Path.Combine(projectPath, "Program.cs");
        if (File.Exists(programCs))
        {
            var content = await File.ReadAllTextAsync(programCs, ct);
            var portMatch = Regex.Match(content, @"UseUrls\(""http://[^:]+:(\d+)""");
            if (portMatch.Success && int.TryParse(portMatch.Groups[1].Value, out var port))
            {
                analysis.DetectedPort = port;
                analysis.PortSource = "Program.cs";
                return;
            }
        }

        analysis.PortSource = "افتراضي ASP.NET Core";
    }
}

// ═══════════════════════════════════════════════
// النماذج
// ═══════════════════════════════════════════════

public class ProjectAnalysis
{
    public string ProjectPath { get; set; } = string.Empty;
    public string? CsprojPath { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string AssemblyName { get; set; } = string.Empty;
    public string TargetFramework { get; set; } = "net10.0";
    public string OutputType { get; set; } = "Library";
    public ProjectType ProjectType { get; set; }
    public bool IsWebProject { get; set; }

    public string EntryCommand { get; set; } = string.Empty;
    public string EntryCommandExplanation { get; set; } = string.Empty;

    public int DetectedPort { get; set; } = 5000;
    public string PortSource { get; set; } = string.Empty;

    public List<NuGetPackageInfo> NuGetPackages { get; set; } = [];
    public List<SuggestedPackage> SuggestedPackages { get; set; } = [];
    public List<string> Errors { get; set; } = [];

    public bool IsValid => CsprojPath != null && Errors.Count == 0;
}

public enum ProjectType
{
    Web,
    Worker,
    Console,
    Blazor,
    Library
}

public class NuGetPackageInfo
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

public class SuggestedPackage
{
    public string Name { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public bool Selected { get; set; } = true;
}

public class PublishResult
{
    public bool Success { get; set; }
    public string? OutputPath { get; set; }
    public string? Error { get; set; }
    public string? Output { get; set; }
    public TimeSpan Duration { get; set; }
}
