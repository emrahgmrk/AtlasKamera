using System;
using System.Collections.Generic;
using LibVLCSharp.Shared;

namespace AtlasKamera.Servisler;

/// <summary>
/// Paylaşılan LibVLC örneği ve oynatıcı üretimi.
///
/// NEDEN BU AYARLAR
/// ----------------
/// Tarayıcıdaki çözüm (go2rtc + MSE) her kamerayı H.264'e yeniden kodlamak
/// zorundaydı; burada akış kameradan geldiği gibi (H.265 dâhil) ekran kartında
/// çözülür — yeniden kodlama yok, işlemci yükü yok, gecikme düşük.
///
/// Donma karşıtı seçenekler:
///   drop-late-frames / skip-frames : geciken kareyi beklemek yerine atar.
///       Canlı izlemede bir kareyi kaçırmak, görüntüyü dondurmaktan iyidir.
///   clock-jitter=0 / clock-synchro=0 : VLC akışın saatine uymaya çalışmasın;
///       kamera saatleri kayabiliyor, uyum çabası düzenli takılma yapıyordu.
///   network-caching : tampon. Düşük = az gecikme, yüksek = dalgalanmaya dayanıklı.
///       Yerel ağda 200-300 ms yeter; P2P/VPN'de daha yükseği gerekir.
/// </summary>
public sealed class VlcMotor : IDisposable
{
    public LibVLC LibVlc { get; }

    /// <summary>Yerel ağ kameraları için tampon (ms).</summary>
    public const int YerelTampon = 300;

    /// <summary>P2P / uzak ağ için tampon — aktarma dalgalandığından daha yüksek.</summary>
    public const int UzakTampon = 1200;

    /// <param name="olcumKipi">
    /// Ekran yokken (komut satırı ölçümü) kullanılır: sahte video çıkışı
    /// eklenir, böylece kareler yine de çözülür ve sayaçlar dolar. Arayüzde
    /// bu kip kapalıdır — görüntü gerçek pencereye çizilir.
    /// </param>
    public VlcMotor(bool donanimHizlandirma = true, bool ayrintiliGunluk = false,
                    bool olcumKipi = false)
    {
        var secenekler = new List<string>
        {
            "--no-audio",                  // kamera duvarında ses yok
            "--no-osd",
            "--no-snapshot-preview",
            "--no-stats",
            "--drop-late-frames",
            "--skip-frames",
            "--clock-jitter=0",
            "--clock-synchro=0",
            "--no-video-title-show",
            "--rtsp-tcp",                  // UDP'de parça kaybı donmaya yol açıyor
        };

        // Donanım kod çözme ATLAS_KAMERA_DONANIM=1 ile açılır; VARSAYILAN KAPALI.
        //
        // d3d11va ile dört eşzamanlı H.265 akışta VLC "buffer deadlock
        // prevented" veriyor ve akışlar "Ended" olup kopuyordu (günlükte
        // ayrıca "surface dimensions differ from avcodec dimensions"
        // uyarıları). Duvardaki alt akışlar küçük (640×360); yazılımla
        // çözmek bu makinede ucuz ve kopmaları bitiriyor.
        var donanimIstendi = Environment.GetEnvironmentVariable("ATLAS_KAMERA_DONANIM") == "1";
        secenekler.Add(donanimHizlandirma && donanimIstendi
            ? "--avcodec-hw=d3d11va"
            : "--avcodec-hw=none");

        if (olcumKipi)
        {
            // Pencereye çizme, ama kareleri çöz: akıcılık sayaçları dolsun
            secenekler.Add("--vout=dummy");
            secenekler.Add("--no-drop-late-frames");   // ölçümde kare atlamasın
            secenekler.Remove("--drop-late-frames");
            secenekler.Remove("--skip-frames");
        }

        if (ayrintiliGunluk)
        {
            secenekler.Add("--verbose=2");
        }
        else
        {
            secenekler.Add("--quiet");
            secenekler.Add("--verbose=0");
        }

        Core.Initialize();
        LibVlc = new LibVLC(secenekler.ToArray());

        // Teşhis için: ATLAS_KAMERA_AYRINTILI=1 ile VLC'nin kendi iletileri
        // uygulama günlüğüne düşer. Akış neden "Ended" oluyor, RTSP ne
        // yanıtlıyor — bunlar ancak buradan görülüyor.
        if (ayrintiliGunluk ||
            Environment.GetEnvironmentVariable("ATLAS_KAMERA_AYRINTILI") == "1")
        {
            try
            {
                LibVlc.Log += (_, e) =>
                {
                    if (e.Level is LogLevel.Error or LogLevel.Warning)
                        Gunluk.Yaz($"VLC[{e.Level}] {e.Module}: {e.Message}");
                };
            }
            catch (Exception e)
            {
                Gunluk.Hata("VLC günlüğü", e);
            }
        }
    }

    /// <summary>Bir akış için oynatıcı ve ortam üretir.</summary>
    public (MediaPlayer oynatici, Media ortam) Olustur(string rtspAdresi, int tamponMs)
    {
        var ortam = new Media(LibVlc, new Uri(rtspAdresi));
        ortam.AddOption($":network-caching={tamponMs}");
        ortam.AddOption($":live-caching={tamponMs}");
        ortam.AddOption(":rtsp-tcp");
        ortam.AddOption(":no-audio");
        // Güvenlik akışlarında altyazı/OSD kanalı yok; her etkin iz bir kod
        // çözücü ve tampon demek (ChakraDeep8/RtspCameraViewer deneyimi).
        ortam.AddOption(":no-spu");
        // Yeniden bağlanmada eski veriyi atla
        ortam.AddOption(":rtsp-frame-buffer-size=1000000");

        var oynatici = new MediaPlayer(LibVlc)
        {
            EnableHardwareDecoding = true,
            Mute = true,
            EnableMouseInput = false,
            EnableKeyInput = false,
        };
        return (oynatici, ortam);
    }

    public void Dispose()
    {
        LibVlc.Dispose();
    }
}
