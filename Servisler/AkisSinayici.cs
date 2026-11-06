using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AtlasKamera.Modeller;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace AtlasKamera.Servisler;

/// <summary>Bir akışın belirli süredeki akıcılık ölçümü.</summary>
public record SinamaSonucu(
    string Ad,
    bool Bagli,
    string Mesaj,
    uint Genislik,
    uint Yukseklik,
    string Kodek,
    /// <summary>Akıcılık yüzdesi: gelen görüntü süresi / geçen süre (100 = kusursuz).</summary>
    int AkicilikYuzde,
    /// <summary>Tamponlama + kopma sayısı.</summary>
    int DuraklamaSayisi,
    /// <summary>Kaynağın kare hızı.</summary>
    float Fps,
    double AcilisSn,
    /// <summary>Tamponlamada geçen toplam süre (sn).</summary>
    double EnBuyukDuraklamaSn);

/// <summary>
/// Kameraları arka arkaya (ya da hepsini birden) açıp akıcılık ölçer.
///
/// Donma şikâyetini rakamla konuşabilmek için var: kaç kare gösterildi, kaç
/// kare düştü, akış kaç Mbit/s taşıyor, görüntü kaç saniyede açıldı.
/// Komut satırından <c>AtlasKamera.exe --sina</c> ile çalışır.
/// </summary>
public static class AkisSinayici
{
    public static async Task<List<SinamaSonucu>> HepsiniSinaAsync(
        IEnumerable<Kamera> kameralar, VlcMotor motor, P2PTunel tunel,
        int olcumSn = 20, bool ayniAnda = true, Action<string>? bilgi = null)
    {
        var liste = kameralar.Where(k => k.Aktif).ToList();
        var sonuclar = new List<SinamaSonucu>();

        // P2P tünelleri önce açılır (ölçüm süresi tünel beklemesiyle kirlenmesin)
        foreach (var k in liste.Where(x => x.Baglanti == BaglantiTuru.DahuaP2P)
                               .GroupBy(x => P2PTunel.Normalle(x.SeriNo)).Select(g => g.First()))
        {
            bilgi?.Invoke($"  tünel açılıyor: {k.SeriNo} …");
            var ok = await tunel.HazirlaAsync(k.SeriNo, 30);
            bilgi?.Invoke(ok ? "    hazır" : $"    açılamadı: {tunel.SonHata(k.SeriNo)}");
        }

        if (ayniAnda)
        {
            // Arayüzdeki duvar gibi kademeli başlat: hepsini aynı anda açmak
            // NVR'a bir çırpıda onlarca RTSP isteği gönderiyor, cihaz bir
            // kısmını geri çeviriyor ve ölçüm olduğundan kötü çıkıyordu.
            var gorevler = new List<Task<SinamaSonucu>>();
            foreach (var k in liste)
            {
                var kamera = k;
                gorevler.Add(Task.Run(() => Sina(kamera, motor, tunel, olcumSn)));
                await Task.Delay(350);
            }
            sonuclar.AddRange(await Task.WhenAll(gorevler));
        }
        else
        {
            foreach (var k in liste)
            {
                bilgi?.Invoke($"  {k.Ad} …");
                sonuclar.Add(await Task.Run(() => Sina(k, motor, tunel, olcumSn)));
            }
        }
        return sonuclar;
    }

    private static SinamaSonucu Sina(Kamera k, VlcMotor motor, P2PTunel tunel, int olcumSn,
                                     bool anaAkisZorla = false)
    {
        MediaPlayer? oynatici = null;
        Media? ortam = null;
        var baslangic = DateTime.UtcNow;
        var yenidenDeneme = !anaAkisZorla;
        try
        {
            var anaAkis = anaAkisZorla || !k.DuvardaAltAkis;
            var adres = Adresler.Rtsp(k, anaAkis, tunel);
            var tampon = k.Baglanti == BaglantiTuru.DahuaP2P
                ? VlcMotor.UzakTampon : VlcMotor.YerelTampon;
            (oynatici, ortam) = motor.Olustur(adres, tampon);

            using var oynuyor = new ManualResetEventSlim(false);
            var hata = false;
            oynatici.Playing += (_, _) => oynuyor.Set();
            oynatici.EncounteredError += (_, _) => { hata = true; oynuyor.Set(); };

            // Tamponlama = akışın kesilip yeniden dolması; donmanın ölçüsü bu.
            var tamponlama = 0;
            var tamponluSure = 0.0;
            DateTime? tamponBasi = null;
            var tamponKilidi = new object();
            oynatici.Buffering += (_, e) =>
            {
                lock (tamponKilidi)
                {
                    if (e.Cache < 100f)
                    {
                        tamponBasi ??= DateTime.UtcNow;
                    }
                    else if (tamponBasi is { } bas)
                    {
                        tamponlama++;
                        tamponluSure += (DateTime.UtcNow - bas).TotalSeconds;
                        tamponBasi = null;
                    }
                }
            };

            oynatici.Play(ortam);
            if (!oynuyor.Wait(TimeSpan.FromSeconds(15)) || hata)
            {
                Kapat(oynatici, ortam);
                oynatici = null; ortam = null;
                // Alt akış açılmadıysa (bazı NVR kanallarında kapalı) ana akışı dene
                if (!anaAkis && yenidenDeneme)
                    return Sina(k, motor, tunel, olcumSn, anaAkisZorla: true);
                return Basarisiz(k, hata ? "bağlanamadı (kimlik ya da yol hatalı olabilir)"
                                         : "yanıt yok (15 sn)");
            }

            var acilis = (DateTime.UtcNow - baslangic).TotalSeconds;

            uint g = 0, y = 0;
            for (var i = 0; i < 20 && g == 0; i++)
            {
                Thread.Sleep(150);
                try { oynatici.Size(0, ref g, ref y); } catch { }
            }

            // Ölçüm: akış bu süre boyunca kaç kez tamponlamaya düştü ve
            // toplam ne kadar durdu. Oynatma kesintisiz ise hiç tamponlama olmaz.
            var kaynakFps = oynatici.Fps > 1 ? oynatici.Fps : 0f;
            var ilkAn = DateTime.UtcNow;
            var kopma = 0;
            oynatici.EndReached += (_, _) => Interlocked.Increment(ref kopma);
            oynatici.EncounteredError += (_, _) => Interlocked.Increment(ref kopma);

            Thread.Sleep(TimeSpan.FromSeconds(olcumSn));

            var gecenSn = Math.Max(0.1, (DateTime.UtcNow - ilkAn).TotalSeconds);
            double duran;
            int tamponSayisi;
            lock (tamponKilidi)
            {
                if (tamponBasi is { } hala) tamponluSure += (DateTime.UtcNow - hala).TotalSeconds;
                duran = tamponluSure;
                tamponSayisi = tamponlama + (tamponBasi is null ? 0 : 1);
            }
            var gosterilen = (int)Math.Round(Math.Max(0, 1 - duran / gecenSn) * 100);
            // İlk tamponlama akışın açılışıdır, kesinti sayılmaz
            var kayip = Math.Max(0, tamponSayisi - 1) + kopma;
            var mbps = kaynakFps;
            var enBuyukDuraklama = duran;

            var kodek = "";
            try
            {
                var iz = ortam.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
                kodek = DortLuKod(iz.Codec);
            }
            catch { }

            return new SinamaSonucu(k.Ad, true, "tamam", g, y, kodek,
                gosterilen, kayip, mbps, acilis, enBuyukDuraklama);
        }
        catch (Exception e)
        {
            return Basarisiz(k, e.Message);
        }
        finally
        {
            // LibVLC'de Stop() ağ beklemesinde takılabiliyor: zaman sınırı koy,
            // ölçüm bütün kameraları bekletmesin.
            Kapat(oynatici, ortam);
        }
    }

    /// <summary>Oynatıcıyı zaman sınırlı kapatır (takılırsa bırakılır).</summary>
    private static void Kapat(MediaPlayer? oynatici, Media? ortam)
    {
        if (oynatici is null && ortam is null) return;
        var gorev = Task.Run(() =>
        {
            try { oynatici?.Stop(); } catch { }
            try { oynatici?.Dispose(); } catch { }
            try { ortam?.Dispose(); } catch { }
        });
        gorev.Wait(TimeSpan.FromSeconds(3));
    }

    private static SinamaSonucu Basarisiz(Kamera k, string mesaj) =>
        new(k.Ad, false, mesaj, 0, 0, "", 0, 0, 0, 0, 0);

    private static string DortLuKod(uint kod)
    {
        if (kod == 0) return "";
        var b = BitConverter.GetBytes(kod);
        var s = new string(b.Select(x => (char)x).ToArray()).Trim('\0', ' ');
        return s.Length == 4 ? s.ToUpperInvariant() : "";
    }

    /// <summary>Sonuçları hizalı tablo olarak biçimler.</summary>
    public static string Tablo(IEnumerable<SinamaSonucu> sonuclar, int olcumSn)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"{"Kamera",-26} {"Durum",-7} {"Çözünürlük",-11} {"Kodek",-6} " +
                      $"{"Kaynak",7} {"Akıcılık",9} {"Kesinti",11} {"Açılış",7}");
        sb.AppendLine(new string('─', 94));
        foreach (var s in sonuclar)
        {
            if (!s.Bagli)
            {
                sb.AppendLine($"{Kes(s.Ad, 26),-26} {"HATA",-7} {s.Mesaj}");
                continue;
            }
            var coz = s.Genislik > 0 ? $"{s.Genislik}×{s.Yukseklik}" : "—";
            var dur = s.DuraklamaSayisi == 0
                ? "yok"
                : $"{s.DuraklamaSayisi}× {s.EnBuyukDuraklamaSn:F1}s";
            sb.AppendLine($"{Kes(s.Ad, 26),-26} {"canlı",-7} {coz,-11} {s.Kodek,-6} " +
                          $"{s.Fps,5:F0}fps {s.AkicilikYuzde,8}% {dur,11} {s.AcilisSn,6:F1}s");
        }
        var canli = sonuclar.Where(s => s.Bagli).ToList();
        if (canli.Count > 0)
        {
            sb.AppendLine(new string('─', 94));
            var ortalama = canli.Average(s => s.AkicilikYuzde);
            var duraklayan = canli.Count(s => s.DuraklamaSayisi > 0);
            sb.AppendLine($"{canli.Count} kamera canlı · ortalama akıcılık %{ortalama:F0} · " +
                          $"duraklayan kamera {duraklayan} ({olcumSn} sn ölçüm)");
            sb.AppendLine("Akıcılık = kesintisiz geçen sürenin oranı · Kesinti = tamponlama/kopma " +
                          "sayısı ve toplam süresi. %98+ ve kesinti yok = sorunsuz.");
        }
        return sb.ToString();
    }

    private static string Kes(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
