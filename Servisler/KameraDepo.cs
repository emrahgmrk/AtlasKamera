using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using AtlasKamera.Modeller;

namespace AtlasKamera.Servisler;

/// <summary>Dışa aktarma dosyasının kökü.</summary>
[XmlRoot("AtlasKameraListesi")]
public class AktarmaPaketi
{
    public int Surum { get; set; } = 1;
    public DateTime Tarih { get; set; } = DateTime.Now;

    /// <summary>Parolalar aktarma parolasıyla şifrelendiyse true.</summary>
    public bool ParolalarDahil { get; set; }

    [XmlArray("Kameralar"), XmlArrayItem("Kamera")]
    public List<Kamera> Kameralar { get; set; } = new();
}

/// <summary>
/// Kamera listesinin diskteki hâli.
///
/// Ayarlar %APPDATA%\AtlasKamera\kameralar.xml içinde; parolalar DPAPI ile
/// şifreli. Dışa aktarmada ayrı bir dosya üretilir (parolalar istenirse
/// aktarma parolasıyla yeniden şifrelenir), böylece liste başka makineye
/// taşınabilir.
/// </summary>
public class KameraDepo
{
    public static string Klasor => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AtlasKamera");

    public static string Dosya => Path.Combine(Klasor, "kameralar.xml");

    public ObservableCollection<Kamera> Kameralar { get; } = new();

    private static readonly XmlSerializer Seri = new(typeof(AktarmaPaketi));

    public void Yukle()
    {
        Kameralar.Clear();
        if (!File.Exists(Dosya)) return;
        try
        {
            using var fs = File.OpenRead(Dosya);
            if (Seri.Deserialize(fs) is AktarmaPaketi p)
                foreach (var k in p.Kameralar.OrderBy(x => x.Sira).ThenBy(x => x.Ad))
                    Kameralar.Add(k);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"Kamera listesi okunamadı ({Dosya}): {e.Message}", e);
        }
    }

    public void Kaydet()
    {
        Directory.CreateDirectory(Klasor);
        var p = new AktarmaPaketi { Kameralar = Kameralar.ToList() };
        // Önce geçici dosyaya yaz: yazma yarıda kalırsa liste kaybolmasın
        var gecici = Dosya + ".yeni";
        using (var fs = File.Create(gecici))
            Seri.Serialize(fs, p);
        if (File.Exists(Dosya)) File.Replace(gecici, Dosya, null);
        else File.Move(gecici, Dosya);
    }

    /// <summary>
    /// Listeyi XML'e yazar. <paramref name="aktarmaParolasi"/> verilirse kamera
    /// parolaları da (taşınabilir biçimde) eklenir; verilmezse parola alanları boşalır.
    /// </summary>
    public void DisaAktar(string yol, IEnumerable<Kamera> secilenler, string? aktarmaParolasi)
    {
        var sifreli = !string.IsNullOrEmpty(aktarmaParolasi);
        var paket = new AktarmaPaketi { ParolalarDahil = sifreli };
        foreach (var k in secilenler)
        {
            var kopya = k.Kopyala();
            kopya.ParolaSifreli = sifreli
                ? Gizli.AktarmaIcinSifrele(k.Parola, aktarmaParolasi!)
                : "";
            paket.Kameralar.Add(kopya);
        }
        using var fs = File.Create(yol);
        Seri.Serialize(fs, paket);
    }

    /// <summary>
    /// Dosyadaki kameraları okur (listeye eklemez). Parola şifreliyse
    /// <paramref name="aktarmaParolasi"/> ile çözülüp DPAPI'ye geçirilir.
    /// </summary>
    public static List<Kamera> IceAktarOku(string yol, string? aktarmaParolasi, out bool parolaGerekli)
    {
        using var fs = File.OpenRead(yol);
        var paket = Seri.Deserialize(fs) as AktarmaPaketi
                    ?? throw new InvalidOperationException("Dosya bir ATLAS kamera listesi değil.");

        parolaGerekli = paket.ParolalarDahil && string.IsNullOrEmpty(aktarmaParolasi);
        if (parolaGerekli) return paket.Kameralar;

        foreach (var k in paket.Kameralar)
        {
            k.Id = Guid.NewGuid();      // çakışma olmasın: içe aktarılan her kamera yeni kayıt
            if (paket.ParolalarDahil && !string.IsNullOrEmpty(k.ParolaSifreli))
            {
                var duz = Gizli.AktarmadanCoz(k.ParolaSifreli, aktarmaParolasi!);
                k.ParolaSifreli = "";
                k.Parola = duz;         // DPAPI ile bu makineye özel yeniden şifrelenir
            }
            else
            {
                k.ParolaSifreli = "";
            }
        }
        return paket.Kameralar;
    }
}
