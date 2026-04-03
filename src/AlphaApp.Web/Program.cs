using AlphaApp.Core.Extensions;
using AlphaApp.Core.Pipeline;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// تسجيل خدمات AlphaApp الوسيطة
builder.Services.AddAlphaApp(options =>
{
    options.DataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
    options.QemuBinaryPath = builder.Configuration["Qemu:BinaryPath"] ?? "qemu-system-x86_64";
    options.DefaultMemoryMB = builder.Configuration.GetValue("Qemu:MemoryMB", 256);
    options.CpuCores = builder.Configuration.GetValue("Qemu:CpuCores", 1);
    options.EnableKvm = builder.Configuration.GetValue("Qemu:EnableKvm", false);
    options.BootTimeoutSeconds = builder.Configuration.GetValue("Qemu:BootTimeoutSeconds", 120);
    options.AppReadyTimeoutSeconds = builder.Configuration.GetValue("Qemu:AppReadyTimeoutSeconds", 60);
});
builder.Services.AddSingleton<AlphaAppPipeline>();

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseStaticFiles();

// Headers مطلوبة لـ qemu-wasm (SharedArrayBuffer)
app.Use(async (context, next) =>
{
    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    context.Response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
    await next();
});

app.UseRouting();
app.UseCors();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// إنشاء مجلد البيانات
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
foreach (var sub in new[] { "images", "snapshots", "apps" })
    Directory.CreateDirectory(Path.Combine(dataDir, sub));

app.Run();
