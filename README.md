# Kurfamüzik

Kurfamüzik Web oynatıcısı ([kurfanova.kurfa.workers.dev](https://kurfanova.kurfa.workers.dev) / [kurfanova.github.io](https://kurfanova.github.io)) için geliştirilen **Windows Masaüstü (.NET 10)** ve **Android** yerel istemci uygulamasıdır.

Canlı arayüz bulut üzerinden güncellenir; uygulama kodundaki yerleşik medya çözücü, sistem entegrasyonu ve çevrimdışı yerel müzik motoru ise bu depodan derlenir.

## İndirme

En güncel kurulum dosyalarına [Releases](https://github.com/KurfaCore/kurfanova-app/releases/latest) sayfasından veya aşağıdaki doğrudan bağlantılardan ulaşabilirsiniz:

| Platform | Paket | Gereksinim | Bağlantı |
| :--- | :--- | :--- | :--- |
| **Windows** | `Kurfanova-Muzik-Windows.exe` | Windows 10 / 11 (x64) | [EXE İndir](https://github.com/KurfaCore/kurfanova-app/releases/latest/download/Kurfanova-Muzik-Windows.exe) |
| **Android** | `Kurfanova-Muzik.apk` | Android 8.0+ | [APK İndir](https://github.com/KurfaCore/kurfanova-app/releases/latest/download/Kurfanova-Muzik.apk) |

> Windows kurulum paketi `.NET 10` çalışma zamanını tek dosya içinde barındırır (`self-contained`), harici bağımlılık kurulumu gerektirmez.

## Özellikler

- **Yerel Ses ve İndirme Motoru (`127.0.0.1`):** HTTP `Range` destekli yerel akış sunucusu, cihazdaki yerel ses dosyalarını (MP3, M4A, WAV, FLAC, OGG, AAC, OPUS) gerçek süreleriyle okuma ve seçilen klasöre MP3/MP4 kaydetme.
- **Windows Masaüstü Entegrasyonu:** Şeffaf katmanlı sistem tepsisi menüsü (`TrayMenuForm`) ve kayan mini oynatıcı (`MiniPlayerForm`), kalıcı kullanıcı verisi (`user-data.json`), başlangıç ve kısayol tercihleri.
- **Android Arka Plan ve Kilit Ekranı Desteği:** `ForegroundService` ve `MediaSession` entegrasyonu ile ekran kapalıyken kesintisiz çalma, bildirim kontrolleri ve `MediaStore` yerel müzik tarama.
- **Otomatik Güncelleme & Yedek Bağlantı:** `win-update.json` ve `apk-update.json` üzerinden SHA-256 doğrulamalı güncelleme denetimi; birincil sunucuya erişilemediğinde otomatik olarak yedek Worker adresine geçiş.
- **Türkçe ve İngilizce Dil Desteği:** Web arayüzü, masaüstü pencereleri ve tepsi menüsü için tam Türkçe / İngilizce (`TR` / `EN`) dil geçişi.

## Proje Yapısı

```text
kurfanova-app/
├── desktop-windows/         # Windows masaüstü istemcisi (C# / .NET 10 WinForms + WebView2)
│   ├── MainForm.cs          # WebView2 barındırıcı, JS bridge, DWM ve sistem tepsisi yönetimi
│   ├── NativeDesktopEngine.cs # Yerel HTTP Range ses sunucusu, medya çözücü ve dosya yönetimi
│   ├── MiniPlayerForm.cs    # 32-bit şeffaf katmanlı kayan mini oynatıcı
│   ├── TrayMenuForm.cs      # Windows 10/11 uyumlu sistem tepsisi menüsü
│   ├── AutoUpdater.cs       # SHA-256 doğrulamalı masaüstü güncelleme motoru
│   └── SetupForm.cs         # İlk kurulum sihirbazı
├── app/                     # Android mobil istemcisi (Java WebView + MediaSession servisi)
└── .github/workflows/       # Windows EXE ve Android APK otomatik derleme akışları
```

## Kaynak Koddan Derleme

### Windows Masaüstü (.NET 10 SDK)

```powershell
dotnet build desktop-windows/KurfanovaMuzik.csproj -c Release
```

Tek dosya çalıştırılabilir paket üretmek için:

```powershell
dotnet publish desktop-windows/KurfanovaMuzik.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist-win
```

### Android APK (JDK 17 & Android SDK)

```bash
gradle assembleDebug
```
