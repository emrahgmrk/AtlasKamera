using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AtlasKamera.Servisler;

/// <summary>
/// Dahua P2P tüneli — uzak şantiyedeki NVR'a bulut üzerinden bağlanır.
///
/// Cihazın yerel ağına erişim olmadığında (başka şantiye, VPN yok) Dahua'nın
/// kendi bulutu üzerinden bir aktarma oturumu kurulur. İşi <c>dh-p2p.exe</c>
/// yapar: seri numarasıyla cihaza bağlanıp RTSP'yi (554) ve web/CGI portunu
/// (80) yerel portlara aktarır. Oynatıcı ve PTZ sonra yalnız 127.0.0.1'e konuşur.
///
/// NVR tek P2P oturumuna izin verdiğinden tünel seri numarası başına birdir ve
/// kapandıktan hemen sonra yeniden açılmaz (cihaz önceki oturumu bırakana dek
/// kısa bir soğuma beklenir); üst üste açıp kapatmak cihazın oturum yuvasını
/// uzun süre tıkar.
/// </summary>
public sealed class P2PTunel : IDisposable
{
    /// <summary>
    /// RTSP tünellerinin başlangıç portu; web/CGI portu bunun 100 fazlası.
    /// ATLAS (web arayüzü) 8700-8800 aralığını kullanıyor; aynı makinede ikisi
    /// birden çalışabilsin diye burada 8900'den başlanır.
    /// </summary>
    private const int IlkPort = 8900;
    private const int WebPortFarki = 100;
    /// <summary>Kapanan tünel bu kadar beklemeden yeniden açılmaz.</summary>
    private const int SogumaSn = 30;

    /// <summary>
    /// Bir tünel bu süre dolmadan yeniden tazelenmez.
    ///
    /// Aynı NVR'a bağlı birden çok kamera (ör. iki kanal) aynı tüneli
    /// paylaşıyor. Hepsi ayrı ayrı "akış gelmiyor" deyip tüneli tazeleyince
    /// dakikada birkaç kez açılıp kapanıyor ve cihazın tek oturum yuvası
    /// uzun süre tıkanıyordu.
    /// </summary>
    private const int TazelemeAraligiSn = 120;

    /// <summary>Aktarmadan bu kadar süre veri gelmezse dh-p2p kendini kapatır.</summary>
    private const int SessizlikSn = 90;

    private sealed class Tunel
    {
        public string Seri = "";
        public int Port;
        public Process? Surec;
        public DateTime Bitis;
        public string Hata = "";
        public int Deneme;
        /// <summary>dh-p2p "Ready to connect" dedi mi — akış ancak o zaman gelir.</summary>
        public volatile bool Hazir;
        /// <summary>Son tazeleme/zorla kapatma anı (çok sık yapılmasın diye).</summary>
        public DateTime SonTazeleme;
    }

    private readonly ConcurrentDictionary<string, Tunel> _tuneller = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _kilit = new();
    private readonly SurecKafesi _kafes = new();
    private int _sonrakiPort = IlkPort;

    public static string DhP2pYolu
    {
        get
        {
            // Önce uygulamanın yanı, sonra ATLAS kurulumu
            var yaninda = Path.Combine(AppContext.BaseDirectory, "dh-p2p.exe");
            if (File.Exists(yaninda)) return yaninda;
            return @"C:\ATLAS\kamera\dh-p2p.exe";
        }
    }

    public static bool Kurulu => File.Exists(DhP2pYolu);

    public static string GunlukKlasoru => Path.Combine(KameraDepo.Klasor, "gunluk");

    /// <summary>Seri numarası için ayrılmış RTSP portu (tünel kapalıyken de sabit).</summary>
    public int Port(string seri)
    {
        var t = _tuneller.GetOrAdd(Normalle(seri), s =>
        {
            lock (_kilit)
            {
                var port = _sonrakiPort;
                _sonrakiPort += 2;      // RTSP + yedek
                return new Tunel { Seri = s, Port = port };
            }
        });
        return t.Port;
    }

    public int WebPort(string seri) => Port(seri) + WebPortFarki;

    /// <summary>
    /// Önceki çalıştırmadan kalmış kendi tünellerimizi kapatır.
    ///
    /// Süreç kafesi uygulama çökse bile tünelleri kapatır, ama kafesin
    /// kurulamadığı durumlar (ilkelerin engellediği ortamlar) için ikinci bir
    /// emniyet: bizim port aralığımızı dinleyen dh-p2p süreçleri sonlandırılır.
    /// ATLAS'ın kendi tünellerine (8700-8800) dokunulmaz.
    /// </summary>
    public static void SahipsizleriTemizle()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("dh-p2p"))
            {
                try
                {
                    // Yalnız bizim klasörümüzden çalışan süreçler
                    var yol = p.MainModule?.FileName;
                    if (yol is null) { p.Dispose(); continue; }
                    if (!string.Equals(Path.GetFullPath(yol), Path.GetFullPath(DhP2pYolu),
                                       StringComparison.OrdinalIgnoreCase))
                    {
                        p.Dispose();
                        continue;
                    }
                    if (p.Id == Environment.ProcessId) { p.Dispose(); continue; }
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(2000);
                }
                catch { /* erişilemeyen süreç: atla */ }
                finally { p.Dispose(); }
            }
        }
        catch { }
    }

    public string SonHata(string seri) =>
        _tuneller.TryGetValue(Normalle(seri), out var t) ? t.Hata : "";

    public bool Calisiyor(string seri) =>
        _tuneller.TryGetValue(Normalle(seri), out var t)
        && t.Surec is { HasExited: false } && t.Hazir;

    /// <summary>
    /// Tüneli açar (açıksa dokunmaz) ve RTSP portu bağlantı kabul edene kadar bekler.
    /// </summary>
    public async Task<bool> HazirlaAsync(string seri, int bekleSn = 25, CancellationToken iptal = default)
    {
        seri = Normalle(seri);
        if (string.IsNullOrEmpty(seri)) return false;
        if (!Kurulu)
        {
            if (_tuneller.TryGetValue(seri, out var t0))
                t0.Hata = "dh-p2p.exe bulunamadı (uygulama klasörü ya da C:\\ATLAS\\kamera).";
            return false;
        }

        var port = Port(seri);
        var t = _tuneller[seri];

        if (t.Surec is { HasExited: false } && t.Hazir && await PortAcikMi(port, 400, iptal))
            return true;

        lock (_kilit)
        {
            if (t.Surec is { HasExited: true } or null)
            {
                var kalan = SogumaSn - (DateTime.UtcNow - t.Bitis).TotalSeconds;
                if (t.Bitis != default && kalan > 0)
                {
                    t.Hata = $"cihaz önceki oturumu bıraksın diye bekleniyor ({(int)kalan} sn)";
                    return false;
                }
                Baslat(t);
            }
        }

        var bitis = DateTime.UtcNow.AddSeconds(bekleSn);
        while (DateTime.UtcNow < bitis && !iptal.IsCancellationRequested)
        {
            if (t.Surec is { HasExited: true })
            {
                t.Hata = string.IsNullOrEmpty(t.Hata) ? "tünel beklenmedik şekilde kapandı" : t.Hata;
                return false;
            }
            // Önce cihazla el sıkışma bitmeli, sonra port denenir
            if (t.Hazir && await PortAcikMi(port, 400, iptal))
            {
                t.Deneme = 0;
                return true;
            }
            await Task.Delay(400, CancellationToken.None);
        }
        if (string.IsNullOrEmpty(t.Hata))
            t.Hata = t.Hazir
                ? "tünel açıldı ama bağlantı kabul etmedi"
                : "cihaz buluta yanıt vermedi (kapalı ya da ağı yok olabilir)";

        // El sıkışma tamamlanmadıysa süreç aktarma sunucusunda asılı kalmış
        // olabilir (işlemci boşta, çıktı yok). Bir sonraki deneme temiz
        // başlasın diye kapatılır; soğuma süresi cihazı korur.
        if (!t.Hazir)
        {
            lock (_kilit)
            {
                // Aynı tünele bağlı diğer kamera da aynı anda buraya düşüyor;
                // ikisi birden kapatmasın.
                if (t.Surec is { HasExited: false }
                    && (DateTime.UtcNow - t.SonTazeleme).TotalSeconds >= TazelemeAraligiSn)
                {
                    t.SonTazeleme = DateTime.UtcNow;
                    Oldur(t);
                    Gunluk.Yaz($"P2P tüneli yanıt vermedi, kapatıldı: {seri}");
                }
            }
        }
        return false;
    }

    private void Baslat(Tunel t)
    {
        try
        {
            Directory.CreateDirectory(GunlukKlasoru);
            var gunluk = Path.Combine(GunlukKlasoru, $"dhp2p-{t.Seri}.log");
            try
            {
                if (File.Exists(gunluk) && new FileInfo(gunluk).Length > 5 * 1024 * 1024)
                    File.Delete(gunluk);
            }
            catch { /* günlük silinemezse önemli değil */ }

            var psi = new ProcessStartInfo(DhP2pYolu)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(DhP2pYolu) ?? AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add("--relay");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add($"127.0.0.1:{t.Port}:554");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"127.0.0.1:{t.Port + WebPortFarki}:80");
            psi.ArgumentList.Add(t.Seri);
            psi.Environment["DHP2P_SESSIZLIK"] = SessizlikSn.ToString();

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var yazici = new object();
            void Yaz(string? satir)
            {
                if (satir is null) return;
                try
                {
                    lock (yazici)
                        File.AppendAllText(gunluk, $"{DateTime.Now:HH:mm:ss} {satir}{Environment.NewLine}");
                }
                catch { /* günlüğe yazılamazsa akış sürsün */ }
            }
            void Incele(string? satir)
            {
                if (string.IsNullOrEmpty(satir)) return;
                // dh-p2p bulut el sıkışmasını bitirince bunu yazar; öncesinde
                // port açık olsa bile RTSP isteği boş döner.
                if (satir.Contains("Ready to connect", StringComparison.OrdinalIgnoreCase))
                {
                    t.Hazir = true;
                    t.Hata = "";
                }
                else if (satir.Contains("panicked", StringComparison.OrdinalIgnoreCase)
                         || satir.Contains("no entry found", StringComparison.OrdinalIgnoreCase))
                {
                    // dh-p2p'nin yığın izi kullanıcıya bir şey anlatmıyor;
                    // bu panik bulutun cihaz için boş kayıt döndürmesinden oluyor.
                    t.Hata = "cihaz bulutta kayıtlı görünmüyor (seri numarası yanlış "
                             + "ya da cihaz buluta hiç bağlanmamış olabilir)";
                }
                else if (satir.StartsWith("error", StringComparison.OrdinalIgnoreCase))
                {
                    t.Hata = satir.Length > 200 ? satir[..200] : satir;
                }
            }

            p.OutputDataReceived += (_, e) => { Incele(e.Data); Yaz(e.Data); };
            p.ErrorDataReceived += (_, e) => { Incele(e.Data); Yaz(e.Data); };
            p.Exited += (_, _) =>
            {
                t.Bitis = DateTime.UtcNow;
                Gunluk.Yaz($"P2P tüneli kapandı: {t.Seri}");
            };

            p.Start();
            Gunluk.Yaz($"P2P tüneli başlatıldı: {t.Seri} → 127.0.0.1:{t.Port} (pid {p.Id})");
            // Uygulama çökse/zorla kapatılsa bile tünel arkada kalmasın
            _kafes.Ekle(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            t.Surec = p;
            t.Deneme++;
            t.Hata = "";
            t.Hazir = false;
        }
        catch (Exception e)
        {
            t.Hata = "tünel başlatılamadı: " + e.Message;
        }
    }

    private static async Task<bool> PortAcikMi(int port, int zamanAsimiMs, CancellationToken iptal)
    {
        var istemci = new TcpClient();
        try
        {
            var gorev = istemci.ConnectAsync("127.0.0.1", port, iptal).AsTask();
            var bitti = await Task.WhenAny(gorev, Task.Delay(zamanAsimiMs, CancellationToken.None));
            if (bitti != gorev)
            {
                // Zaman aşımı: bağlantı görevi arkada tamamlanacak. Hatası
                // gözlemlenmezse sonlandırıcı iş parçacığı onu yeniden fırlatıyor
                // ("unobserved task exception"), bu yüzden burada yutulur.
                _ = gorev.ContinueWith(t => { _ = t.Exception; },
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                _ = gorev.ContinueWith(_ => istemci.Dispose(),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                return false;
            }
            var acik = !gorev.IsFaulted && istemci.Connected;
            istemci.Dispose();
            return acik;
        }
        catch
        {
            try { istemci.Dispose(); } catch { }
            return false;
        }
    }

    public void Kapat(string seri)
    {
        if (!_tuneller.TryGetValue(Normalle(seri), out var t)) return;
        lock (_kilit) Oldur(t);
    }

    /// <summary>
    /// Tüneli kapatır ki bir sonraki istek yenisini kursun.
    ///
    /// Tünel ayakta ve port açık olduğu hâlde kameradan görüntü gelmediği
    /// durumlar oluyor: aktarma oturumu cihaz tarafında ölmüş ama tünel bunu
    /// bilmiyor. Kart birkaç kez boşa denedikten sonra burayı çağırır.
    /// Soğuma süresi (<see cref="SogumaSn"/>) cihazın oturumu bırakmasına
    /// zaman tanır; ardı ardına tazeleme cihazın tek oturum yuvasını tıkar.
    /// </summary>
    public void Tazele(string seri)
    {
        seri = Normalle(seri);
        if (!_tuneller.TryGetValue(seri, out var t)) return;
        lock (_kilit)
        {
            if (t.Surec is not { HasExited: false }) return;
            // Aynı tüneli paylaşan kameralar sırayla tazeleme istiyor:
            // aralığa uymayan istekler yok sayılır.
            if ((DateTime.UtcNow - t.SonTazeleme).TotalSeconds < TazelemeAraligiSn) return;
            t.SonTazeleme = DateTime.UtcNow;
            Gunluk.Yaz($"P2P tüneli tazeleniyor: {seri} (akış gelmiyordu)");
            Oldur(t);
        }
    }

    /// <summary>Artık kullanılmayan serilerin tünellerini kapatır.</summary>
    public void Temizle(IEnumerable<string> gerekenSeriler)
    {
        var gereken = new HashSet<string>(gerekenSeriler.Select(Normalle), StringComparer.OrdinalIgnoreCase);
        foreach (var (seri, t) in _tuneller.ToArray())
            if (!gereken.Contains(seri) && t.Surec is { HasExited: false })
                lock (_kilit) Oldur(t);
    }

    private static void Oldur(Tunel t)
    {
        t.Hazir = false;
        try
        {
            if (t.Surec is { HasExited: false })
            {
                t.Surec.Kill(entireProcessTree: true);
                t.Surec.WaitForExit(3000);
            }
        }
        catch { /* süreç zaten kapanmış olabilir */ }
        finally
        {
            t.Surec?.Dispose();
            t.Surec = null;
            t.Bitis = DateTime.UtcNow;
        }
    }

    public static string Normalle(string? seri) => (seri ?? "").Trim().ToUpperInvariant();

    public void Dispose()
    {
        foreach (var t in _tuneller.Values)
            lock (_kilit) Oldur(t);
        _kafes.Dispose();
    }
}
