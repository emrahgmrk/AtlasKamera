using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace AtlasKamera.Servisler;

/// <summary>Yayımlanmış bir sürümün bilgisi.</summary>
public record Surum(string Etiket, Version Numara, string Baslik, string Notlar,
                    string IndirmeAdresi, long Boyut);

/// <summary>
/// GitHub Releases üzerinden uzaktan güncelleme.
///
/// Uygulama şantiyelerdeki ayrı bilgisayarlara kopyalanarak dağıtılıyor;
/// her birine elle yeni klasör taşımak yerine, yeni sürüm yayımlanınca
/// uygulamanın kendisi haber verir ve tek tıkla kurar.
///
/// NASIL ÇALIŞIR
/// -------------
/// 1. Açılışta (ve sonra günde bir) Releases API'sine bakılır.
/// 2. Etiket (v1.1.0) çalışan sürümden yeniyse üst çubukta düğme belirir.
/// 3. Kullanıcı onaylarsa paket indirilir, geçici klasöre açılır ve küçük bir
///    toplu iş dosyası devreye girer: uygulama kapanır, dosyalar değiştirilir,
///    uygulama yeniden açılır. Kendi üzerine yazamayan çalışan bir süreç
///    olduğumuz için bu adım dışarıdan yürütülmek zorunda.
///
/// Ayarlar (%APPDATA%\AtlasKamera) güncellemeden etkilenmez: kamera listesi
/// ayrı klasörde durur, yalnız program dosyaları değişir.
/// </summary>
public static class Guncelleyici
{
    /// <summary>Sürümlerin yayımlandığı depo.</summary>
    public const string Depo = "emrahgmrk/AtlasKamera";

    private static readonly HttpClient _istemci = Olustur();

    private static HttpClient Olustur()
    {
        var i = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub API kimliksiz isteklerde bile User-Agent istiyor
        i.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AtlasKamera", SuAnkiSurum.ToString()));
        i.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return i;
    }

    public static Version SuAnkiSurum =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    /// <summary>
    /// Yayımlanmış en son sürümü sorar. Yeni sürüm yoksa ya da ağ yoksa null döner
    /// — güncelleme denetimi hiçbir zaman uygulamayı engellememeli.
    /// </summary>
    public static async Task<Surum?> YeniSurumVarMi()
    {
        try
        {
            var adres = $"https://api.github.com/repos/{Depo}/releases/latest";
            using var yanit = await _istemci.GetAsync(adres);
            if (!yanit.IsSuccessStatusCode)
            {
                Gunluk.Yaz($"güncelleme denetimi: HTTP {(int)yanit.StatusCode}");
                return null;
            }

            using var akis = await yanit.Content.ReadAsStreamAsync();
            using var belge = await JsonDocument.ParseAsync(akis);
            var kok = belge.RootElement;

            var etiket = kok.GetProperty("tag_name").GetString() ?? "";
            if (!SurumAyikla(etiket, out var numara)) return null;
            if (numara <= SuAnkiSurum) return null;              // zaten güncel

            // Paket: .zip uzantılı ilk varlık
            var varlik = kok.TryGetProperty("assets", out var liste)
                ? liste.EnumerateArray().FirstOrDefault(v =>
                    (v.GetProperty("name").GetString() ?? "").EndsWith(".zip",
                        StringComparison.OrdinalIgnoreCase))
                : default;
            if (varlik.ValueKind != JsonValueKind.Object)
            {
                Gunluk.Yaz($"güncelleme {etiket} bulundu ama paketi (.zip) yok");
                return null;
            }

            return new Surum(
                etiket,
                numara,
                kok.TryGetProperty("name", out var b) ? (b.GetString() ?? etiket) : etiket,
                kok.TryGetProperty("body", out var n) ? (n.GetString() ?? "") : "",
                varlik.GetProperty("browser_download_url").GetString() ?? "",
                varlik.TryGetProperty("size", out var s) ? s.GetInt64() : 0);
        }
        catch (Exception e)
        {
            // Ağ yok, DNS yok, GitHub kapalı — hiçbiri kullanıcıyı ilgilendirmez
            Gunluk.Yaz("güncelleme denetimi yapılamadı: " + e.Message);
            return null;
        }
    }

    /// <summary>"v1.2.3" ya da "1.2.3" → Version.</summary>
    private static bool SurumAyikla(string etiket, out Version numara)
    {
        numara = new Version(0, 0);
        var temiz = etiket.TrimStart('v', 'V').Trim();
        if (!Version.TryParse(temiz, out var v)) return false;
        // 1.2 → 1.2.0.0 (karşılaştırma tutarlı olsun)
        numara = new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        return true;
    }

    /// <summary>
    /// Paketi indirir, açar ve değiştirme işini bir toplu iş dosyasına devredip
    /// uygulamayı kapatır. Dönerse kurulum başlamamıştır.
    /// </summary>
    public static async Task<string?> IndirVeKur(Surum surum, IProgress<int>? ilerleme = null)
    {
        var klasor = Path.Combine(Path.GetTempPath(), "AtlasKameraGuncelleme");
        var paket = Path.Combine(klasor, $"{surum.Etiket}.zip");
        var acilan = Path.Combine(klasor, surum.Etiket);
        try
        {
            if (Directory.Exists(klasor)) Directory.Delete(klasor, true);
            Directory.CreateDirectory(klasor);

            Gunluk.Yaz($"güncelleme indiriliyor: {surum.Etiket} ({surum.Boyut / 1024 / 1024} MB)");
            using (var yanit = await _istemci.GetAsync(surum.IndirmeAdresi,
                       HttpCompletionOption.ResponseHeadersRead))
            {
                yanit.EnsureSuccessStatusCode();
                var toplam = yanit.Content.Headers.ContentLength ?? surum.Boyut;
                using var kaynak = await yanit.Content.ReadAsStreamAsync();
                using var hedef = File.Create(paket);
                var tampon = new byte[81920];
                long yazilan = 0;
                int okunan;
                while ((okunan = await kaynak.ReadAsync(tampon)) > 0)
                {
                    await hedef.WriteAsync(tampon.AsMemory(0, okunan));
                    yazilan += okunan;
                    if (toplam > 0) ilerleme?.Report((int)(yazilan * 100 / toplam));
                }
            }

            ZipFile.ExtractToDirectory(paket, acilan, overwriteFiles: true);

            // Paketin içinde tek bir klasör varsa asıl içerik odur
            var icerik = acilan;
            var altlar = Directory.GetDirectories(acilan);
            if (altlar.Length == 1 && Directory.GetFiles(acilan).Length == 0)
                icerik = altlar[0];

            if (!File.Exists(Path.Combine(icerik, "AtlasKamera.exe")))
                return "Paketin içinde AtlasKamera.exe yok.";

            YenidenBaslatarakDegistir(icerik);
            return null;      // buraya dönülürse uygulama kapanmak üzeredir
        }
        catch (Exception e)
        {
            Gunluk.Hata("güncelleme kurulumu", e);
            return e.Message;
        }
    }

    /// <summary>
    /// Çalışan uygulama kendi dosyalarının üzerine yazamaz: işi bir toplu iş
    /// dosyası yapar — uygulamanın kapanmasını bekler, dosyaları kopyalar,
    /// uygulamayı yeniden açar ve kendini siler.
    /// </summary>
    private static void YenidenBaslatarakDegistir(string kaynakKlasor)
    {
        var hedef = AppContext.BaseDirectory.TrimEnd('\\');
        var exe = Path.Combine(hedef, "AtlasKamera.exe");
        var betik = Path.Combine(Path.GetTempPath(), "atlaskamera-guncelle.cmd");
        var pid = Environment.ProcessId;

        File.WriteAllText(betik, $"""
            @echo off
            rem ATLAS Kamera güncelleme betiği — uygulama tarafından üretildi
            echo Guncelleme kuruluyor, lutfen bekleyin...
            rem Uygulamanin kapanmasini bekle (en fazla 30 sn)
            for /l %%i in (1,1,30) do (
                tasklist /FI "PID eq {pid}" | find "{pid}" >nul || goto :kopyala
                timeout /t 1 /nobreak >nul
            )
            :kopyala
            robocopy "{kaynakKlasor}" "{hedef}" /E /IS /IT /R:2 /W:2 /NFL /NDL /NJH /NJS >nul
            if errorlevel 8 (
                echo Kopyalama basarisiz. Dosyalar: {kaynakKlasor}
                pause
                exit /b 1
            )
            start "" "{exe}"
            rem Betik kendini siler
            (goto) 2>nul & del "%~f0"
            """, System.Text.Encoding.Default);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{betik}\"")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Minimized,
            WorkingDirectory = Path.GetTempPath(),
        });

        Gunluk.Yaz("güncelleme betiği başlatıldı, uygulama kapanıyor");
        System.Windows.Application.Current.Shutdown();
    }
}
