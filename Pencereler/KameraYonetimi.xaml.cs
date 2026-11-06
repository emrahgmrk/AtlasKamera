using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AtlasKamera.Modeller;
using AtlasKamera.Servisler;
using LibVLCSharp.Shared;
using Microsoft.Win32;
// System.Windows.Media.MediaPlayer ile çakışıyor: burada kastedilen VLC oynatıcısı
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace AtlasKamera.Pencereler;

/// <summary>
/// Kamera listesi yönetimi: ekleme, düzenleme, sınama ve XML dışa/içe aktarma.
///
/// Değişiklikler pencere kapanana kadar geçici bir listede tutulur; "Kaydet"
/// denince diske yazılır, "Vazgeç" denince atılır.
/// </summary>
public partial class KameraYonetimi : Window
{
    private readonly KameraDepo _depo;
    private readonly P2PTunel _tunel;
    private readonly VlcMotor _motor;
    private readonly List<Kamera> _calisma;
    private Kamera? _secili;
    private bool _yukleniyor;

    public KameraYonetimi(KameraDepo depo, P2PTunel tunel, VlcMotor motor)
    {
        InitializeComponent();
        _depo = depo;
        _tunel = tunel;
        _motor = motor;
        _calisma = depo.Kameralar.Select(k => k.Kopyala()).ToList();
        Liste.ItemsSource = _calisma;
        if (_calisma.Count > 0) Liste.SelectedIndex = 0;
    }

    // ── Liste ────────────────────────────────────────────────────────────
    private void Liste_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FormuYaz();                       // önceki seçimi kaydet
        _secili = Liste.SelectedItem as Kamera;
        FormuOku();
    }

    private void Ekle_Click(object sender, RoutedEventArgs e)
    {
        FormuYaz();
        var k = new Kamera
        {
            Ad = "Yeni kamera",
            Sira = _calisma.Count == 0 ? 0 : _calisma.Max(x => x.Sira) + 1,
        };
        _calisma.Add(k);
        ListeyiTazele();
        Liste.SelectedItem = k;
        AdKutu.Focus();
        AdKutu.SelectAll();
    }

    private void Sil_Click(object sender, RoutedEventArgs e)
    {
        if (_secili is null) return;
        var onay = MessageBox.Show(this, $"\"{_secili.Ad}\" silinsin mi?", "Kamera sil",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (onay != MessageBoxResult.Yes) return;
        _calisma.Remove(_secili);
        _secili = null;
        ListeyiTazele();
    }

    private void Yukari_Click(object sender, RoutedEventArgs e) => Tasi(-1);
    private void Asagi_Click(object sender, RoutedEventArgs e) => Tasi(+1);

    private void Tasi(int yon)
    {
        if (_secili is null) return;
        var i = _calisma.IndexOf(_secili);
        var j = i + yon;
        if (i < 0 || j < 0 || j >= _calisma.Count) return;
        (_calisma[i], _calisma[j]) = (_calisma[j], _calisma[i]);
        for (var s = 0; s < _calisma.Count; s++) _calisma[s].Sira = s;
        ListeyiTazele();
        Liste.SelectedItem = _secili;
    }

    private void ListeyiTazele()
    {
        var secili = _secili;
        Liste.ItemsSource = null;
        Liste.ItemsSource = _calisma;
        if (secili is not null && _calisma.Contains(secili)) Liste.SelectedItem = secili;
        else if (_calisma.Count > 0) Liste.SelectedIndex = 0;
        else { _secili = null; Form.IsEnabled = false; }
    }

    // ── Form ─────────────────────────────────────────────────────────────
    private void FormuOku()
    {
        _yukleniyor = true;
        Form.IsEnabled = _secili is not null;
        var k = _secili;
        if (k is null) { _yukleniyor = false; return; }

        AdKutu.Text = k.Ad;
        KonumKutu.Text = k.Konum;
        MarkaKutu.SelectedIndex = (int)k.Marka;
        BaglantiKutu.SelectedIndex = (int)k.Baglanti;
        HostKutu.Text = k.Host;
        PortKutu.Text = k.Port.ToString();
        SeriKutu.Text = k.SeriNo;
        KullaniciKutu.Text = k.Kullanici;
        ParolaKutu.Password = k.Parola;
        KanalKutu.Text = k.Kanal.ToString();
        WebPortKutu.Text = k.WebPort.ToString();
        AnaYolKutu.Text = k.OzelAnaYol;
        AltYolKutu.Text = k.OzelAltYol;
        AltAkisKutu.IsChecked = k.DuvardaAltAkis;
        PtzKutu.IsChecked = k.Ptz;
        AktifKutu.IsChecked = k.Aktif;

        AlanlariDuzenle();
        _yukleniyor = false;
        OnizlemeyiTazele();
    }

    private void FormuYaz()
    {
        if (_secili is null || _yukleniyor) return;
        var k = _secili;
        k.Ad = AdKutu.Text.Trim();
        k.Konum = KonumKutu.Text.Trim();
        k.Marka = (Marka)Math.Max(0, MarkaKutu.SelectedIndex);
        k.Baglanti = (BaglantiTuru)Math.Max(0, BaglantiKutu.SelectedIndex);
        k.Host = HostKutu.Text.Trim();
        k.Port = Sayi(PortKutu.Text, 554);
        k.SeriNo = SeriKutu.Text.Trim();
        k.Kullanici = KullaniciKutu.Text.Trim();
        if (ParolaKutu.Password != k.Parola) k.Parola = ParolaKutu.Password;
        k.Kanal = Sayi(KanalKutu.Text, 1);
        k.WebPort = Sayi(WebPortKutu.Text, 80);
        k.OzelAnaYol = AnaYolKutu.Text.Trim();
        k.OzelAltYol = AltYolKutu.Text.Trim();
        k.DuvardaAltAkis = AltAkisKutu.IsChecked == true;
        k.Ptz = PtzKutu.IsChecked == true;
        k.Aktif = AktifKutu.IsChecked == true;
    }

    private static int Sayi(string metin, int varsayilan) =>
        int.TryParse(metin?.Trim(), out var s) && s > 0 ? s : varsayilan;

    private void Marka_Degisti(object sender, SelectionChangedEventArgs e)
    {
        if (_yukleniyor) return;
        FormuYaz();
        OnizlemeyiTazele();
    }

    private void Baglanti_Degisti(object sender, SelectionChangedEventArgs e)
    {
        if (_yukleniyor) return;
        FormuYaz();
        AlanlariDuzenle();
        OnizlemeyiTazele();
    }

    private void AlanlariDuzenle()
    {
        var p2p = BaglantiKutu.SelectedIndex == (int)BaglantiTuru.DahuaP2P;
        RtspAlanlari.Visibility = p2p ? Visibility.Collapsed : Visibility.Visible;
        P2pAlanlari.Visibility = p2p ? Visibility.Visible : Visibility.Collapsed;
        // P2P her zaman Dahua
        if (p2p && MarkaKutu.SelectedIndex != (int)Marka.Dahua)
            MarkaKutu.SelectedIndex = (int)Marka.Dahua;
    }

    private void OnizlemeyiTazele()
    {
        if (_secili is null) return;
        var ana = Adresler.Maskeli(_secili, true, _tunel);
        var alt = Adresler.Maskeli(_secili, false, _tunel);
        AdresOnizleme.Text = $"Ana akış : {ana}\nAlt akış : {alt}";
    }

    // ── Sınama ───────────────────────────────────────────────────────────
    private async void Sina_Click(object sender, RoutedEventArgs e)
    {
        FormuYaz();
        if (_secili is null) return;
        var k = _secili.Kopyala();

        SinaDugme.IsEnabled = false;
        Sonuc("Deneniyor…", bilgi: true);
        try
        {
            if (k.Baglanti == BaglantiTuru.DahuaP2P)
            {
                Sonuc("Uzak bağlantı kuruluyor…", bilgi: true);
                if (!await _tunel.HazirlaAsync(k.SeriNo, 25))
                {
                    Sonuc("Tünel açılamadı: " + _tunel.SonHata(k.SeriNo), bilgi: false);
                    return;
                }
            }

            var adres = Adresler.Rtsp(k, anaAkis: !k.DuvardaAltAkis, _tunel);
            var sonuc = await Task.Run(() => Dene(adres, k));
            Sonuc(sonuc.mesaj, sonuc.ok);
        }
        catch (Exception ex)
        {
            Sonuc("Hata: " + ex.Message, bilgi: false);
        }
        finally
        {
            SinaDugme.IsEnabled = true;
        }
    }

    /// <summary>Akışı kısa süre açıp çözünürlük/kodek bilgisini okur.</summary>
    private (bool ok, string mesaj) Dene(string adres, Kamera k)
    {
        MediaPlayer? oynatici = null;
        Media? ortam = null;
        try
        {
            var tampon = k.Baglanti == BaglantiTuru.DahuaP2P
                ? VlcMotor.UzakTampon : VlcMotor.YerelTampon;
            (oynatici, ortam) = _motor.Olustur(adres, tampon);

            using var hazir = new ManualResetEventSlim(false);
            oynatici.Playing += (_, _) => hazir.Set();
            var hataVar = false;
            oynatici.EncounteredError += (_, _) => { hataVar = true; hazir.Set(); };

            oynatici.Play(ortam);
            if (!hazir.Wait(TimeSpan.FromSeconds(12)))
                return (false, "Yanıt yok (adres, port ya da kimlik bilgisi hatalı olabilir).");
            if (hataVar)
                return (false, "Bağlanılamadı (kimlik bilgisi ya da yol hatalı olabilir).");

            // Çözünürlük bilgisi birkaç yüz ms içinde dolar
            uint g = 0, y = 0;
            for (var i = 0; i < 20 && g == 0; i++)
            {
                Thread.Sleep(150);
                try { oynatici.Size(0, ref g, ref y); } catch { }
            }
            var kodek = ortam.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video)
                             .Codec;
            var kodekAdi = kodek == 0 ? "" : FourCc(kodek);
            var boyut = g > 0 ? $"{g}×{y}" : "çözünürlük okunamadı";
            return (true, $"Bağlandı — {boyut}{(kodekAdi.Length > 0 ? " · " + kodekAdi : "")}");
        }
        catch (Exception e)
        {
            return (false, "Hata: " + e.Message);
        }
        finally
        {
            try { oynatici?.Stop(); } catch { }
            try { oynatici?.Dispose(); } catch { }
            try { ortam?.Dispose(); } catch { }
        }
    }

    private static string FourCc(uint kod)
    {
        var b = BitConverter.GetBytes(kod);
        var s = new string(b.Select(x => (char)x).ToArray()).Trim('\0', ' ');
        return s.Length == 4 ? s.ToUpperInvariant() : "";
    }

    private void Sonuc(string mesaj, bool bilgi)
    {
        SinamaSonucu.Text = mesaj;
        SinamaSonucu.Foreground = new SolidColorBrush(bilgi
            ? Color.FromRgb(0x22, 0xC5, 0x5E) : Color.FromRgb(0xF8, 0x71, 0x71));
    }

    // ── Dışa / içe aktarma ───────────────────────────────────────────────
    private void DisaAktar_Click(object sender, RoutedEventArgs e)
    {
        FormuYaz();
        if (_calisma.Count == 0)
        {
            MessageBox.Show(this, "Aktarılacak kamera yok.", "Dışa aktar");
            return;
        }

        var soru = MessageBox.Show(this,
            "Kamera parolaları da aktarılsın mı?\n\n" +
            "Evet: parolalar sizin belirleyeceğiniz bir aktarma parolasıyla şifrelenir; " +
            "hedef bilgisayarda aynı parolayla açılır.\n" +
            "Hayır: parolalar aktarılmaz, hedef bilgisayarda elle girilir.",
            "Dışa aktar", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (soru == MessageBoxResult.Cancel) return;

        string? aktarmaParolasi = null;
        if (soru == MessageBoxResult.Yes)
        {
            var p = ParolaSor.Sor(this, "Aktarma parolası belirleyin",
                "Bu parolayı hedef bilgisayarda içe aktarırken soracağız.");
            if (string.IsNullOrEmpty(p)) return;
            aktarmaParolasi = p;
        }

        var kaydet = new SaveFileDialog
        {
            Title = "Kamera listesini dışa aktar",
            Filter = "ATLAS kamera listesi (*.xml)|*.xml",
            FileName = $"atlas-kameralar-{DateTime.Now:yyyyMMdd}.xml",
        };
        if (kaydet.ShowDialog(this) != true) return;

        try
        {
            _depo.DisaAktar(kaydet.FileName, _calisma, aktarmaParolasi);
            MessageBox.Show(this, $"{_calisma.Count} kamera aktarıldı:\n{kaydet.FileName}",
                "Dışa aktar", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Aktarılamadı: " + ex.Message, "Dışa aktar",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void IceAktar_Click(object sender, RoutedEventArgs e)
    {
        var ac = new OpenFileDialog
        {
            Title = "Kamera listesi içe aktar",
            Filter = "ATLAS kamera listesi (*.xml)|*.xml|Tüm dosyalar (*.*)|*.*",
        };
        if (ac.ShowDialog(this) != true) return;

        try
        {
            var kameralar = KameraDepo.IceAktarOku(ac.FileName, null, out var parolaGerekli);
            if (parolaGerekli)
            {
                var p = ParolaSor.Sor(this, "Aktarma parolası",
                    "Bu dosyadaki parolalar şifreli. Dışa aktarırken belirlenen parolayı girin.");
                if (string.IsNullOrEmpty(p)) return;
                kameralar = KameraDepo.IceAktarOku(ac.FileName, p, out _);
                if (kameralar.Count > 0 && kameralar.All(k => string.IsNullOrEmpty(k.Parola)))
                    MessageBox.Show(this,
                        "Parolalar çözülemedi (aktarma parolası yanlış olabilir). " +
                        "Kameralar parolasız eklendi.", "İçe aktar",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            FormuYaz();
            var basi = _calisma.Count == 0 ? 0 : _calisma.Max(x => x.Sira) + 1;
            foreach (var k in kameralar) { k.Sira = basi++; _calisma.Add(k); }
            ListeyiTazele();
            MessageBox.Show(this, $"{kameralar.Count} kamera eklendi. Kaydet'e basmayı unutmayın.",
                "İçe aktar", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Okunamadı: " + ex.Message, "İçe aktar",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Kaydet / vazgeç ──────────────────────────────────────────────────
    private void Kaydet_Click(object sender, RoutedEventArgs e)
    {
        FormuYaz();
        var eksik = _calisma.FirstOrDefault(k =>
            string.IsNullOrWhiteSpace(k.Ad) ||
            (k.Baglanti == BaglantiTuru.Rtsp && string.IsNullOrWhiteSpace(k.Host)) ||
            (k.Baglanti == BaglantiTuru.DahuaP2P && string.IsNullOrWhiteSpace(k.SeriNo)));
        if (eksik is not null)
        {
            MessageBox.Show(this,
                $"\"{eksik.Ad}\" eksik: ad ve " +
                (eksik.Baglanti == BaglantiTuru.DahuaP2P ? "seri numarası" : "IP adresi") +
                " zorunlu.", "Kaydet", MessageBoxButton.OK, MessageBoxImage.Warning);
            Liste.SelectedItem = eksik;
            return;
        }

        try
        {
            _depo.Kameralar.Clear();
            foreach (var k in _calisma) _depo.Kameralar.Add(k);
            _depo.Kaydet();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Kaydedilemedi: " + ex.Message, "Kaydet",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Vazgec_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
