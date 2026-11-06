using System;
using System.Xml.Serialization;

namespace AtlasKamera.Modeller;

/// <summary>Kameranın hangi üreticinin adres düzenini kullandığı.</summary>
public enum Marka
{
    Hikvision,
    Dahua,
    Diger,          // RTSP yolu elle yazılır
}

/// <summary>Kameraya nasıl ulaşıldığı.</summary>
public enum BaglantiTuru
{
    Rtsp,           // ağdan doğrudan (yerel ya da VPN/Tailscale üzerinden uzak şantiye)
    DahuaP2P,       // Dahua bulut tüneli (dh-p2p.exe) — cihazın seri numarasıyla
}

/// <summary>
/// Bir kamera ya da NVR kanalı.
///
/// Duvarda alt akış, büyütünce ana akış gösterilir: NVR'lar iki akışı birden
/// yayınlar, alt akış (D1/720p) duvarda yeterli ve işlemciyi/ağı yormaz.
/// </summary>
public class Kamera
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Ad { get; set; } = "";
    public string Konum { get; set; } = "";
    public int Sira { get; set; }
    public bool Aktif { get; set; } = true;

    public Marka Marka { get; set; } = Marka.Hikvision;
    public BaglantiTuru Baglanti { get; set; } = BaglantiTuru.Rtsp;

    // ── Doğrudan RTSP ──
    public string Host { get; set; } = "";
    public int Port { get; set; } = 554;

    // ── Dahua P2P ──
    public string SeriNo { get; set; } = "";

    public string Kullanici { get; set; } = "";

    /// <summary>Diskte DPAPI ile şifreli; düz metin yalnız bellekte.</summary>
    public string ParolaSifreli { get; set; } = "";

    [XmlIgnore]
    public string Parola
    {
        get => Servisler.Gizli.Coz(ParolaSifreli);
        set => ParolaSifreli = Servisler.Gizli.Sifrele(value);
    }

    /// <summary>NVR kanal numarası (tek kameralarda 1).</summary>
    public int Kanal { get; set; } = 1;

    /// <summary>Boşsa markadan üretilir. Marka = Diğer ise zorunlu.</summary>
    public string OzelAnaYol { get; set; } = "";
    public string OzelAltYol { get; set; } = "";

    /// <summary>Duvarda alt akış kullan (büyütünce ana akışa geçilir).</summary>
    public bool DuvardaAltAkis { get; set; } = true;

    /// <summary>
    /// Görüntü kartı tamamen doldursun (kenarlarda siyah bant kalmasın).
    ///
    /// Kamera 16:9, kart başka oranda olduğunda VLC görüntüyü sığdırır ve
    /// kenarlarda siyah bant bırakır. Bu seçenekle görüntü kırpılarak
    /// (CropGeometry) kartı doldurur — kenarlardan bir miktar görüntü gider.
    /// </summary>
    public bool EkraniDoldur { get; set; }

    public bool Ptz { get; set; }

    /// <summary>PTZ/CGI için cihazın web portu.</summary>
    public int WebPort { get; set; } = 80;

    public override string ToString() => string.IsNullOrWhiteSpace(Ad) ? "(adsız)" : Ad;

    public Kamera Kopyala()
    {
        var k = (Kamera)MemberwiseClone();
        return k;
    }

    /// <summary>Markanın kanal düzenine göre RTSP yolu.</summary>
    public string Yol(bool anaAkis)
    {
        var ozel = anaAkis ? OzelAnaYol : OzelAltYol;
        if (!string.IsNullOrWhiteSpace(ozel))
            return ozel.StartsWith('/') ? ozel : "/" + ozel;

        var kanal = Kanal < 1 ? 1 : Kanal;
        return Marka switch
        {
            // Hikvision: 101 = kanal 1 ana akış, 102 = kanal 1 alt akış
            Marka.Hikvision => $"/Streaming/Channels/{kanal}{(anaAkis ? "01" : "02")}",
            // Dahua: subtype 0 ana, 1 alt
            Marka.Dahua => $"/cam/realmonitor?channel={kanal}&subtype={(anaAkis ? 0 : 1)}",
            _ => "/",
        };
    }
}
