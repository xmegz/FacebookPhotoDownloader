# Facebook Media Downloader

C# / .NET 10 alapú Facebook média letöltő Playwright böngészőautomatizálással.

A program a bejelentkezett Facebook munkamenetet használva képes egy profilról elérhető képek és videók letöltésére.

## Fő funkciók

* Facebook profil persistent bejelentkezés
* külön Chromium profil használata
* képek letöltése a legnagyobb elérhető felbontásban
* videók letöltése Facebook CDN-ről
* Facebook byte-range alapú videó streamek kezelése
* külön video és audio assetek felismerése
* automatikus `ffprobe` elemzés
* legjobb videóminőség kiválasztása
* video és audio összefűzése FFmpeg segítségével
* külön `photos` és `videos` könyvtár
* duplikált képek kiszűrése

## Követelmények

A programhoz szükséges:

* Windows 10 vagy Windows 11
* .NET 10 SDK
* Microsoft Playwright
* Chromium Playwright böngésző
* FFmpeg
* FFprobe

## Projekt létrehozása

Telepítsd a Playwright csomagot:


Fordítsd le a projektet:

```powershell
dotnet build
```

Ezután telepítsd a Chromium böngészőt:

```powershell
pwsh bin\Debug\net10.0\playwright.ps1 install chromium
```

## FFmpeg telepítése

Windows alatt például `winget` használatával:

```powershell
winget install Gyan.FFmpeg
```

Ellenőrzés:

```powershell
ffmpeg -version
```

és:

```powershell
ffprobe -version
```

Mindkét parancsnak működnie kell abból a terminálból, amelyből a programot indítod.

## Program indítása

A program első paramétere a Facebook URL.

A második opcionális paraméter a letöltési könyvtár.

Példa:

```powershell
dotnet run -- "https://www.facebook.com/groups/646199282939310/media/videos" "D:\Facebook\PROFIL"
```

Lefordított `.exe` használata esetén:

```powershell
FacebookMediaDownloader.exe "https://www.facebook.com/groups/646199282939310/media/photos" "D:\Facebook\PROFIL"
```

## Első indítás

Az első futáskor a program megnyit egy Chromium böngészőt.

A böngésző egy külön persistent profilt használ.

A profil helye:

```text
%LOCALAPPDATA%\FacebookMediaDownloader\ChromiumProfile
```

Például:

```text
C:\Users\felhasznalo\AppData\Local\FacebookMediaDownloader\ChromiumProfile
```

Az első indításkor:

1. jelentkezz be a Facebookra;
2. végezd el a kétlépcsős hitelesítést, ha szükséges;
3. állítsd be a Facebook cookie-hozzájárulást;
4. a konzolban nyomj `ENTER`-t.

A következő indításoknál a Facebook munkamenet normál esetben megmarad.

A program nem tárolja külön a Facebook-jelszót.

## Letöltési struktúra

Ha például ezt adod meg:

```powershell
FacebookMediaDownloader.exe `
    "https://www.facebook.com/PROFIL" `
    "D:\Facebook\PROFIL"
```

akkor az eredmény:

```text
D:\Facebook\PROFIL
│
├── photos
│   ├── 00001_2048x1365_52b9a52d31.jpg
│   ├── 00002_1536x2048_c23a087ce1.jpg
│   └── ...
│
├── videos
│   ├── 00001_123456789_1080p.mp4
│   ├── 00002_987654321_720p.mp4
│   └── ...
│
└── _temp
```

A `_temp` könyvtárat a program a videók feldolgozásához használja.

A sikeresen feldolgozott ideiglenes fájlokat automatikusan törli.

## Képek letöltése

A program:

1. összegyűjti a Facebook fotó URL-eket;
2. megnyitja a fotóoldalt;
3. megkeresi az elérhető Facebook CDN képeket;
4. megvizsgálja azok tényleges felbontását;
5. a legnagyobb képet tölti le.

A fájlnév tartalmazza a felbontást:

```text
00001_2048x1365_52b9a52d31.jpg
```

Ebben:

```text
00001
```

a sorszám,

```text
2048x1365
```

a kép felbontása,

```text
52b9a52d31
```

pedig egy rövidített egyedi azonosító.

## Videók letöltése

A Facebook videók gyakran nem hagyományos, egyetlen MP4 fájlként kerülnek továbbításra.

A Facebook például ilyen URL-eket használhat:

```text
video.mp4?...&bytestart=1322639&byteend=1649840
```

Ez csak egy videófragment, nem a teljes fájl.

A program ezért nem közvetlenül ezt menti el.

A folyamat:

```text
Facebook video oldal
        |
        v
Playwright hálózati figyelés
        |
        v
Facebook CDN MP4 assetek
        |
        v
bytestart / byteend eltávolítása
        |
        v
teljes média asset letöltése
        |
        v
ffprobe elemzés
        |
        +--> video 360p
        +--> video 720p
        +--> video 1080p
        +--> audio
        |
        v
legjobb video + audio
        |
        v
FFmpeg
        |
        v
kész MP4
```

## Videóminőség

A program minden letöltött assetet megvizsgál az `ffprobe` segítségével.

Például:

```text
Asset 1
  ffprobe: video 640x360, 475 kbps

Asset 2
  ffprobe: video 1280x720, 1800 kbps

Asset 3
  ffprobe: video 1920x1080, 3900 kbps

Asset 4
  ffprobe: audio, 128 kbps
```

Ezután automatikusan a legnagyobb felbontású videót választja:

```text
Legjobb video: 1920x1080
Audio asset: 128 kbps
```

és FFmpeg segítségével összefűzi őket.

## FFmpeg feldolgozás

Ha a Facebook külön video- és audiofájlt használ, a program lényegében ezt hajtja végre:

```powershell
ffmpeg `
    -i video.mp4 `
    -i audio.mp4 `
    -map 0:v:0 `
    -map 1:a:0 `
    -c copy `
    -movflags +faststart `
    output.mp4
```

A:

```text
-c copy
```

miatt nincs újrakódolás.

Ez azt jelenti, hogy:

* nincs minőségromlás;
* a feldolgozás gyors;
* az eredeti Facebook videó- és audiosáv kerül az eredménybe.

## Persistent Facebook session

A Playwright böngészőprofil itt található:

```text
%LOCALAPPDATA%\FacebookMediaDownloader\ChromiumProfile
```

Ezt ne töröld, ha szeretnéd megtartani a Facebook bejelentkezést.

Ha új Facebook sessiont szeretnél:

1. zárd be a programot;
2. töröld ezt a könyvtárat;
3. indítsd újra a programot.

A következő indulásnál újra be kell jelentkezni.

## Konzol kimenet

Egy sikeres videófeldolgozás például:

```text
[3/62]

https://www.facebook.com/PROFIL/videos/1517716313381066/

Elfogott MP4 request: 12
Egyedi asset: 4

dash_vp9-basic-gen2_360p
dash_vp9-basic-gen2_720p
dash_vp9-basic-gen2_1080p
dash_audio

Asset 1/4
  méret: 2.15 MB
  ffprobe: video 640x360, 475 kbps

Asset 2/4
  méret: 8.73 MB
  ffprobe: video 1280x720, 1800 kbps

Asset 3/4
  méret: 17.40 MB
  ffprobe: video 1920x1080, 3900 kbps

Asset 4/4
  méret: 0.72 MB
  ffprobe: audio, 128 kbps

Legjobb video: 1920x1080
Audio asset: 128 kbps

OK -> 00003_1517716313381066_1080p.mp4
```

## Gyakori hibák

### FFmpeg nincs a PATH-ban

Hiba:

```text
HIBA: ffmpeg nincs a PATH-ban.
```

Ellenőrizd:

```powershell
ffmpeg -version
```

Ha nem működik, telepítsd:

```powershell
winget install Gyan.FFmpeg
```

Ezután nyiss új PowerShell ablakot.

### FFprobe nincs a PATH-ban

Ellenőrzés:

```powershell
ffprobe -version
```

Az FFprobe normál esetben az FFmpeg csomag része.

### Playwright Chromium nincs telepítve

Futtasd:

```powershell
pwsh bin\Debug\net8.0\playwright.ps1 install chromium
```

### Facebook újra bejelentkezést kér

A session itt van:

```text
%LOCALAPPDATA%\FacebookMediaDownloader\ChromiumProfile
```

Ellenőrizd, hogy a könyvtár nem törlődik-e két futtatás között.

### Nem talál minden képet vagy videót

A Facebook dinamikusan tölti be a tartalmakat.

A program automatikusan lefelé görget, de nagyon nagy profiloknál előfordulhat, hogy a Facebook nem tölti be egyszerre az összes régi médiát.

Ilyen esetben érdemes közvetlenül a profil megfelelő szekcióját megadni, például:

```text
https://www.facebook.com/PROFIL/photos
```

vagy:

```text
https://www.facebook.com/PROFIL/videos
```

## Biztonság

A Chromium persistent profil Facebook session cookie-kat tartalmaz.

Ezért a következő könyvtár érzékeny adatnak tekintendő:

```text
%LOCALAPPDATA%\FacebookMediaDownloader\ChromiumProfile
```

Ne:

* másold nyilvános helyre;
* töltsd fel GitHubra;
* add át másnak;
* csomagold bele egy release-be.

Git használata esetén érdemes `.gitignore` fájlban kizárni a helyi outputot:

```gitignore
bin/
obj/
downloads/
_temp/
facebook-cookies.txt
```

## Build

Release build:

```powershell
dotnet build -c Release
```

Az executable ezután jellemzően itt lesz:

```text
bin\Release\net8.0\
```

## Publish

Windows x64 self-contained build például:

```powershell
dotnet publish `
    -c Release `
    -r win-x64 `
    --self-contained true
```

Az eredmény:

```text
bin\Release\net8.0\win-x64\publish\
```

A Playwright Chromium böngészőt ettől függetlenül telepíteni kell azon a gépen, ahol a program fut.

## Megjegyzés

A Facebook webes felülete és média-kiszolgálási mechanizmusa időről időre változik.

A program szándékosan nem a Facebook obfuszkált CSS class neveire épít, hanem főként:

* URL-struktúrára;
* Playwright hálózati eseményekre;
* Facebook CDN requestekre;
* tényleges képdimenziókra;
* `ffprobe` médiavizsgálatra.

Ez robusztusabbá teszi a megoldást, de Facebook-oldali változás esetén később szükség lehet a feldolgozási logika módosítására.
