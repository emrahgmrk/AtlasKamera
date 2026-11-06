using System;
using AtlasKamera.Modeller;

namespace AtlasKamera.Servisler;

/// <summary>Kamera kaydından RTSP ve HTTP adreslerini üretir.</summary>
public static class Adresler
{
    /// <summary>
    /// Oynatılacak RTSP adresi. P2P kameralarda hedef, tünelin yerel portudur
    /// (tünelin önceden açılmış olması gerekir).
    /// </summary>
    public static string Rtsp(Kamera k, bool anaAkis, P2PTunel tunel)
    {
        var (host, port) = Hedef(k, tunel);
        var kimlik = Kimlik(k);
        var yol = k.Yol(anaAkis);
        return $"rtsp://{kimlik}{host}:{port}{yol}";
    }

    /// <summary>PTZ/CGI isteklerinin gideceği taban adres (kimlik bilgisi içermez).</summary>
    public static string HttpTaban(Kamera k, P2PTunel tunel)
    {
        if (k.Baglanti == BaglantiTuru.DahuaP2P)
            return $"http://127.0.0.1:{tunel.WebPort(k.SeriNo)}";
        var port = k.WebPort <= 0 ? 80 : k.WebPort;
        return $"http://{k.Host}:{port}";
    }

    private static (string host, int port) Hedef(Kamera k, P2PTunel tunel) =>
        k.Baglanti == BaglantiTuru.DahuaP2P
            ? ("127.0.0.1", tunel.Port(k.SeriNo))
            : (k.Host, k.Port <= 0 ? 554 : k.Port);

    /// <summary>"kullanıcı:parola@" — parolada : / @ gibi karakterler olabilir, kaçışlanır.</summary>
    private static string Kimlik(Kamera k)
    {
        if (string.IsNullOrEmpty(k.Kullanici)) return "";
        var kul = Uri.EscapeDataString(k.Kullanici);
        var par = Uri.EscapeDataString(k.Parola ?? "");
        return string.IsNullOrEmpty(par) ? $"{kul}@" : $"{kul}:{par}@";
    }

    /// <summary>Günlükte/arayüzde gösterilecek, parolası gizlenmiş adres.</summary>
    public static string Maskeli(Kamera k, bool anaAkis, P2PTunel tunel)
    {
        var (host, port) = Hedef(k, tunel);
        var kul = string.IsNullOrEmpty(k.Kullanici) ? "" : $"{k.Kullanici}:***@";
        return $"rtsp://{kul}{host}:{port}{k.Yol(anaAkis)}";
    }
}
