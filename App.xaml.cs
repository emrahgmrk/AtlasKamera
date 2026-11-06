using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using AtlasKamera.Servisler;

namespace AtlasKamera;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int surecId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Ölçüm modu: AtlasKamera.exe --sina [saniye]
        // Arayüz açmadan bütün kameralara bağlanır, akıcılık raporu yazar.
        if (e.Args.Any(a => a.Equals("--sina", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await SinamaCalistir(e.Args);
            // VLC'nin arka plan iş parçacıkları bazen kapanışı geciktiriyor:
            // ölçüm kipinde süreci kesin olarak sonlandır.
            Environment.Exit(0);
            return;
        }

        // Dışa aktarma kipi: AtlasKamera.exe --disa-aktar <dosya> [parola]
        // Kamera listesini dağıtım paketine koymak için arayüz açmadan çalışır.
        var disa = Array.FindIndex(e.Args, a =>
            a.Equals("--disa-aktar", StringComparison.OrdinalIgnoreCase));
        if (disa >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DisaAktar(e.Args.Skip(disa + 1).ToArray());
            Environment.Exit(0);
            return;
        }

        // Yakalanmamış hata uygulamayı kapatmasın: tek bir kameranın hatası
        // yüzünden bütün duvarın gitmesi kabul edilemez.
        Gunluk.YakalayicilariKur(this);
        Gunluk.Yaz($"=== ATLAS Kamera açılıyor (sürüm {GetType().Assembly.GetName().Version}) ===");

        base.OnStartup(e);
        var pencere = new MainWindow();
        MainWindow = pencere;
        pencere.Show();
    }

    /// <summary>
    /// Kamera listesini XML'e yazar (arayüzsüz).
    ///
    /// Parola verilirse kamera parolaları da pakete girer; o parola hedef
    /// bilgisayarda içe aktarırken sorulur. Verilmezse parolalar boş kalır
    /// ve her bilgisayarda elle girilmesi gerekir.
    /// </summary>
    private static void DisaAktar(string[] args)
    {
        if (!AttachConsole(-1)) AllocConsole();
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0)
        {
            Console.WriteLine("Kullanım: AtlasKamera.exe --disa-aktar <dosya.xml> [aktarma-parolası]");
            return;
        }

        var dosya = args[0];
        var parola = args.Length > 1 ? args[1] : null;

        var depo = new KameraDepo();
        try
        {
            depo.Yukle();
            depo.DisaAktar(dosya, depo.Kameralar, parola);
            Console.WriteLine();
            Console.WriteLine($"{depo.Kameralar.Count} kamera aktarıldı: {dosya}");
            Console.WriteLine(parola is null
                ? "Parolalar aktarılmadı — hedef bilgisayarda elle girilmeli."
                : "Parolalar şifreli olarak aktarıldı; içe aktarırken aynı parola sorulacak.");
        }
        catch (Exception e)
        {
            Console.WriteLine("Aktarılamadı: " + e.Message);
        }
        FreeConsole();
    }

    private static async Task SinamaCalistir(string[] args)
    {
        if (!AttachConsole(-1)) AllocConsole();
        var sure = 20;
        var i = Array.FindIndex(args, a => a.Equals("--sina", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var s) && s > 0)
            sure = s;
        var sirayla = args.Any(a => a.Equals("--sirayla", StringComparison.OrdinalIgnoreCase));

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine();
        Console.WriteLine("ATLAS Kamera — akış ölçümü");
        Console.WriteLine($"ölçüm süresi: {sure} sn · " +
                          (sirayla ? "kameralar sırayla" : "kameralar aynı anda (duvar senaryosu)"));

        // Önceki çalıştırmadan kalmış tünel varsa kapat (cihaz tek oturuma izin veriyor)
        P2PTunel.SahipsizleriTemizle();

        var depo = new KameraDepo();
        try { depo.Yukle(); }
        catch (Exception ex) { Console.WriteLine("Liste okunamadı: " + ex.Message); return; }

        if (depo.Kameralar.Count == 0)
        {
            Console.WriteLine($"Kamera tanımlı değil ({KameraDepo.Dosya}).");
            return;
        }

        using var tunel = new P2PTunel();
        using var motor = new VlcMotor(olcumKipi: true);
        var sonuclar = await AkisSinayici.HepsiniSinaAsync(
            depo.Kameralar, motor, tunel, sure, ayniAnda: !sirayla,
            bilgi: Console.WriteLine);

        var tablo = AkisSinayici.Tablo(sonuclar, sure);
        Console.WriteLine(tablo);

        // Konsol çıktısı kaybolabiliyor (çift tıkla çalıştırma, yönlendirme):
        // rapor her hâlükârda ayarlar klasörüne de yazılır.
        try
        {
            var dosya = System.IO.Path.Combine(KameraDepo.Klasor, "son-olcum.txt");
            System.IO.Directory.CreateDirectory(KameraDepo.Klasor);
            System.IO.File.WriteAllText(dosya,
                $"ATLAS Kamera ölçümü — {DateTime.Now:dd.MM.yyyy HH:mm}{Environment.NewLine}{tablo}");
            Console.WriteLine($"Rapor: {dosya}");
        }
        catch { }
        FreeConsole();
    }
}
