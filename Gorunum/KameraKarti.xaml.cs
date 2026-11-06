using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AtlasKamera.Modeller;
using AtlasKamera.Servisler;
using LibVLCSharp.Shared;
// System.Windows.Media.MediaPlayer ile çakışıyor: burada kastedilen VLC oynatıcısı
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace AtlasKamera.Gorunum;

/// <summary>
/// Duvardaki tek kamera karesi: oynatıcı, durum göstergesi, PTZ ve araçlar.
///
/// Duvarda alt akış oynatılır; kart büyütülünce ana akışa geçilir. Böylece
/// dokuz kamera aynı anda açıkken ağ ve ekran kartı yükü düşük kalır, ama
/// tek kameraya bakarken tam çözünürlük görünür.
///
/// YAŞAM DÖNGÜSÜ
/// -------------
/// Başlatma isteği birden çok yerden gelebiliyor: duvarın kurulması, yenile
/// düğmesi, büyütme, nabzın yeniden bağlanması. Bunlar çakıştığında geride
/// sahipsiz oynatıcı kalıp bellek ve soket sızdırıyordu. Bu yüzden:
///   - her başlatma bir "nesil" numarası alır, eski nesle ait olaylar yok sayılır;
///   - başlatmalar tek kilitle sıraya girer;
///   - art arda başarısızlıkta yeniden deneme aralığı uzar — erişilemeyen bir
///     kameraya saniyede bir bağlanmaya çalışmak cihazı ve P2P aktarmasını yoruyor.
/// </summary>
public partial class KameraKarti : UserControl, IDisposable
{
    private readonly VlcMotor _motor;
    private readonly P2PTunel _tunel;
    private readonly PtzIstemci _ptz;

    private readonly SemaphoreSlim _baslatmaKilidi = new(1, 1);
    private MediaPlayer? _oynatici;
    private Media? _ortam;
    private CancellationTokenSource? _iptal;
    private int _nesil;                      // her başlatma bunu artırır
    private bool _anaAkis;
    private bool _atildi;
    private int _altAkisHatalari;            // alt akışta art arda kaç hata alındı
    private int _basarisizDeneme;            // art arda başarısız bağlanma sayısı
    private DateTime _sonDeneme = DateTime.MinValue;
    private DateTime _sonKare = DateTime.MinValue;
    private volatile bool _tamponluyor;
    private int _tamponlamaSayisi;
    private DateTime _tamponBasi = DateTime.MinValue;
    private long _oncekiZaman = -1;
    private DateTime _zamanDegisimi = DateTime.MinValue;
    private readonly DispatcherTimerSarmal _nabiz;

    public Kamera Kamera { get; private set; }

    /// <summary>Kart büyütülmek istendiğinde ana pencereye haber verir.</summary>
    public event EventHandler<KameraKarti>? BuyutIstendi;

    /// <summary>Sağ tık menüsünden tam ekran istendi.</summary>
    public event EventHandler? TamEkranIstendi;

    /// <summary>Sağ tık menüsünden kamera ayarları istendi.</summary>
    public event EventHandler<KameraKarti>? AyarlarIstendi;

    /// <summary>Görüntü gelmeye başladı ya da kesildi (duvarın "yalnız canlı" süzgeci için).</summary>
    public event EventHandler? CanliDegisti;

    /// <summary>Menüden akış tercihi değişti — ayarın diske yazılması için.</summary>
    public event EventHandler? AyarDegisti;

    public bool Buyuk { get; private set; }

    /// <summary>Son saniyelerde görüntü geldi mi.</summary>
    public bool Canli { get; private set; }

    /// <summary>Oynatıcı kurulu mu (bağlanıyor ya da oynuyor).</summary>
    public bool Calisiyor => _oynatici is not null;

    /// <summary>Şu an ana akış mı oynatılıyor.</summary>
    public bool AnaAkista => _anaAkis;

    public KameraKarti(Kamera kamera, VlcMotor motor, P2PTunel tunel, PtzIstemci ptz)
    {
        InitializeComponent();
        Kamera = kamera;
        _motor = motor;
        _tunel = tunel;
        _ptz = ptz;

        AdYazisi.Text = kamera.Ad;
        KonumYazisi.Text = string.IsNullOrWhiteSpace(kamera.Konum)
            ? KaynakOzeti(kamera) : kamera.Konum;
        PtzDugme.Visibility = kamera.Ptz ? Visibility.Visible : Visibility.Collapsed;
        DugmeleriTazele();

        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Buyut_Click(this, e); };

        _nabiz = new DispatcherTimerSarmal(TimeSpan.FromSeconds(3), NabizKontrol);

        // Kart boyutu değişince kırpma oranı da değişmeli (düzen/tam ekran)
        SizeChanged += (_, _) => { if (Kamera.EkraniDoldur) KirpmayiUygula(); };

        // Sağ tık menüsü iki yere de gerekiyor: video penceresinin içeriği ayrı
        // bir görsel ağaçta çizildiğinden kartın menüsü oraya miras kalmıyor.
        Video.ContextMenu = MenuOlustur();
        BosPanel.ContextMenu = MenuOlustur();
    }

    // ── Sağ tık menüsü ───────────────────────────────────────────────────
    private MenuItem? _mnuBuyut, _mnuAna, _mnuAlt, _mnuPtz;

    private ContextMenu MenuOlustur()
    {
        var menu = new ContextMenu { Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x26)) };

        MenuItem Ekle(string baslik, RoutedEventHandler tiklama, string? kisayol = null)
        {
            var o = new MenuItem
            {
                Header = baslik,
                InputGestureText = kisayol ?? "",
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF2)),
            };
            o.Click += tiklama;
            menu.Items.Add(o);
            return o;
        }

        var buyut = Ekle("Büyüt", (_, _) => BuyutIstendi?.Invoke(this, this), "Çift tık");
        Ekle("Tam ekran", (_, _) => TamEkranIstendi?.Invoke(this, EventArgs.Empty), "F11");
        menu.Items.Add(new Separator());

        var ana = Ekle("Ana akış (yüksek çözünürlük)", (_, _) => AkisSec(anaAkis: true));
        var alt = Ekle("Alt akış (düşük yük)", (_, _) => AkisSec(anaAkis: false));
        ana.IsCheckable = alt.IsCheckable = true;
        menu.Items.Add(new Separator());

        var doldur = Ekle("Ekranı doldur (kenarları kırp)", (_, _) => DoldurmayiDegistir());
        doldur.IsCheckable = true;
        menu.Items.Add(new Separator());

        Ekle("Akışı yenile", (_, _) => Yenile_Click(this, new RoutedEventArgs()));
        Ekle("Anlık görüntü kaydet", (_, _) => AnlikGoruntuKaydet());
        var ptz = Ekle("PTZ denetimi", (_, _) => PtzDugme_Click(this, new RoutedEventArgs()));
        ptz.IsCheckable = true;
        menu.Items.Add(new Separator());
        Ekle("Kamera ayarları…", (_, _) => AyarlarIstendi?.Invoke(this, this));

        // Menü her açılışta güncel durumu göstersin
        menu.Opened += (_, _) =>
        {
            buyut.Header = Buyuk ? "Küçült" : "Büyüt";
            ana.IsChecked = _anaAkis;
            alt.IsChecked = !_anaAkis;
            ptz.Visibility = Kamera.Ptz ? Visibility.Visible : Visibility.Collapsed;
            ptz.IsChecked = PtzPaneli.Visibility == Visibility.Visible;
            doldur.IsChecked = Kamera.EkraniDoldur;
        };

        // İlk kurulan menünün öğeleri durum güncellemesinde de kullanılıyor
        _mnuBuyut ??= buyut;
        _mnuAna ??= ana;
        _mnuAlt ??= alt;
        _mnuPtz ??= ptz;
        return menu;
    }

    /// <summary>Menüden akış seçimi: seçim kartta kalıcıdır (sayfa değişse de).</summary>
    private void AkisSec(bool anaAkis)
    {
        if (_anaAkis == anaAkis) return;
        // Kullanıcı elle seçti: duvara dönüşte de bu seçim korunsun
        Kamera.DuvardaAltAkis = !anaAkis;
        _altAkisHatalari = 0;
        _basarisizDeneme = 0;
        Gunluk.Yaz($"{Kamera.Ad}: {(anaAkis ? "ana" : "alt")} akışa geçiliyor (menüden)");
        AyarDegisti?.Invoke(this, EventArgs.Empty);
        DugmeleriTazele();
        Baslat(anaAkis);
    }

    /// <summary>
    /// Görüntü kartı doldursun mu — kenarlardaki siyah bantları kaldırır.
    ///
    /// LibVLC iki yol sunuyor: AspectRatio görüntüyü esnetir (insanlar şişman
    /// görünür), CropGeometry kenarlardan kırpar ve oranı bozmaz. İkincisi
    /// tercih edildi; kırpma oranı kartın kendi en/boy oranıdır.
    /// </summary>
    private void DoldurmayiDegistir()
    {
        Kamera.EkraniDoldur = !Kamera.EkraniDoldur;
        Gunluk.Yaz($"{Kamera.Ad}: ekranı doldur = {Kamera.EkraniDoldur}");
        AyarDegisti?.Invoke(this, EventArgs.Empty);
        DugmeleriTazele();
        KirpmayiUygula();
    }

    /// <summary>Kırpma oranını kartın güncel en/boy oranına göre ayarlar.</summary>
    private void KirpmayiUygula()
    {
        var o = _oynatici;
        if (o is null) return;
        try
        {
            // Büyük görüntüde kırpma yok: kameranın kendi yazısı (tarih, kanal
            // adı) kenarlarda duruyor ve kırpma onu kesiyor.
            if (Kamera.EkraniDoldur && !Buyuk && ActualWidth > 1 && ActualHeight > 1)
            {
                // "G:Y" — VLC görüntüyü bu orana kırpar, gerisini kenarlardan atar
                o.CropGeometry = $"{(int)Math.Round(ActualWidth)}:{(int)Math.Round(ActualHeight)}";
            }
            else
            {
                o.CropGeometry = null;      // kaynağın kendi oranı, siyah bantlı
            }
        }
        catch (Exception e)
        {
            Gunluk.Hata($"kırpma {Kamera.Ad}", e);
        }
    }

    /// <summary>Araç düğmelerinin yazısını güncel duruma göre yazar.</summary>
    private void DugmeleriTazele()
    {
        AkisDugme.Content = _anaAkis ? "HD" : "SD";
        AkisDugme.ToolTip = _anaAkis
            ? "Ana akış — alt akışa geçmek için tıklayın"
            : "Alt akış — ana akışa (yüksek çözünürlük) geçmek için tıklayın";
        DoldurDugme.Opacity = Kamera.EkraniDoldur ? 1.0 : 0.55;
        BuyutDugme.Content = Buyuk ? "🗗" : "🗖";
    }

    private void AkisDugme_Click(object sender, RoutedEventArgs e) => AkisSec(!_anaAkis);

    private void DoldurDugme_Click(object sender, RoutedEventArgs e) => DoldurmayiDegistir();

    /// <summary>Görüntüyü Resimler klasörüne PNG olarak kaydeder.</summary>
    private void AnlikGoruntuKaydet()
    {
        var o = _oynatici;
        if (o is null || !Canli)
        {
            KisaUyari("Görüntü yokken anlık görüntü alınamaz");
            return;
        }
        try
        {
            var klasor = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AtlasKamera");
            System.IO.Directory.CreateDirectory(klasor);
            var adTemiz = string.Join("_", Kamera.Ad.Split(System.IO.Path.GetInvalidFileNameChars()));
            var yol = System.IO.Path.Combine(klasor, $"{adTemiz}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            if (o.TakeSnapshot(0, yol, 0, 0))
            {
                KisaBilgi("Kaydedildi: " + System.IO.Path.GetFileName(yol));
                Gunluk.Yaz($"{Kamera.Ad}: anlık görüntü {yol}");
            }
            else
            {
                KisaUyari("Anlık görüntü alınamadı");
            }
        }
        catch (Exception e)
        {
            Gunluk.Hata($"anlık görüntü {Kamera.Ad}", e);
            KisaUyari("Anlık görüntü alınamadı: " + e.Message);
        }
    }

    /// <summary>Konum yazılmamışsa başlıkta kameranın kaynağı görünsün.</summary>
    private static string KaynakOzeti(Kamera k) =>
        k.Baglanti == BaglantiTuru.DahuaP2P
            ? $"P2P {k.SeriNo} · Kanal {k.Kanal}"
            : $"{k.Host} · Kanal {k.Kanal}";

    /// <summary>Akışı başlatır (ateşle-unut). Büyük kartlarda ana akış kullanılır.</summary>
    public void Baslat(bool anaAkis)
    {
        if (_atildi) return;
        _ = BaslatAsync(anaAkis);
    }

    private async Task BaslatAsync(bool anaAkis)
    {
        if (_atildi) return;
        if (!await _baslatmaKilidi.WaitAsync(TimeSpan.FromSeconds(20))) return;
        var benimNesil = Interlocked.Increment(ref _nesil);
        try
        {
            if (_atildi) return;
            _anaAkis = anaAkis;
            _sonDeneme = DateTime.UtcNow;
            DurdurIc();

            var iptalKaynak = new CancellationTokenSource();
            _iptal = iptalKaynak;
            var iptal = iptalKaynak.Token;

            DurumGoster("Bağlanıyor…", "", hata: false);
            AkisRozeti.Text = anaAkis ? "ana akış" : "alt akış";

            // P2P ise önce tünel kurulmalı
            if (Kamera.Baglanti == BaglantiTuru.DahuaP2P)
            {
                DurumGoster("Uzak bağlantı kuruluyor…", Kamera.SeriNo, hata: false);
                // Tünel ayakta olduğu hâlde üç kez görüntü alınamadıysa aktarma
                // oturumu cihaz tarafında ölmüş olabilir: tüneli yenile.
                if (_basarisizDeneme > 0 && _basarisizDeneme % 2 == 0)
                    _tunel.Tazele(Kamera.SeriNo);
                bool hazir;
                try
                {
                    hazir = await _tunel.HazirlaAsync(Kamera.SeriNo, 40, iptal);
                }
                catch (Exception e)
                {
                    Gunluk.Hata($"tünel {Kamera.Ad}", e);
                    hazir = false;
                }
                if (iptal.IsCancellationRequested || benimNesil != Volatile.Read(ref _nesil)) return;
                if (!hazir)
                {
                    BasarisizOldu("Uzak cihaza ulaşılamadı", _tunel.SonHata(Kamera.SeriNo));
                    return;
                }
            }

            var adres = Adresler.Rtsp(Kamera, anaAkis, _tunel);
            var tampon = Kamera.Baglanti == BaglantiTuru.DahuaP2P
                ? VlcMotor.UzakTampon : VlcMotor.YerelTampon;

            var (oynatici, ortam) = _motor.Olustur(adres, tampon);
            _oynatici = oynatici;
            _ortam = ortam;
            _tamponlamaSayisi = 0;
            _tamponluyor = false;

            // Olaylar yalnız bu nesle aitse dikkate alınır
            bool BenimNesil() => benimNesil == Volatile.Read(ref _nesil) && !_atildi;

            // Playing: akış açıldı, ama ilk kare henüz çizilmemiş olabilir —
            // paneli burada kaldırmak boş gri bir kare bırakıyor. Panel ancak
            // Vout (video çıkışı oluştu) ile kalkar.
            //
            // Video penceresinin KENDİSİ hiçbir zaman gizlenmez: gizliyken
            // oynatıcı atanırsa VLC görüntüyü kendi ayrı penceresinde açıyor
            // ve duvar bomboş kalıyor. Yalnızca üstündeki panel gizlenir.
            oynatici.Playing += (_, _) =>
            {
                if (!BenimNesil()) return;
                _sonKare = DateTime.UtcNow;
                _basarisizDeneme = 0;
            };
            oynatici.TimeChanged += (_, _) => { if (BenimNesil()) _sonKare = DateTime.UtcNow; };
            oynatici.Vout += (_, _) =>
            {
                if (!BenimNesil()) return;
                _sonKare = DateTime.UtcNow;
                if (!_anaAkis) _altAkisHatalari = 0;    // alt akış tuttu
                Dispatcher.BeginInvoke(() => { DurumGizle(); CanliligiBildir(true); });
            };
            oynatici.Buffering += (_, e) =>
            {
                if (!BenimNesil()) return;
                _sonKare = DateTime.UtcNow;
                if (e.Cache >= 100f)
                {
                    _tamponluyor = false;
                    _tamponBasi = DateTime.MinValue;
                }
                else if (!_tamponluyor)
                {
                    _tamponluyor = true;
                    _tamponBasi = DateTime.UtcNow;
                    Interlocked.Increment(ref _tamponlamaSayisi);
                }
            };
            oynatici.EncounteredError += (_, _) =>
            {
                if (!BenimNesil()) return;
                Dispatcher.BeginInvoke(() =>
                {
                    // Duvarda ana akışa OTOMATİK geçilmez. Alt akış açılmayınca
                    // ana akışa düşmek (2560×1440) NVR'ın çıkış bant genişliğini
                    // yiyor ve diğer kameraların alt akışlarını da düşürüyordu —
                    // biri düşünce ötekiler de düşüyor, kısır döngü. Kart alt
                    // akışta ısrar eder; ana akış isteniyorsa HD düğmesiyle
                    // elle seçilir (büyütme penceresi zaten ana akış kullanır).
                    if (!_anaAkis)
                    {
                        _altAkisHatalari++;
                        BasarisizOldu("Alt akış açılmadı",
                            _altAkisHatalari > 2
                                ? "NVR alt akışı vermiyor — HD düğmesiyle ana akışı deneyebilirsiniz"
                                : "yeniden deneniyor…");
                        return;
                    }
                    BasarisizOldu("Akış hatası", Adresler.Maskeli(Kamera, _anaAkis, _tunel));
                });
            };
            oynatici.EndReached += (_, _) =>
            {
                if (!BenimNesil()) return;
                Gunluk.Yaz($"{Kamera.Ad}: bağlantı kesildi (akış sonu)");
                Dispatcher.BeginInvoke(() =>
                    DurumGoster("Bağlantı kesildi", "yeniden bağlanılıyor…", hata: false));
            };

            Video.MediaPlayer = oynatici;
            oynatici.Play(ortam);
            KirpmayiUygula();
            DugmeleriTazele();
            _nabiz.Baslat();
        }
        catch (Exception e)
        {
            Gunluk.Hata($"başlatma {Kamera.Ad}", e);
            BasarisizOldu("Başlatılamadı", e.Message);
        }
        finally
        {
            // Nabız HER durumda çalışmalı: başlatma yarıda kaldıysa (tünel
            // kurulamadı, kimlik hatası) kartın kendi kendine yeniden denemesi
            // buna bağlı. Eskiden yalnız başarılı başlatmada kuruluyordu ve
            // bağlanamayan kamera "Yeniden dene" düğmesine basılana kadar
            // öylece kalıyordu.
            if (!_atildi) _nabiz.Baslat();
            _baslatmaKilidi.Release();
        }
    }

    private void BasarisizOldu(string baslik, string ayrinti)
    {
        _basarisizDeneme++;
        DurumGoster(baslik, ayrinti, hata: true);
    }

    /// <summary>
    /// Üç saniyede bir: görüntü akıyor mu, akmıyorsa yeniden bağlan.
    ///
    /// Yeniden deneme aralığı art arda başarısızlıkta uzar (10 → 20 → 40 → 60 sn):
    /// erişilemeyen bir kameraya sürekli bağlanmaya çalışmak hem cihazı hem —
    /// P2P'de — bulut aktarmasını yoruyor.
    /// </summary>
    private void NabizKontrol()
    {
        if (_atildi) return;
        RozetiGuncelle();

        // P2P kameralarda daha sabırlı ol: her deneme buluta yeni bir oturum
        // isteği demek ve cihaz tek oturuma izin veriyor.
        var tavan = Kamera.Baglanti == BaglantiTuru.DahuaP2P ? 180 : 60;
        var bekleme = TimeSpan.FromSeconds(
            Math.Min(tavan, 10 * Math.Pow(2, Math.Min(5, _basarisizDeneme))));
        var sonDenemedenBeri = DateTime.UtcNow - _sonDeneme;

        if (_oynatici is null)
        {
            CanliligiBildir(false);
            // Hiç başlayamadı: geri çekilme süresi dolunca yeniden dene
            if (_basarisizDeneme > 0 && sonDenemedenBeri > bekleme)
                Baslat(_anaAkis);
            return;
        }

        if (_sonKare == DateTime.MinValue)
        {
            CanliligiBildir(false);

            // Alt akış bağlandı ama kare göndermiyor. Bazı NVR kanallarında
            // alt akış tanımlı olduğu hâlde yayın kapalı: RTSP el sıkışması
            // başarılı, hata yok, görüntü de yok. Hata beklemeden ana akışa
            // geçilir — kullanıcı kartı büyütünce görüntünün gelmesinin sebebi
            // buydu (büyütme ana akışa geçiriyor).
            // Bağlandı ama ilk kare gelmedi: 20 sn sonra yeniden kur
            if (sonDenemedenBeri > TimeSpan.FromSeconds(20))
            {
                Gunluk.Yaz($"{Kamera.Ad}: görüntü hiç gelmedi, yeniden kuruluyor");
                _basarisizDeneme++;
                Baslat(_anaAkis);
            }
            return;
        }

        // Gri ekran denetimi: akış ölmüşse kareler "geliyormuş" gibi görünse
        // bile yeniden kur.
        var olumNedeni = AkisOlduMu();
        if (olumNedeni is not null && sonDenemedenBeri > TimeSpan.FromSeconds(10))
        {
            Gunluk.Yaz($"{Kamera.Ad}: akış durdu ({olumNedeni}), yeniden kuruluyor");
            CanliligiBildir(false);
            DurumGoster("Görüntü durdu", olumNedeni, hata: false);
            _sonKare = DateTime.MinValue;
            _oncekiZaman = -1;
            _zamanDegisimi = DateTime.MinValue;
            _basarisizDeneme++;
            Baslat(_anaAkis);
            return;
        }

        var gecen = (DateTime.UtcNow - _sonKare).TotalSeconds;
        CanliligiBildir(gecen < 8);
        if (gecen > 12 && sonDenemedenBeri > bekleme)
        {
            Gunluk.Yaz($"{Kamera.Ad}: {gecen:F0} sn görüntü yok, yeniden bağlanılıyor");
            DurumGoster("Görüntü durdu", "yeniden bağlanılıyor…", hata: false);
            _sonKare = DateTime.MinValue;
            _basarisizDeneme++;
            Baslat(_anaAkis);
        }
        else if (gecen < 6)
        {
            DurumNoktasi.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
        }
    }

    /// <summary>
    /// Akış gerçekten yaşıyor mu — "gri ekran" denetimi.
    ///
    /// Görüntü penceresi açıldıktan sonra akış sessizce ölebiliyor: NVR alt
    /// akışı kesiyor, kart gri kalıyor ve kimse fark etmiyordu. TimeChanged
    /// olayları bir süre daha gelmeye devam ettiği için "görüntü var"
    /// sanılıyordu. Burada oynatıcının kendi durumu denetleniyor.
    /// </summary>
    private string? AkisOlduMu()
    {
        var o = _oynatici;
        if (o is null) return null;
        try
        {
            var durum = o.State;
            if (durum is VLCState.Error or VLCState.Ended or VLCState.Stopped)
                return $"oynatıcı durumu: {durum}";

            // Görüntü gösteriliyor sanılıyor ama altında bir şey yok:
            // oynatıcı durmuş ya da video çıkışı kalmamış (ekran gri kalır).
            if (BosPanel.Visibility == Visibility.Collapsed)
            {
                if (!o.IsPlaying) return "oynatma durdu";
                if (o.VoutCount == 0) return "video çıkışı kayboldu";
            }

            // Tamponlama uzadıkça uzuyor: akış geliyor ama kare çıkmıyor
            if (_tamponluyor && _tamponBasi != DateTime.MinValue
                && (DateTime.UtcNow - _tamponBasi).TotalSeconds > 12)
                return "12 sn+ tamponlama";

            // Akışın saati duruyorsa görüntü de durmuştur. Canlı yayında saat
            // ilerlemeyen kameralar da var; o durumda bu denetim atlanır.
            var zaman = o.Time;
            if (zaman > 0)
            {
                if (zaman != _oncekiZaman)
                {
                    _oncekiZaman = zaman;
                    _zamanDegisimi = DateTime.UtcNow;
                }
                else if (_zamanDegisimi != DateTime.MinValue
                         && (DateTime.UtcNow - _zamanDegisimi).TotalSeconds > 15)
                {
                    return "akış saati 15 sn'dir ilerlemiyor";
                }
            }
        }
        catch (Exception e)
        {
            Gunluk.Hata($"sağlık denetimi {Kamera.Ad}", e);
        }
        return null;
    }

    /// <summary>Canlılık değiştiyse duvara haber ver ("yalnız canlı" süzgeci için).</summary>
    private void CanliligiBildir(bool canli)
    {
        if (Canli == canli) return;
        Canli = canli;
        CanliDegisti?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Rozete akışın gerçek durumunu yazar: çözünürlük, kare hızı, kesinti sayısı.
    ///
    /// Kare sayaçları (LibVLCSharp'ta Media.Statistics) bu sürümde dolmuyor;
    /// donmanın güvenilir işareti tamponlamadır — akış kesilince VLC tamponu
    /// boşaltıp yeniden doldurur. İlk tamponlama açılışın kendisidir, sayılmaz.
    /// </summary>
    private void RozetiGuncelle()
    {
        var o = _oynatici;
        if (o is null) return;
        try
        {
            uint g = 0, y = 0;
            try { o.Size(0, ref g, ref y); } catch { }
            var akis = _anaAkis ? "ana" : "alt";
            var coz = g > 0 ? $" · {g}×{y}" : "";
            var fps = o.Fps > 1 ? $" · {o.Fps:F0} fps" : "";
            var kesintiSayisi = Math.Max(0, _tamponlamaSayisi - 1);
            var kesinti = kesintiSayisi > 0 ? $" · {kesintiSayisi} kesinti" : "";

            AkisRozeti.Text = _tamponluyor
                ? $"{akis}{coz} · tamponlanıyor…"
                : $"{akis}{coz}{fps}{kesinti}";
            AkisRozeti.Foreground = new SolidColorBrush(_tamponluyor || kesintiSayisi > 0
                ? Color.FromRgb(0xF5, 0x9E, 0x0B) : Color.FromRgb(0x64, 0x74, 0x8B));
        }
        catch { /* rozet güncellenemezse akış sürsün */ }
    }

    /// <summary>Akışı durdurur (kart duvardan kalkarken ya da büyütme değişirken).</summary>
    public void Durdur()
    {
        Interlocked.Increment(ref _nesil);     // bekleyen olaylar artık geçersiz
        DurdurIc();
    }

    private void DurdurIc()
    {
        _nabiz.Durdur();
        try { _iptal?.Cancel(); } catch { }
        try { _iptal?.Dispose(); } catch { }
        _iptal = null;

        var o = _oynatici;
        var m = _ortam;
        _oynatici = null;
        _ortam = null;
        try { Video.MediaPlayer = null; } catch { }

        if (o is not null || m is not null)
        {
            // Stop() ağ beklemesinde takılabiliyor: arayüzü kilitlemesin diye
            // kapatma arka planda yapılır.
            Task.Run(() =>
            {
                try { o?.Stop(); } catch { }
                try { o?.Dispose(); } catch { }
                try { m?.Dispose(); } catch { }
            });
        }
        _sonKare = DateTime.MinValue;
        _tamponlamaSayisi = 0;
        _tamponluyor = false;
        _tamponBasi = DateTime.MinValue;
        _oncekiZaman = -1;
        _zamanDegisimi = DateTime.MinValue;
        CanliligiBildir(false);
        try
        {
            BosPanel.Visibility = Visibility.Visible;
            DurumNoktasi.Fill = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        }
        catch { }
    }

    /// <summary>Kart büyütüldü/küçültüldü: akış kalitesi buna göre değişir.</summary>
    public void BuyuklukDegisti(bool buyuk)
    {
        if (Buyuk == buyuk) return;
        Buyuk = buyuk;
        DugmeleriTazele();
        KirpmayiUygula();
        var istenen = buyuk || !Kamera.DuvardaAltAkis;
        if (istenen != _anaAkis) Baslat(istenen);
    }

    /// <summary>
    /// Görüntü yokken boş paneli gösterir, video penceresini gizler.
    ///
    /// Video penceresi (HWND) oynatıcı atanmadığında beyaz kalıyor; koyu
    /// arayüzde kocaman beyaz dikdörtgenler çıkıyordu. Bu yüzden görüntü
    /// gelene kadar VideoView tamamen gizlenir.
    /// </summary>
    /// <summary>
    /// Görüntü yokken boş paneli gösterir.
    ///
    /// Panel video penceresinin İÇİNDE (VideoView.Content) olduğundan video
    /// penceresi hiç gizlenmez — gizlenirse VLC görüntüyü kendi ayrı
    /// penceresinde açıyor ve kart bomboş kalıyor.
    /// </summary>
    private void DurumGoster(string baslik, string ayrinti, bool hata)
    {
        BosPanel.Visibility = Visibility.Visible;
        DurumBaslik.Text = baslik;
        DurumAyrinti.Text = ayrinti;
        DurumBaslik.Foreground = new SolidColorBrush(hata
            ? Color.FromRgb(0xF8, 0x71, 0x71) : Color.FromRgb(0x9A, 0xA4, 0xB2));
        DurumNoktasi.Fill = new SolidColorBrush(hata
            ? Color.FromRgb(0xEF, 0x44, 0x44) : Color.FromRgb(0xF5, 0x9E, 0x0B));
        YenidenDene.Visibility = hata ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Görüntü geldi: boş paneli kaldır (video zaten arkada).</summary>
    private void DurumGizle()
    {
        BosPanel.Visibility = Visibility.Collapsed;
        DurumNoktasi.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    }

    // ── Olaylar ──────────────────────────────────────────────────────────
    private void Yenile_Click(object sender, RoutedEventArgs e)
    {
        _basarisizDeneme = 0;
        _altAkisHatalari = 0;
        Baslat(_anaAkis);
    }

    private void YenidenDene_Click(object sender, RoutedEventArgs e) => Yenile_Click(sender, e);

    private void Buyut_Click(object sender, RoutedEventArgs e) => BuyutIstendi?.Invoke(this, this);

    private void PtzDugme_Click(object sender, RoutedEventArgs e)
    {
        PtzPaneli.Visibility = PtzPaneli.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Ptz_Bas(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement f || f.Tag is not string ad) return;
        if (!Enum.TryParse<PtzYon>(ad, out var yon)) return;
        try { await _ptz.BaslaAsync(Kamera, yon); }
        catch (Exception ex) { KisaUyari(ex.Message); Gunluk.Hata($"PTZ {Kamera.Ad}", ex); }
    }

    private async void Ptz_Birak(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement f || f.Tag is not string ad) return;
        if (!Enum.TryParse<PtzYon>(ad, out var yon)) return;
        try { await _ptz.DurAsync(Kamera, yon); }
        catch { /* dur komutu yutulur: istemcide otomatik durdurucu var */ }
    }

    private void KisaBilgi(string mesaj)
    {
        AkisRozeti.Text = mesaj.Length > 60 ? mesaj[..60] : mesaj;
        AkisRozeti.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    }

    private void KisaUyari(string mesaj)
    {
        AkisRozeti.Text = mesaj.Length > 60 ? mesaj[..60] : mesaj;
        AkisRozeti.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    }

    public void Dispose()
    {
        if (_atildi) return;
        _atildi = true;
        Durdur();
        _baslatmaKilidi.Dispose();
    }
}

/// <summary>Arayüz iş parçacığında çalışan basit yineleyici.</summary>
internal sealed class DispatcherTimerSarmal
{
    private readonly System.Windows.Threading.DispatcherTimer _zamanlayici;

    public DispatcherTimerSarmal(TimeSpan aralik, Action is_)
    {
        _zamanlayici = new System.Windows.Threading.DispatcherTimer { Interval = aralik };
        _zamanlayici.Tick += (_, _) =>
        {
            try { is_(); }
            catch (Exception e) { Gunluk.Hata("nabız", e); }
        };
    }

    public void Baslat() => _zamanlayici.Start();
    public void Durdur() => _zamanlayici.Stop();
}
