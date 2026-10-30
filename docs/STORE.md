# Poppy'yi Microsoft Store'da yayınlama

Bu rehber, Poppy'nin ücretli Store sürümünü yayınlamak için gereken adımları anlatır. Kod ve paket hazır. Aşağıdaki adımları hesap sahibi olarak senin yapman gerekiyor.

## 1. Geliştirici hesabı

1. <https://partner.microsoft.com/dashboard/registration> adresine git. Bireysel hesap ücretsiz.
2. Kimlik doğrulaması bittikten sonra **Apps and games → New product → MSIX or PWA app** yolunu izle.
3. İsim olarak **Poppy** ayır. İsim alınmışsa *Poppy – File Ring* gibi bir varyasyon dene.

## 2. Paket kimliğini koda yaz

Partner Center'da **Product management → Product identity** sayfasındaki üç değeri `packaging/store.json` dosyasına kopyala:

```json
{
  "IdentityName": "12345KaanBahaSever.Poppy",
  "Publisher": "CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX",
  "PublisherDisplayName": "Kaan Baha Sever"
}
```

## 3. Paketi üret

```powershell
./build.ps1 -Version 1.0.0
```

`artifacts/release/Poppy-1.0.0-x64.msix` dosyası oluşur. Paketi imzalamana gerek yok, Store kendisi imzalar.

## 4. Gönderim (Submission)

| Bölüm | Ne yazılacak |
|---|---|
| Pricing | Ücretli. Örneğin 4,99 $. İstersen 7 günlük deneme de açabilirsin. |
| Properties | Kategori: *Productivity*. Alt kategori yok. |
| Age ratings | Anketi doldur. Sonuç büyük ihtimalle "3+" çıkar. |
| Packages | `Poppy-1.0.0-x64.msix` dosyasını yükle. |
| Store listing (en-us, tr-tr) | Açıklama aşağıda. Ekran görüntüleri `docs/screenshots` klasöründe. |
| Submission options → Restricted capabilities | `runFullTrust` için gerekçe: *"Poppy is a desktop (Win32) app. It needs full trust to show its ring overlay above other apps while files are being dragged, and to run FFmpeg/Office for conversions."* |

### Kısa açıklama (TR)
> Bir dosyayı sürükle, bir tuşa dokun, halkadan seç. Poppy dosyaları uygulama açmadan dönüştürür, sıkıştırır, keser ve paylaşır.

### Kısa açıklama (EN)
> Drag a file, tap a key, pick from the ring. Poppy converts, compresses, trims and shares files without opening an app.

## 5. Güncellemeler

Yeni sürüm için `src/Poppy/Poppy.csproj` dosyasındaki `<Version>` değerini artır ve `./build.ps1 -Version x.y.z` komutunu çalıştır. Çıkan yeni `.msix` dosyasıyla Partner Center'da yeni bir gönderim oluştur.

## Notlar

- Store sürümü kendini GitHub'dan güncellemez, güncellemeleri Store yapar. Bu kural kodda zaten var (`AutoStart.IsPackaged`).
- "Windows ile başlat" seçeneği Store sürümünde MSIX `StartupTask` ile çalışır. Kullanıcı bunu Ayarlar → Uygulamalar → Başlangıç'tan da kapatabilir.
- HEIC kaydetmek için kullanıcının bilgisayarında Store'daki "HEVC Video Extensions" uzantısı kurulu olmalı. Kurulu değilse halkadaki HEIC dilimi bunu söyler.
