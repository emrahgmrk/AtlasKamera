; ATLAS Kamera â€” kurulum betiÄŸi (Inno Setup 6)
;
; Derlemek iÃ§in:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" kurulum.iss
;
; Ã–nce daÄŸÄ±tÄ±m klasÃ¶rÃ¼ hazÄ±r olmalÄ±:
;   dotnet publish -c Release -r win-x64 --self-contained true -o <KaynakKlasor>
;
; Kurulum, .NET ve VLC dÃ¢hil her ÅŸeyi taÅŸÄ±r â€” hedef bilgisayara baÅŸka bir ÅŸey
; kurmak gerekmez.

#define Ad "ATLAS Kamera"
#define Surum "1.0.0"
#define Yayinci "ATLAS"
#define ExeAdi "AtlasKamera.exe"
#define KaynakKlasor "C:\Users\EMRAH\Desktop\AtlasKamera-Dagitim"
#define KameraListesi "C:\Users\EMRAH\Desktop\kameralar-paylasim.xml"

[Setup]
AppId={{8F3C2A41-9D7E-4B18-A6C5-ATLASKAMERA01}
AppName={#Ad}
AppVersion={#Surum}
AppVerName={#Ad} {#Surum}
AppPublisher={#Yayinci}
DefaultDirName={autopf}\AtlasKamera
DefaultGroupName={#Ad}
DisableProgramGroupPage=yes
OutputDir=C:\Users\EMRAH\Desktop
OutputBaseFilename=AtlasKamera-Kurulum-{#Surum}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#ExeAdi}
; GÃ¼ncelleme paketleri uygulamanÄ±n kendi gÃ¼ncelleyicisinden gelir; kurulum
; yalnÄ±z ilk kurulum ve elle yÃ¼kseltme iÃ§indir.
VersionInfoVersion={#Surum}

[Languages]
Name: "turkce"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "MasaÃ¼stÃ¼ne kÄ±sayol ekle"; GroupDescription: "KÄ±sayollar:"
Name: "startup"; Description: "Bilgisayar aÃ§Ä±lÄ±nca ATLAS Kamera'yÄ± baÅŸlat"; GroupDescription: "BaÅŸlangÄ±Ã§:"; Flags: unchecked

[Files]
Source: "{#KaynakKlasor}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Kamera listesi: kurulumdan sonra uygulamadan iÃ§e aktarÄ±lÄ±r
Source: "{#KameraListesi}"; DestDir: "{app}"; DestName: "kameralar-paylasim.xml"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#Ad}"; Filename: "{app}\{#ExeAdi}"
Name: "{group}\{#Ad} kaldÄ±r"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#Ad}"; Filename: "{app}\{#ExeAdi}"; Tasks: desktopicon
Name: "{commonstartup}\{#Ad}"; Filename: "{app}\{#ExeAdi}"; Tasks: startup

[Run]
Filename: "{app}\{#ExeAdi}"; Description: "ATLAS Kamera'yÄ± ÅŸimdi baÅŸlat"; Flags: nowait postinstall skipifsilent

[Messages]
turkce.FinishedLabel=Kurulum tamamlandÄ±.%n%nKamera listesini yÃ¼klemek iÃ§in uygulamada [Kameralar] > [Ä°Ã§e aktar] deyip kurulum klasÃ¶rÃ¼ndeki "kameralar-paylasim.xml" dosyasÄ±nÄ± seÃ§in. Aktarma parolasÄ± size ayrÄ±ca bildirilmiÅŸtir.

[Code]
// Uygulama Ã§alÄ±ÅŸÄ±yorken kurulum yapÄ±lÄ±rsa dosyalar kilitli olur.
function InitializeSetup(): Boolean;
var
  Kod: Integer;
begin
  Result := True;
  if Exec('taskkill.exe', '/IM AtlasKamera.exe /F', '', SW_HIDE,
          ewWaitUntilTerminated, Kod) then
    Sleep(1500);
end;

function InitializeUninstall(): Boolean;
var
  Kod: Integer;
begin
  Result := True;
  Exec('taskkill.exe', '/IM AtlasKamera.exe /F', '', SW_HIDE,
       ewWaitUntilTerminated, Kod);
  Exec('taskkill.exe', '/IM dh-p2p.exe /F', '', SW_HIDE,
       ewWaitUntilTerminated, Kod);
  Sleep(1000);
end;

