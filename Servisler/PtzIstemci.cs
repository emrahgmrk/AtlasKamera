using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AtlasKamera.Modeller;

namespace AtlasKamera.Servisler;

public enum PtzYon
{
    Yukari, Asagi, Sol, Sag,
    SolYukari, SagYukari, SolAsagi, SagAsagi,
    Yakinlas, Uzaklas,
}

/// <summary>
/// PTZ denetimi — Dahua CGI ve Hikvision ISAPI.
///
/// İki üreticinin protokolü ayrı: Dahua <c>/cgi-bin/ptz.cgi</c> adresine
/// başla/dur komutu gönderir, Hikvision <c>/ISAPI/PTZCtrl/.../continuous</c>
/// ucuna hız vektörü yazar (sıfır vektör = dur). İkisi de Digest kimlik ister.
///
/// Güvenlik ağı: "başla" komutundan sonra uygulama kapanır ya da fare bırakma
/// olayı kaybolursa kamera dönmeye devam etmesin diye <see cref="AzamiHareketSn"/>
/// sonra kendiliğinden "dur" gönderilir.
/// </summary>
public sealed class PtzIstemci : IDisposable
{
    private const int AzamiHareketSn = 6;
    private const int ZamanAsimiSn = 5;

    private readonly P2PTunel _tunel;
    private readonly ConcurrentDictionary<Guid, HttpClient> _istemciler = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _durdurucular = new();

    public PtzIstemci(P2PTunel tunel) => _tunel = tunel;

    private HttpClient Istemci(Kamera k) => _istemciler.GetOrAdd(k.Id, _ =>
    {
        var isleyici = new HttpClientHandler
        {
            Credentials = new CredentialCache
            {
                { new Uri(Adresler.HttpTaban(k, _tunel)), "Digest",
                  new NetworkCredential(k.Kullanici, k.Parola) },
            },
            PreAuthenticate = true,
        };
        return new HttpClient(isleyici) { Timeout = TimeSpan.FromSeconds(ZamanAsimiSn) };
    });

    /// <summary>Kamera bilgileri değişince önbellekteki kimlik geçersizdir.</summary>
    public void Unut(Guid kameraId)
    {
        if (_istemciler.TryRemove(kameraId, out var i)) i.Dispose();
    }

    public async Task BaslaAsync(Kamera k, PtzYon yon, int hiz = 4)
    {
        hiz = Math.Clamp(hiz, 1, 8);
        await GonderAsync(k, yon, hiz, basla: true);

        // Önceki otomatik durdurucuyu iptal et, yenisini kur
        var yeni = new CancellationTokenSource();
        if (_durdurucular.TryRemove(k.Id, out var eski)) { eski.Cancel(); eski.Dispose(); }
        _durdurucular[k.Id] = yeni;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(AzamiHareketSn), yeni.Token);
                await GonderAsync(k, yon, hiz, basla: false);
            }
            catch (OperationCanceledException) { /* zamanında dur geldi */ }
            catch { /* kamera ulaşılamıyorsa sessiz geç */ }
        });
    }

    public async Task DurAsync(Kamera k, PtzYon yon)
    {
        if (_durdurucular.TryRemove(k.Id, out var d)) { d.Cancel(); d.Dispose(); }
        await GonderAsync(k, yon, 1, basla: false);
    }

    private async Task GonderAsync(Kamera k, PtzYon yon, int hiz, bool basla)
    {
        var taban = Adresler.HttpTaban(k, _tunel);
        var kanal = k.Kanal < 1 ? 1 : k.Kanal;

        if (k.Marka == Marka.Hikvision)
        {
            // ISAPI: pan/tilt/zoom hız vektörü (-100..100); dur = sıfır vektör
            var (pan, tilt, zoom) = HikvisionVektor(yon, hiz);
            if (!basla) (pan, tilt, zoom) = (0, 0, 0);
            var govde = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                        $"<PTZData><pan>{pan}</pan><tilt>{tilt}</tilt><zoom>{zoom}</zoom></PTZData>";
            using var icerik = new StringContent(govde, Encoding.UTF8, "application/xml");
            var yanit = await Istemci(k).PutAsync(
                $"{taban}/ISAPI/PTZCtrl/channels/{kanal}/continuous", icerik);
            Dogrula(yanit.StatusCode, (int)yanit.StatusCode);
            return;
        }

        // Dahua CGI
        var kod = DahuaKod(yon);
        var dikey = yon is PtzYon.SolYukari or PtzYon.SagYukari or PtzYon.SolAsagi or PtzYon.SagAsagi
            ? hiz : 0;
        var adres = $"{taban}/cgi-bin/ptz.cgi?action={(basla ? "start" : "stop")}" +
                    $"&channel={kanal}&code={kod}&arg1={dikey}&arg2={hiz}&arg3=0";
        var y = await Istemci(k).GetAsync(adres);
        Dogrula(y.StatusCode, (int)y.StatusCode);
    }

    private void Dogrula(HttpStatusCode durum, int kod)
    {
        if (durum == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Kamera kullanıcı adı/parolası kabul edilmedi.");
        if (durum != HttpStatusCode.OK)
            throw new InvalidOperationException($"Kamera komutu reddetti (HTTP {kod}).");
    }

    private static string DahuaKod(PtzYon y) => y switch
    {
        PtzYon.Yukari => "Up",
        PtzYon.Asagi => "Down",
        PtzYon.Sol => "Left",
        PtzYon.Sag => "Right",
        PtzYon.SolYukari => "LeftUp",
        PtzYon.SagYukari => "RightUp",
        PtzYon.SolAsagi => "LeftDown",
        PtzYon.SagAsagi => "RightDown",
        PtzYon.Yakinlas => "ZoomTele",
        PtzYon.Uzaklas => "ZoomWide",
        _ => "Up",
    };

    private static (int pan, int tilt, int zoom) HikvisionVektor(PtzYon y, int hiz)
    {
        var h = Math.Clamp(hiz * 12, 10, 100);     // 1..8 → 12..96
        return y switch
        {
            PtzYon.Yukari => (0, h, 0),
            PtzYon.Asagi => (0, -h, 0),
            PtzYon.Sol => (-h, 0, 0),
            PtzYon.Sag => (h, 0, 0),
            PtzYon.SolYukari => (-h, h, 0),
            PtzYon.SagYukari => (h, h, 0),
            PtzYon.SolAsagi => (-h, -h, 0),
            PtzYon.SagAsagi => (h, -h, 0),
            PtzYon.Yakinlas => (0, 0, h),
            PtzYon.Uzaklas => (0, 0, -h),
            _ => (0, 0, 0),
        };
    }

    public void Dispose()
    {
        foreach (var d in _durdurucular.Values) { d.Cancel(); d.Dispose(); }
        foreach (var i in _istemciler.Values) i.Dispose();
    }
}
