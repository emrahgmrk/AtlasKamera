using System;
using System.Windows;
using System.Windows.Input;
using AtlasKamera.Gorunum;
using AtlasKamera.Modeller;
using AtlasKamera.Servisler;

namespace AtlasKamera.Pencereler;

/// <summary>
/// Tek kamerayı büyük gösteren ayrı pencere.
///
/// NEDEN AYRI PENCERE
/// ------------------
/// Büyütme eskiden duvarın içinde yapılıyordu: büyütülen kart tek başına
/// kalıyor, diğerleri durdurulup görsel ağaçtan çıkarılıyordu. Geri dönünce
/// hepsi baştan bağlanmak zorundaydı — duvar birkaç saniye "Bağlanıyor…"
/// yazıyordu. Ağaçta bırakıp yalnız gizlemek de olmuyor: LibVLC'nin video
/// penceresi yerel (HWND) ve gizlenince ya kayboluyor ya da duvarın üstünde
/// hayalet gibi çizilmeye devam ediyor.
///
/// Ayrı pencere bu ikilemi ortadan kaldırıyor: duvar olduğu gibi akmaya devam
/// eder, büyük görüntü kendi penceresinde ana akışla açılır.
///
/// Kırpma (ekranı doldur) burada kapalıdır: büyük görüntüde kameranın kendi
/// yazısı (tarih, kanal adı) kenarlarda duruyor ve kırpma onu kesiyordu.
/// </summary>
public sealed class BuyutmePenceresi : Window
{
    private readonly KameraKarti _kart;

    public BuyutmePenceresi(Kamera kamera, VlcMotor motor, P2PTunel tunel, PtzIstemci ptz,
                            Window sahip)
    {
        // Kameranın kopyası: büyük görüntüde kırpma yok, duvardaki kartın
        // ayarları da bundan etkilenmez.
        var kopya = kamera.Kopyala();
        kopya.EkraniDoldur = false;

        Title = string.IsNullOrWhiteSpace(kamera.Ad) ? "Kamera" : kamera.Ad;
        Owner = sahip;
        Background = System.Windows.Media.Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = Math.Min(1600, SystemParameters.WorkArea.Width * 0.9);
        Height = Math.Min(900, SystemParameters.WorkArea.Height * 0.9);
        ShowInTaskbar = false;

        _kart = new KameraKarti(kopya, motor, tunel, ptz);
        Content = _kart;

        Loaded += (_, _) =>
        {
            // Büyük pencerede ana akış: tam çözünürlük burada anlamlı.
            _kart.BuyuklukDegisti(true);
            _kart.Baslat(anaAkis: true);
        };

        // Kartın büyüt düğmesi/çift tık burada "kapat" demek
        _kart.BuyutIstendi += (_, _) => Close();
        _kart.TamEkranIstendi += (_, _) => TamEkraniDegistir();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (WindowStyle == WindowStyle.None) TamEkraniDegistir();
                else Close();
                e.Handled = true;
            }
            else if (e.Key == Key.F11)
            {
                TamEkraniDegistir();
                e.Handled = true;
            }
        };

        Closed += (_, _) =>
        {
            try { _kart.Dispose(); }
            catch (Exception e) { Gunluk.Hata("büyütme penceresi kapanışı", e); }
        };
    }

    private void TamEkraniDegistir()
    {
        if (WindowStyle != WindowStyle.None)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;      // Maximized'den geçiş gerekiyor
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
        }
    }
}
