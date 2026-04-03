<div align="center">

# 🧊 AlphaApp

**تجميد تطبيقات Linux كاملة وتشغيلها فورياً في المتصفح**

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![QEMU](https://img.shields.io/badge/QEMU-10.1-FF6600?logo=qemu)](https://www.qemu.org/)
[![Alpine Linux](https://img.shields.io/badge/Alpine-3.23-0D597F?logo=alpinelinux)](https://alpinelinux.org/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

[نظرة عامة](#-نظرة-عامة) · [كيف يعمل](#-كيف-يعمل) · [البدء السريع](#-البدء-السريع) · [البنية](#-البنية) · [حزمة .cph](#-حزمة-cph) · [التكامل مع Cepha](#-التكامل-مع-cepha)

</div>

---

## 🌟 نظرة عامة

**AlphaApp** هي منصة ثورية تحوّل أي تطبيق Linux إلى تطبيق يعمل **فورياً في المتصفح** — بدون انتظار إقلاع أو تثبيت.

### الفكرة الجوهرية

بدلاً من إقلاع نظام تشغيل كامل في كل مرة، نقوم بـ:

1. **إقلاع** توزيعة Linux مُجهّزة بالتطبيق المطلوب
2. **انتظار** اكتمال التشغيل وجاهزية التطبيق
3. **تجميد** حالة الآلة الافتراضية بالكامل (CPU + RAM + أجهزة)
4. **حفظ** الحالة المُجمّدة كلقطة (`savevm`)
5. **تحميل** اللقطة في [qemu-wasm](https://github.com/nicedude/nicedude) في المتصفح
6. **استعادة** فورية — التطبيق يعمل من لحظة التجميد مباشرة!

> 💡 **النتيجة:** أي تطبيق يعمل على Linux يمكنه العمل في أي متصفح، على أي جهاز، بدون خادم.

### ما يميّز AlphaApp

| الميزة | التقليدي | AlphaApp |
|--------|----------|----------|
| وقت الإقلاع | 10-60 ثانية | **فوري** (0 ثانية) |
| الاعتماديات | يجب تثبيتها | **مُضمّنة في اللقطة** |
| التوافق | يعتمد على الجهاز | **أي متصفح** |
| الخادم | مطلوب دائماً | **بدون خادم** (CDN فقط) |
| صيغة التوزيع | Docker/VM كبيرة | **حزمة .cph مضغوطة** |

---

## ⚙️ كيف يعمل

```
┌──────────────────────────────────────────────────────────────┐
│                    AlphaApp Pipeline                         │
│                                                              │
│  📦 dotnet publish ──→ 💿 qcow2 ──→ 🖥️ QEMU Boot          │
│                                          │                   │
│                                          ▼                   │
│  🌐 qemu-wasm ◄── 📦 .cph ◄── 🧊 savevm ◄── ⏳ App Ready  │
│       │                                                      │
│       ▼                                                      │
│  ▶️ إقلاع فوري في المتصفح!                                  │
└──────────────────────────────────────────────────────────────┘
```

### مراحل Pipeline التسع

| # | المرحلة | الوصف |
|---|---------|-------|
| 1 | 📦 نشر المشروع | `dotnet publish` تلقائي مع اكتشاف ذكي |
| 2 | 💿 بناء صورة القرص | إنشاء qcow2 + سكربتات التهيئة |
| 3 | ⬇️ تحميل التوزيعة | Alpine Linux virt (~67MB) |
| 4 | 🖥️ إقلاع الضيف | QEMU TCG مع QMP |
| 5 | ⏳ انتظار الجاهزية | فحص المنفذ تلقائياً |
| 6 | 🧊 تجميد الضيف | QMP `stop` — تجميد كامل |
| 7 | 💾 حفظ الحالة | `savevm` — CPU + RAM + أجهزة |
| 8 | 🌐 تجهيز WASM | إنشاء مشغّل + حزمة .cph |
| 9 | 🎉 اكتمال | جاهز للتشغيل في المتصفح! |

### بث التقدّم الحي (SSE)

التقدّم يُبث لحظياً عبر **Server-Sent Events** — بدون SignalR أو مكتبات إضافية:

```
Client ◄──── text/event-stream ────── Server
         data: {"step":3,"status":"running","percent":30}
         data: {"step":3,"status":"done","duration":0.9}
         data: {"step":4,"status":"running","percent":40}
```

---

## 🚀 البدء السريع

### المتطلبات

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [QEMU](https://www.qemu.org/) (qemu-system-x86_64 + qemu-img)

### التشغيل

```bash
git clone https://github.com/sbay-dev/AlphaApp.git
cd AlphaApp/src

# بناء
dotnet build

# تشغيل واجهة الويب
cd AlphaApp.Web
dotnet run
```

افتح المتصفح على `http://localhost:5127`

### الاستخدام

1. انقر **➕ إنشاء AlphaApp جديد**
2. استخدم **📁 استعراض** لاختيار مجلد مشروع .NET
3. النظام يُحلّل المشروع تلقائياً (اسم DLL، المنفذ، الحزم المطلوبة)
4. انقر **🚀 إنشاء وبناء** — تابع التقدّم لحظياً
5. بعد الاكتمال: **▶️ تشغيل** في المتصفح أو **📦 تحميل .cph**

---

## 🏗️ البنية

```
AlphaApp/
├── src/
│   ├── AlphaApp.Core/              # المكتبة الوسيطة (Middleware)
│   │   ├── Models/
│   │   │   ├── AlphaAppDefinition   # تعريف التطبيق
│   │   │   ├── SnapshotInfo         # بيانات اللقطة
│   │   │   ├── QemuOptions          # خيارات QEMU
│   │   │   └── GuestState           # حالة الضيف + أحداث التقدّم
│   │   ├── Services/
│   │   │   ├── QemuManager          # إدارة VM + جلسات QMP دائمة
│   │   │   ├── SnapshotService      # Pipeline من 7 خطوات
│   │   │   ├── ImageManager         # بناء صور qcow2 + تحميل Alpine
│   │   │   ├── WasmBridgeService    # جسر qemu-wasm + تصدير .cph
│   │   │   └── ProjectAnalyzer      # تحليل ذكي لمشاريع .NET
│   │   ├── Pipeline/
│   │   │   └── AlphaAppPipeline     # المنسّق الرئيسي
│   │   └── Extensions/
│   │       └── ServiceCollectionExt  # تسجيل DI
│   │
│   ├── AlphaApp.Web/                # واجهة MVC
│   │   ├── Controllers/
│   │   │   ├── AppsController       # SSE streaming + REST API
│   │   │   ├── HomeController       # لوحة القيادة
│   │   │   └── PlayerController     # مشغّل qemu-wasm
│   │   └── Views/
│   │       ├── Apps/Create          # نموذج ذكي + مستكشف ملفات
│   │       ├── Apps/Index           # قائمة اللقطات + تحميل .cph
│   │       └── Player/Index         # مشغّل في المتصفح
│   │
│   └── AlphaApp.slnx               # Solution (.NET 10 XML format)
│
└── run-dotnet.sh                    # سكربت تشغيل (Termux/proot)
```

### المكونات الرئيسية

#### QemuManager — إدارة الآلات الافتراضية
- إقلاع QEMU مع QMP على TCP
- **جلسات QMP دائمة** — اتصال واحد لكل ضيف
- أوامر: `stop` (تجميد)، `cont` (استئناف)، `savevm`، `quit`
- إصلاح حرج: حذف `arguments: null` من أوامر QMP بدون معاملات

#### ProjectAnalyzer — تحليل ذكي
- اكتشاف DLL وأمر التشغيل من `.csproj`
- استخراج المنفذ من `launchSettings.json`
- تحويل حزم NuGet إلى حزم Alpine (`Microsoft.EntityFrameworkCore.Sqlite` → `sqlite-libs`)
- نشر تلقائي (`dotnet publish`)

#### WasmBridgeService — جسر qemu-wasm
- توليد `player.html` + `alpha-loader.js` + `config.json`
- تصدير حزمة `.cph` مضغوطة

---

## 📦 حزمة .cph

**حزمة `.cph` (Cepha Package)** هي أرشيف مضغوط يحتوي كل ما يلزم لتشغيل تطبيق مُجمّد:

```
MyApp-abc123.cph (ZIP)
├── manifest.json        # بيان الحزمة
├── disk.qcow2           # صورة القرص المُجمّدة
├── player.html          # مشغّل qemu-wasm
├── alpha-loader.js      # سكربت التحميل
└── config.json          # تهيئة QEMU
```

### manifest.json

```json
{
  "format": "cepha-alpha-app",
  "version": "1.0",
  "app": {
    "name": "McpServerFull",
    "architecture": "x86_64",
    "memoryMB": 256,
    "guestPort": 5181
  },
  "runtime": {
    "engine": "qemu-wasm",
    "snapshotName": "alpha-abc123",
    "diskImage": "disk.qcow2",
    "entryPoint": "player.html"
  }
}
```

### الاستخدام

```bash
# تحميل الحزمة من واجهة AlphaApp
# أو برمجياً:
curl -O http://localhost:5127/api/apps/{id}/download-cph

# فك الضغط ونشر على أي CDN
unzip MyApp-abc123.cph -d ./deploy/
# ارفع محتويات deploy/ إلى أي استضافة ثابتة
```

---

## 🔗 التكامل مع Cepha

AlphaApp مصمّم للتكامل مع [WasmMvcRuntime](https://github.com/sbay-dev/WasmMvcRuntime) (Cepha):

| AlphaApp | Cepha |
|----------|-------|
| تطبيقات Linux كاملة عبر VM | تطبيقات .NET MVC أصلية في WASM |
| أي لغة/إطار عمل | C# / ASP.NET Core فقط |
| حجم أكبر (VM + نظام تشغيل) | حجم صغير (DLLs فقط) |
| qemu-wasm (TCG) | .NET WASM runtime |
| أبطأ (محاكاة كاملة) | أسرع (WASM أصلي) |

### سيناريو التكامل

```
Cepha App (سريع + خفيف)
    │
    ├── واجهة MVC في WASM
    ├── EF Core + SQLite في OPFS
    │
    └── عند الحاجة لخدمة خارجية:
        └── AlphaApp (VM مُجمّدة) ──→ Ollama / Redis / أي خدمة Linux
```

> حزمة `.cph` يمكن استدعاؤها من تطبيق Cepha عند الحاجة لتشغيل خدمة لا تتوفر أصلياً في WASM.

---

## 📡 واجهة برمجة التطبيقات (API)

| Method | Endpoint | الوصف |
|--------|----------|-------|
| `POST` | `/api/apps/build` | بناء AlphaApp (SSE streaming) |
| `POST` | `/api/apps/analyze` | تحليل مشروع .NET |
| `POST` | `/api/apps/publish` | نشر مشروع (dotnet publish) |
| `GET` | `/api/apps/browse?path=` | مستكشف الملفات |
| `GET` | `/api/apps/{id}/download-cph` | تحميل حزمة .cph |

---

## 🛠️ تفاصيل تقنية

### بروتوكول QMP

AlphaApp يتواصل مع QEMU عبر **QMP (QEMU Machine Protocol)** على TCP:

```
1. فتح اتصال TCP → قراءة greeting
2. إرسال qmp_capabilities → تفعيل الجلسة
3. أوامر: stop, cont, savevm, quit
4. جلسة دائمة لكل ضيف (بدون إعادة اتصال)
```

**إصلاح حرج:** QMP يرفض `"arguments": null` — يجب حذف الحقل بالكامل عند عدم وجود معاملات.

### بث SSE بدون SignalR

استخدام `Channel<T>` + `SyncProgress<T>` لضمان:
- الأحداث تصل بالترتيب الصحيح
- لا فقدان أحداث عند إنهاء Pipeline
- كتابة متزامنة على thread الطلب

### البيئة المدعومة

- ✅ Linux (أصلي)
- ✅ Termux + proot-distro (Android)
- ✅ WSL2 (Windows)
- ⚠️ macOS (يتطلب QEMU من Homebrew)

---

## 📄 الرخصة

هذا المشروع مرخّص تحت [رخصة MIT](LICENSE).

---

## 📧 التواصل

- GitHub: [@sbay-dev](https://github.com/sbay-dev)
- المستودع: [github.com/sbay-dev/AlphaApp](https://github.com/sbay-dev/AlphaApp)

<div align="center">

**⭐ إذا وجدت هذا المشروع مفيداً، لا تنسَ نجمة المستودع!**

</div>
