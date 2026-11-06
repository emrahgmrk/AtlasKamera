# ATLAS Kamera

Hikvision ve Dahua NVR'ları ile Dahua P2P cihazlarını izleyen Windows
uygulaması. Kameradan gelen görüntü **olduğu gibi** (H.265 dâhil) ekran
kartında çözülür — hiçbir yeniden kodlama yoktur.

## Neden web arayüzünden akıcı

ATLAS'ın tarayıcı sayfası görüntüyü tarayıcıya verebilmek için her kamerayı
H.264'e çeviriyordu. Ölçümde bunun bedeli şuydu:

| | ATLAS web sayfası | Bu uygulama |
|---|---|---|
| Kodlama | Her kamera H.265 → H.264 (ffmpeg) | Yok, kaynak akış doğrudan |
| Duvardaki çözünürlük | 1920×1080'e yükseltilmiş | 640×360 alt akış (kameranın kendi) |
| Toplam veri (9 kamera) | 33,2 Mbit/s | kaynak alt akışlar, kat kat düşük |
| Oynatma | Tarayıcı MSE, hızı 0,1×–5× arası oynuyordu | Doğrudan, sabit hız |

30 saniyelik ölçümde dokuz kamera aynı anda açıkken yerel kameralar **%99**,
P2P kameralar **%91** akıcılık verdi.

## Kurulum

Kurulum gerektirmez. `AtlasKamera-Dagitim` klasörünü hedef bilgisayara
kopyalayıp `AtlasKamera.exe` dosyasını çalıştırmak yeterlidir (.NET ve VLC
klasörün içinde gelir).

## Kullanım

- **Kameralar** düğmesi: kamera ekleme/düzenleme, bağlantı sınama,
  XML dışa/içe aktarma.
- **Düzen**: 1 / 2×2 / 3×3 / 4×4.
- **Çift tıkla**: kamerayı büyüt. Büyütülen kamera **ana akışa** geçer,
  diğerleri durdurulur; böylece tam çözünürlük tek kameraya ayrılır.
- **Esc**: duvara dön. **F11**: tam ekran. **G**: yalnız canlı kameralar.
- Sayfa gezintisi: alt çubuktaki ◀ ▶ ya da **PgUp / PgDn**.

### Kart araçları (fare kartın üzerine gelince belirir)

| Düğme | İşi |
|---|---|
| **SD / HD** | Alt akış ↔ ana akış. Seçim diske kaydedilir, küçük kartta da çalışır. |
| **⛶** | Ekranı doldur: kenarlardaki siyah bantları kırparak görüntüyü karta yayar. |
| **✥** | PTZ yön tuşları (yalnız PTZ kameralarda). |
| **⟳** | Akışı yeniden kur. |
| **🗖** | Büyüt / küçült. |

**Sağ tık menüsü** aynı işleri ve fazlasını verir: büyüt, tam ekran,
ana/alt akış, ekranı doldur, yenile, anlık görüntü kaydet
(`Resimler\AtlasKamera`), PTZ, kamera ayarları.

**Yalnız canlı** (üst çubuk ya da **G**): görüntü vermeyen kameraları duvardan
kaldırır. Gizlenenler durdurulmaz — arka planda bağlanmayı denemeye devam
eder, görüntü gelince kendiliğinden geri gelirler.

### Kamera ekleme

| Alan | Açıklama |
|---|---|
| Marka | Adres düzenini ve PTZ protokolünü belirler (Hikvision ISAPI / Dahua CGI) |
| Bağlantı | *Ağdan doğrudan*: IP ile. *Dahua P2P*: cihaz seri numarası ile (uzak şantiye) |
| Kanal | NVR'daki kanal numarası; tek kamerada 1 |
| Duvarda alt akış | İşaretliyse duvarda alt akış, büyütünce ana akış kullanılır |

Alt akışı kapalı olan NVR kanallarında uygulama kendiliğinden ana akışa düşer.

### Başka bilgisayara taşıma

**Kameralar → Dışa aktar (XML)**. Parolaların da gitmesini isterseniz bir
*aktarma parolası* belirlersiniz; parolalar o parolayla şifrelenir
(PBKDF2 + AES-GCM). Hedef bilgisayarda **İçe aktar** deyip aynı parolayı
girersiniz.

Diskte parolalar Windows DPAPI ile saklanır: ayar dosyası kopyalansa bile
başka kullanıcı hesabında okunamaz.

## Akıcılık ölçümü

```
AtlasKamera.exe --sina 30          # 30 sn, bütün kameralar aynı anda
AtlasKamera.exe --sina 30 --sirayla   # teker teker
```

Rapor ekrana ve `%APPDATA%\AtlasKamera\son-olcum.txt` dosyasına yazılır.
*Akıcılık* kesintisiz geçen sürenin oranıdır; *Kesinti* tamponlama sayısı ve
toplam süresidir. %98 ve üstü, kesintisiz = sorunsuz.

## Görüntü neden bazen kartın dışına kaçıyordu

LibVLC görüntüyü yerel bir pencereye (HWND) çiziyor; bu pencere WPF'in
düzenine tabi değil. Bu yüzden üç kural kodda sabit:

1. **Kart ağaçtan çıkarılmadan önce akışı durdurulur.** Video çıkışı bağlıyken
   kartı taşımak görüntünün kartın dışına kaçmasına, bazı durumlarda sürecin
   çökmesine yol açıyor.
2. **Yerleşim istenen hâldeyse ağaca dokunulmaz.** Her tazelemede bütün
   kartları çıkarıp eklemek, çalışan akışları da yeniden konumlandırıyordu.
3. **Durum paneli video penceresinin İÇİNDE** (`VideoView.Content`). Kardeş
   öğe olarak denendi: video penceresi her zaman üstte kaldığı için kartlar
   bembeyaz göründü.

Aynı tuzaklar [ChakraDeep8/RtspCameraViewer](https://github.com/ChakraDeep8/RtspCameraViewer)
projesinde de belgelenmiş; oradaki çözümler bu uygulamaya uyarlandı.

## Dahua P2P hakkında

Uzak şantiyedeki cihaza `dh-p2p.exe` ile Dahua bulutu üzerinden bağlanılır
(uygulamanın yanında gelir). Bilinmesi gerekenler:

- **NVR tek P2P oturumuna izin verir.** ATLAS web sunucusu aynı cihaza
  bağlıyken bu uygulama bağlanamaz; ikisinden biri kapalı olmalı.
- Tüneli üst üste açıp kapatmak cihazın oturum yuvasını 10–30 dakika tıkar.
  Uygulama bu yüzden tüneli açık tutar ve kapanıştan sonra kısa bir soğuma bekler.
- Aktarma bant genişliği sınırlıdır; P2P kameralarda **alt akış** kullanın.

Tüneller uygulamayla birlikte kapanır; çökme sonrası kalan tünel bir sonraki
açılışta temizlenir.

## Dosyalar

| Yol | İçerik |
|---|---|
| `%APPDATA%\AtlasKamera\kameralar.xml` | Kamera listesi (parolalar DPAPI ile şifreli) |
| `%APPDATA%\AtlasKamera\son-olcum.txt` | Son akıcılık ölçümü |
| `%APPDATA%\AtlasKamera\gunluk\` | P2P tünel günlükleri |

## Kaynak

| Klasör | İçerik |
|---|---|
| `Modeller/` | Kamera tanımı, adres üretimi |
| `Servisler/` | VLC motoru, P2P tüneli, PTZ, depo, ölçüm |
| `Gorunum/` | Duvardaki kamera kartı |
| `Pencereler/` | Kamera yönetimi |

Derleme: `dotnet build` · Dağıtım: `dotnet publish -c Release -r win-x64 --self-contained true -o <klasör>`
