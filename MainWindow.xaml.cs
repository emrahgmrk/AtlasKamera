using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AtlasKamera.Gorunum;
using AtlasKamera.Modeller;
using AtlasKamera.Pencereler;
using AtlasKamera.Servisler;

namespace AtlasKamera;

/// <summary>
/// Kamera duvarı.
///
/// SAYFALAMA
/// ---------
/// Düzen düğmeleri sayfa başına kamera sayısını seçer (1 / 4 / 9 / 16 / tümü).
/// Önceden yalnız sütun sayısı değişiyordu: on kamera "2×2" düzeninde iki
/// sütuna beş satır hâlinde diziliyor, her kart 1244×243 piksele eziliyor ve
/// görüntü o basık alanın içinde küçücük kalıyordu.
///
/// Sayfada olmayan kameraların akışı durdurulur — ağ ve ekran kartı yalnız
/// bakılan kameralar için çalışır.
/// </summary>
public partial class MainWindow : Window
{
    private readonly KameraDepo _depo = new();
    private readonly P2PTunel _tunel = new();
    private readonly VlcMotor _motor;
    private readonly PtzIstemci _ptz;

    private readonly List<KameraKarti> _kartlar = new();

    /// <summary>Açıksa tek kamerayı büyük gösteren pencere.</summary>
    private BuyutmePenceresi? _buyukPencere;

    /// <summary>Sayfa başına kamera (0 = hepsi tek sayfada).</summary>
    private int _sayfaBoyu = 4;
    private int _sayfa;

    /// <summary>Görüntü vermeyen kameralar duvarda gizlensin mi.</summary>
    private bool _yalnizCanli;

    /// <summary>
    /// Canlılık değişimlerini topluca işlemek için küçük gecikme.
    ///
    /// Açılışta kameralar saniyeler içinde tek tek canlanıyor; her birinde
    /// duvarı yeniden kurmak göz yoruyordu.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _suzgecZamanlayici;

    private WindowState _oncekiDurum = WindowState.Normal;
    private WindowStyle _oncekiStil = WindowStyle.SingleBorderWindow;

    /// <summary>Yayımlanmış yeni sürüm (varsa).</summary>
    private Surum? _yeniSurum;

    public MainWindow()
    {
        InitializeComponent();
        _motor = new VlcMotor();
        _ptz = new PtzIstemci(_tunel);

        Loaded += (_, _) => Baslat();
        Closed += (_, _) => Kapat();
        PreviewKeyDown += PencereTus;
    }

    private void Baslat()
    {
        // Önceki çalıştırmadan kalmış tünelleri kapat: cihaz tek P2P oturumuna
        // izin verdiğinden, kalan tünel kameranın hiç açılmamasına yol açar.
        P2PTunel.SahipsizleriTemizle();
        try
        {
            _depo.Yukle();
        }
        catch (Exception e)
        {
            Gunluk.Hata("liste yükleme", e);
            MessageBox.Show(this, e.Message, "ATLAS Kamera",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        KartlariKur();
        _ = GuncellemeDenetle();
        GuncellemeZamanlayici();
    }

    // ── Uzaktan güncelleme ───────────────────────────────────────────────

    /// <summary>Günde bir yeniden bakar: uygulama günlerce açık kalıyor.</summary>
    private void GuncellemeZamanlayici()
    {
        var z = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromHours(24),
        };
        z.Tick += (_, _) => _ = GuncellemeDenetle();
        z.Start();
    }

    private async System.Threading.Tasks.Task GuncellemeDenetle()
    {
        var surum = await Guncelleyici.YeniSurumVarMi();
        if (surum is null) return;
        _yeniSurum = surum;
        GuncellemeDugme.Content = $"Güncelleme: {surum.Etiket}";
        GuncellemeDugme.Visibility = Visibility.Visible;
        Gunluk.Yaz($"yeni sürüm yayımlanmış: {surum.Etiket} " +
                   $"(çalışan: {Guncelleyici.SuAnkiSurum})");
    }

    private async void Guncelleme_Click(object sender, RoutedEventArgs e)
    {
        if (_yeniSurum is null) return;
        var s = _yeniSurum;

        var notlar = string.IsNullOrWhiteSpace(s.Notlar) ? "" : "\n\n" + s.Notlar.Trim();
        var onay = MessageBox.Show(this,
            $"Yeni sürüm: {s.Baslik} ({s.Etiket})\n" +
            $"Çalışan sürüm: {Guncelleyici.SuAnkiSurum}\n" +
            $"İndirilecek: {s.Boyut / 1024 / 1024} MB{notlar}\n\n" +
            "Güncelleme indirilip kurulacak; uygulama kapanıp yeniden açılacak. " +
            "Kamera listeniz ve ayarlarınız korunur.\n\nDevam edilsin mi?",
            "Güncelleme", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (onay != MessageBoxResult.Yes) return;

        GuncellemeDugme.IsEnabled = false;
        var ilerleme = new Progress<int>(y => GuncellemeDugme.Content = $"İndiriliyor %{y}");
        var hata = await Guncelleyici.IndirVeKur(s, ilerleme);
        if (hata is not null)
        {
            GuncellemeDugme.IsEnabled = true;
            GuncellemeDugme.Content = $"Güncelleme: {s.Etiket}";
            MessageBox.Show(this, "Güncelleme kurulamadı: " + hata, "Güncelleme",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Kapanış: akışlar durdurulur, tüneller kapatılır.
    ///
    /// VLC'nin kapanışı ağ beklemesinde takılabildiği için her adım zaman
    /// sınırlıdır — uygulama kapanmayı reddedip arkada asılı kalmamalı.
    /// </summary>
    private void Kapat()
    {
        Gunluk.Yaz("kapanıyor");
        try { _buyukPencere?.Close(); } catch { }
        try
        {
            var gorev = System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var k in _kartlar)
                {
                    try { k.Dispose(); } catch (Exception e) { Gunluk.Hata("kart kapatma", e); }
                }
                try { _ptz.Dispose(); } catch { }
                try { _tunel.Dispose(); } catch { }
            });
            gorev.Wait(TimeSpan.FromSeconds(6));
        }
        catch (Exception e)
        {
            Gunluk.Hata("kapanış", e);
        }
        _kartlar.Clear();
        try { _motor.Dispose(); } catch { }
        Gunluk.Yaz("kapandı");
    }

    // ── Kartlar ve sayfalar ──────────────────────────────────────────────

    /// <summary>Kamera listesi değişince kartları yeniden oluşturur.</summary>
    private void KartlariKur()
    {
        foreach (var k in _kartlar) k.Dispose();
        _kartlar.Clear();
        Duvar.Children.Clear();      // kartlarla birlikte temizlenir
        _sayfa = 0;

        var aktifler = _depo.Kameralar.Where(k => k.Aktif)
                                      .OrderBy(k => k.Sira).ThenBy(k => k.Ad).ToList();
        BosMesaj.Visibility = aktifler.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var kam in aktifler)
        {
            var kart = new KameraKarti(kam, _motor, _tunel, _ptz) { Margin = new Thickness(3) };
            kart.BuyutIstendi += (_, k) => BuyutmeyiDegistir(k);
            kart.TamEkranIstendi += (_, _) => TamEkranDegistir();
            kart.AyarlarIstendi += (_, k) => Kameralar_Click(k, new RoutedEventArgs());
            kart.CanliDegisti += (_, _) => SuzgeciTazele();
            kart.AyarDegisti += (_, _) => AyarlariKaydet();
            _kartlar.Add(kart);
        }

        // Kullanılmayan P2P tünellerini kapat
        _tunel.Temizle(aktifler.Where(k => k.Baglanti == BaglantiTuru.DahuaP2P).Select(k => k.SeriNo));

        SayfayiGoster();
        Gunluk.Yaz($"duvar kuruldu: {aktifler.Count} kamera");
    }

    /// <summary>
    /// Duvarda yeri olan kartlar. "Yalnız canlı" açıkken görüntü vermeyenler
    /// listeden çıkar — ama durdurulmazlar, arka planda bağlanmayı denemeye
    /// devam edip canlanınca kendiliğinden geri gelirler.
    /// </summary>
    private List<KameraKarti> GorunurKartlar =>
        _yalnizCanli ? _kartlar.Where(k => k.Canli).ToList() : _kartlar;

    private int SayfaBasinaKamera =>
        _sayfaBoyu <= 0 ? Math.Max(1, GorunurKartlar.Count) : _sayfaBoyu;

    private int SayfaSayisi
    {
        get
        {
            var n = GorunurKartlar.Count;
            return n == 0 ? 1 : (int)Math.Ceiling(n / (double)SayfaBasinaKamera);
        }
    }

    /// <summary>Canlılık değişti: duvarı hemen değil, yarım saniye sonra tazele.</summary>
    private void SuzgeciTazele()
    {
        if (!_yalnizCanli) return;
        _suzgecZamanlayici ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        _suzgecZamanlayici.Tick -= SuzgecZamani;
        _suzgecZamanlayici.Tick += SuzgecZamani;
        _suzgecZamanlayici.Stop();
        _suzgecZamanlayici.Start();
    }

    private void SuzgecZamani(object? gonderen, EventArgs e)
    {
        _suzgecZamanlayici?.Stop();
        SayfayiGoster();
    }

    private void YalnizCanli_Degisti(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _yalnizCanli = YalnizCanli.IsChecked == true;
        _sayfa = 0;
        SayfayiGoster();
    }

    /// <summary>
    /// Geçerli sayfadaki kartları duvara yerleştirir, diğerlerinin akışını durdurur.
    /// </summary>
    private void SayfayiGoster()
    {
        var gorunurler = GorunurKartlar;
        _sayfa = Math.Clamp(_sayfa, 0, Math.Max(0, SayfaSayisi - 1));
        var basla = _sayfa * SayfaBasinaKamera;
        var sayfadakiler = gorunurler.Skip(basla).Take(SayfaBasinaKamera).ToList();

        // Kartlar duvara eklenip çıkarılır. Görünürlükle gizlemek denendi ve
        // olmadı: VideoView'in pencere tutamacı gizli kartta hiç oluşmuyor,
        // duvar tamamen boş kalıyordu.
        //
        // İKİ KURAL (ikisi de pahalıya öğrenildi):
        //  1) Ağaçtan çıkacak kartın akışı ÖNCE durdurulur. Video çıkışı
        //     bağlıyken kartı taşımak, görüntünün kartın dışına kaçmasına —
        //     bazı durumlarda sürecin çökmesine — yol açıyor.
        //  2) Ağaç zaten istenen hâldeyse hiç dokunulmaz. Her tazelemede
        //     bütün kartları çıkarıp eklemek, çalışan akışları da
        //     yeniden parent'lıyordu.
        foreach (var k in _kartlar)
        {
            var kalacak = sayfadakiler.Contains(k);
            if (kalacak) continue;
            if (_yalnizCanli && !k.Canli && SayfadaYeriVar(gorunurler, k))
                continue;            // gizli ama denemeye devam etsin
            k.Durdur();
        }

        var mevcut = Duvar.Children.Cast<UIElement>().ToList();
        if (!mevcut.SequenceEqual(sayfadakiler))
        {
            // Ağaçtan çıkarılacakların akışı yukarıda durduruldu; burada
            // yalnız yerleşim değişiyor.
            Duvar.Children.Clear();
            foreach (var k in sayfadakiler) Duvar.Children.Add(k);
        }
        Duvar.Rows = 0;
        Duvar.Columns = SutunSayisi(sayfadakiler.Count);
        Duvar.UpdateLayout();          // tutamaç oluşsun, sonra oynatıcı atanır

        // Zaten çalışanı yeniden başlatma: süzgeç her tazelendiğinde akışı
        // kesmek görüntüyü sürekli söndürür (ve canlılık değişimi yeni bir
        // tazeleme tetikleyip döngü kurar).
        _ = KademeliBaslat(sayfadakiler.Where(k => !k.Calisiyor).ToList());

        var cokSayfa = SayfaSayisi > 1;
        SayfaCubugu.Visibility = cokSayfa ? Visibility.Visible : Visibility.Collapsed;
        SayfaYazisi.Text = $"Sayfa {_sayfa + 1} / {SayfaSayisi}";
        OncekiSayfa.IsEnabled = _sayfa > 0;
        SonrakiSayfa.IsEnabled = _sayfa < SayfaSayisi - 1;

        var gizli = _kartlar.Count - gorunurler.Count;
        DurumYazisi.Text = _kartlar.Count == 0 ? ""
            : gizli > 0 ? $"{sayfadakiler.Count}/{_kartlar.Count} kamera · {gizli} gizli"
            : cokSayfa ? $"{sayfadakiler.Count}/{_kartlar.Count} kamera"
            : $"{_kartlar.Count} kamera";
    }

    /// <summary>
    /// Gizlenen kart canlansaydı ilk sayfalarda yer bulur muydu?
    ///
    /// Yirmi kameralı bir listede görüntü vermeyen hepsini arka planda
    /// çalıştırmak gereksiz; yalnız geçerli sayfanın kapsayacağı kadarı
    /// denemeye devam eder.
    /// </summary>
    private bool SayfadaYeriVar(List<KameraKarti> gorunurler, KameraKarti kart)
    {
        var sinir = (_sayfa + 1) * SayfaBasinaKamera;
        var sira = _kartlar.IndexOf(kart);
        return sira < sinir + SayfaBasinaKamera;
    }

    /// <summary>Menüden yapılan akış seçimi bir sonraki açılışta da geçerli olsun.</summary>
    private void AyarlariKaydet()
    {
        try { _depo.Kaydet(); }
        catch (Exception e) { Gunluk.Hata("ayar kaydetme", e); }
    }

    /// <summary>
    /// Kartları arayla başlatır.
    ///
    /// Hepsini aynı anda açmak NVR'a bir çırpıda on bir RTSP isteği
    /// gönderiyor; cihaz bir kısmını geri çeviriyor ve o kartlar "alt akış
    /// yok" sanıp ana akışa düşüyordu (2560×1440, duvar için çok pahalı).
    /// Aralarına kısa bir bekleme koymak bunu bitiriyor.
    /// </summary>
    private async System.Threading.Tasks.Task KademeliBaslat(List<KameraKarti> kartlar)
    {
        // İlk kart da beklesin: uygulama yeni açıldığında VLC motoru henüz
        // kurulurken gönderilen ilk istek hata dönebiliyor.
        await System.Threading.Tasks.Task.Delay(250);

        foreach (var k in kartlar)
        {
            if (!_kartlar.Contains(k)) continue;        // bu arada liste değişmiş
            if (k.Calisiyor) continue;                  // başka bir tur zaten başlatmış
            k.Baslat(anaAkis: !k.Kamera.DuvardaAltAkis);
            await System.Threading.Tasks.Task.Delay(350);
        }
    }

    /// <summary>Kart sayısına göre kareye en yakın sütun sayısı (4→2, 9→3, 6→3).</summary>
    private static int SutunSayisi(int adet) =>
        adet <= 1 ? 1 : (int)Math.Ceiling(Math.Sqrt(adet));

    /// <summary>
    /// Kamerayı ayrı bir pencerede büyük gösterir.
    ///
    /// Duvarın içinde büyütmek, diğer kartların durdurulup görsel ağaçtan
    /// çıkarılmasını gerektiriyordu — video pencereleri yerel (HWND) olduğu
    /// için gizlemek işe yaramıyor — ve geri dönüşte duvardaki bütün kameralar
    /// baştan bağlanıyordu. Ayrı pencere duvara hiç dokunmaz.
    /// </summary>
    private void BuyutmeyiDegistir(KameraKarti kart)
    {
        if (_buyukPencere is { IsLoaded: true })
        {
            _buyukPencere.Activate();
            return;
        }
        var pencere = new BuyutmePenceresi(kart.Kamera, _motor, _tunel, _ptz, this);
        _buyukPencere = pencere;
        pencere.Closed += (_, _) => { if (_buyukPencere == pencere) _buyukPencere = null; };
        pencere.Show();
        Gunluk.Yaz($"{kart.Kamera.Ad}: büyütme penceresi açıldı");
    }

    // ── Üst çubuk ────────────────────────────────────────────────────────
    private void Duzen_Degisti(object sender, RoutedEventArgs e)
    {
        // XAML yüklenirken IsChecked="True" bu olayı tetikliyor; denetimler henüz yok
        if (!IsLoaded || Duvar is null) return;
        if (sender is not RadioButton r || r.Tag is not string t) return;
        if (!int.TryParse(t, out var boy)) return;

        _sayfaBoyu = boy;
        _sayfa = 0;
        SayfayiGoster();
    }

    private void OncekiSayfa_Click(object sender, RoutedEventArgs e) => SayfaDegistir(-1);
    private void SonrakiSayfa_Click(object sender, RoutedEventArgs e) => SayfaDegistir(+1);

    private void SayfaDegistir(int yon)
    {
        var yeni = _sayfa + yon;
        if (yeni < 0 || yeni >= SayfaSayisi) return;
        _sayfa = yeni;
        SayfayiGoster();
    }

    private void Kameralar_Click(object sender, RoutedEventArgs e)
    {
        var pencere = new KameraYonetimi(_depo, _tunel, _motor) { Owner = this };
        if (pencere.ShowDialog() == true)
        {
            foreach (var k in _depo.Kameralar) _ptz.Unut(k.Id);
            KartlariKur();
        }
    }

    private void TumunuYenile_Click(object sender, RoutedEventArgs e)
    {
        foreach (var k in Duvar.Children.OfType<KameraKarti>())
            k.Baslat(k.Buyuk || !k.Kamera.DuvardaAltAkis);
    }

    private void TamEkran_Click(object sender, RoutedEventArgs e) => TamEkranDegistir();

    private void TamEkranDegistir()
    {
        if (WindowStyle != WindowStyle.None)
        {
            _oncekiDurum = WindowState;
            _oncekiStil = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            UstCubuk.Visibility = Visibility.Collapsed;
            AltCubuk.Visibility = Visibility.Collapsed;
        }
        else
        {
            WindowStyle = _oncekiStil;
            WindowState = _oncekiDurum;
            UstCubuk.Visibility = Visibility.Visible;
            AltCubuk.Visibility = Visibility.Visible;
        }
    }

    private void PencereTus(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                TamEkranDegistir(); e.Handled = true; break;

            case Key.Escape:
                if (WindowStyle == WindowStyle.None) { TamEkranDegistir(); e.Handled = true; }
                break;

            case Key.D1 or Key.NumPad1: Duzen1.IsChecked = true; e.Handled = true; break;
            case Key.D2 or Key.NumPad2: Duzen4.IsChecked = true; e.Handled = true; break;
            case Key.D3 or Key.NumPad3: Duzen9.IsChecked = true; e.Handled = true; break;
            case Key.D4 or Key.NumPad4: Duzen16.IsChecked = true; e.Handled = true; break;
            case Key.D0 or Key.NumPad0: DuzenHepsi.IsChecked = true; e.Handled = true; break;

            case Key.G:
                YalnizCanli.IsChecked = !(YalnizCanli.IsChecked == true);
                e.Handled = true; break;

            case Key.PageUp or Key.Left: SayfaDegistir(-1); e.Handled = true; break;
            case Key.PageDown or Key.Right: SayfaDegistir(+1); e.Handled = true; break;
        }
    }
}
