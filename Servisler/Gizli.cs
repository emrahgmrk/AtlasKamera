using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AtlasKamera.Servisler;

/// <summary>
/// Parola saklama.
///
/// Diskteki ayar dosyasında parolalar Windows DPAPI ile (yalnız o kullanıcı
/// hesabında çözülebilir) saklanır — dosya kopyalansa başka makinede okunamaz.
///
/// Dışa aktarmada DPAPI kullanılamaz (hedef makine çözemez): orada kullanıcının
/// verdiği aktarma parolasından PBKDF2 ile anahtar türetilip AES-GCM uygulanır.
/// </summary>
public static class Gizli
{
    private static readonly byte[] Kapsam = Encoding.UTF8.GetBytes("AtlasKamera");

    public static string Sifrele(string? duzMetin)
    {
        if (string.IsNullOrEmpty(duzMetin)) return "";
        try
        {
            var veri = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(duzMetin), Kapsam, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(veri);
        }
        catch
        {
            return "";
        }
    }

    public static string Coz(string? sifreli)
    {
        if (string.IsNullOrEmpty(sifreli)) return "";
        try
        {
            var veri = ProtectedData.Unprotect(
                Convert.FromBase64String(sifreli), Kapsam, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(veri);
        }
        catch
        {
            return "";
        }
    }

    // ── Dışa/içe aktarma: taşınabilir şifreleme ──────────────────────────
    private const int TuzUzunluk = 16;
    private const int NonceUzunluk = 12;
    private const int EtiketUzunluk = 16;
    private const int Yineleme = 120_000;

    /// <summary>Aktarma parolasıyla şifreler; sonuç base64 (tuz|nonce|etiket|şifre).</summary>
    public static string AktarmaIcinSifrele(string duzMetin, string aktarmaParolasi)
    {
        if (string.IsNullOrEmpty(duzMetin)) return "";
        var tuz = RandomNumberGenerator.GetBytes(TuzUzunluk);
        var nonce = RandomNumberGenerator.GetBytes(NonceUzunluk);
        var anahtar = Anahtar(aktarmaParolasi, tuz);

        var acik = Encoding.UTF8.GetBytes(duzMetin);
        var sifre = new byte[acik.Length];
        var etiket = new byte[EtiketUzunluk];
        using (var aes = new AesGcm(anahtar, EtiketUzunluk))
            aes.Encrypt(nonce, acik, sifre, etiket);

        using var ms = new MemoryStream();
        ms.Write(tuz); ms.Write(nonce); ms.Write(etiket); ms.Write(sifre);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Aktarma parolasıyla çözer; parola yanlışsa boş döner.</summary>
    public static string AktarmadanCoz(string paket, string aktarmaParolasi)
    {
        if (string.IsNullOrEmpty(paket)) return "";
        try
        {
            var ham = Convert.FromBase64String(paket);
            if (ham.Length < TuzUzunluk + NonceUzunluk + EtiketUzunluk) return "";

            var tuz = ham.AsSpan(0, TuzUzunluk).ToArray();
            var nonce = ham.AsSpan(TuzUzunluk, NonceUzunluk).ToArray();
            var etiket = ham.AsSpan(TuzUzunluk + NonceUzunluk, EtiketUzunluk).ToArray();
            var sifre = ham.AsSpan(TuzUzunluk + NonceUzunluk + EtiketUzunluk).ToArray();

            var acik = new byte[sifre.Length];
            using (var aes = new AesGcm(Anahtar(aktarmaParolasi, tuz), EtiketUzunluk))
                aes.Decrypt(nonce, sifre, etiket, acik);
            return Encoding.UTF8.GetString(acik);
        }
        catch
        {
            return "";
        }
    }

    private static byte[] Anahtar(string parola, byte[] tuz) =>
        Rfc2898DeriveBytes.Pbkdf2(parola ?? "", tuz, Yineleme, HashAlgorithmName.SHA256, 32);
}
