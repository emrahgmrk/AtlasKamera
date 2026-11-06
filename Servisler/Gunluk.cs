using System;
using System.IO;
using System.Text;
using System.Threading;

namespace AtlasKamera.Servisler;

/// <summary>
/// Basit dosya günlüğü — %APPDATA%\AtlasKamera\gunluk\uygulama.log
///
/// Kamera uygulaması saatlerce açık kalıyor ve sorunlar (kopma, yeniden
/// bağlanma, çökme) kimse bakmıyorken oluyor. Ekranda görünmeyen bu olayların
/// sonradan incelenebilmesi için her şey dosyaya yazılır.
/// </summary>
public static class Gunluk
{
    private const long AzamiBoyut = 2 * 1024 * 1024;
    private static readonly object _kilit = new();
    private static string? _yol;

    public static string Yol
    {
        get
        {
            if (_yol is not null) return _yol;
            var klasor = Path.Combine(KameraDepo.Klasor, "gunluk");
            Directory.CreateDirectory(klasor);
            _yol = Path.Combine(klasor, "uygulama.log");
            return _yol;
        }
    }

    public static void Yaz(string mesaj)
    {
        try
        {
            lock (_kilit)
            {
                Dondur();
                File.AppendAllText(Yol,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.CurrentManagedThreadId,3}] {mesaj}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch { /* günlüğe yazamamak uygulamayı durdurmasın */ }
    }

    public static void Hata(string nerede, Exception e) =>
        Yaz($"HATA {nerede}: {e.GetType().Name}: {e.Message}{Environment.NewLine}{e.StackTrace}");

    private static void Dondur()
    {
        try
        {
            if (!File.Exists(Yol) || new FileInfo(Yol).Length <= AzamiBoyut) return;
            var onceki = Yol[..^4] + ".onceki.log";
            if (File.Exists(onceki)) File.Delete(onceki);
            File.Move(Yol, onceki);
        }
        catch { }
    }

    /// <summary>
    /// Yakalanmamış hataları dosyaya yazar ve mümkünse uygulamayı ayakta tutar.
    ///
    /// Arayüz iş parçacığındaki bir hata WPF'de uygulamayı kapatır; kamera
    /// duvarının tek bir karttan ötürü komple kapanması kabul edilebilir
    /// değil, bu yüzden hata yutulup günlüğe yazılır.
    /// </summary>
    public static void YakalayicilariKur(System.Windows.Application uygulama)
    {
        uygulama.DispatcherUnhandledException += (_, e) =>
        {
            Hata("arayüz", e.Exception);
            e.Handled = true;                 // duvar ayakta kalsın
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Hata("uygulama", ex);
            else Yaz("HATA uygulama: " + e.ExceptionObject);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Hata("arka plan görevi", e.Exception);
            e.SetObserved();
        };
    }
}
