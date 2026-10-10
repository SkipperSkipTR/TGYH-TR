# Thank Goodness You're Here! Türkçe Yama

*Thank Goodness You're Here!* için Türkçe yama. Yama oyun dosyalarını değiştirmez; oyun
çalışırken metinleri, yazı tipini, görselleri ve videoları Türkçeleriyle değiştiren bir
[BepInEx](https://github.com/BepInEx/BepInEx) eklentisidir. Bu sayede oyun güncellense de
dosyalar bozulmaz.

Kurulumdan sonra **Türkçe**, *Ayarlar > Dil* menüsündeki ilk seçenektir ve ilk açılışta
kendiliğinden seçilir. Aynı menüden istediğiniz zaman diğer dillere dönebilirsiniz.

## Kurulum

### Windows

1. [TGYH_TR.exe](https://github.com/SkipperSkipTR/TGYH-TR/releases/download/Kurulum/TGYH_TR.exe) dosyasını indirip çalıştırın.
2. Oyun kendiliğinden bulunamazsa "Oyun Klasörünü Seçin" butonuyla oyun klasöründeki
   `Thank Goodness You're Here!.exe` dosyasını seçin.
3. "Yamayı Yükle" butonuna basın. Kurulum aracı gerekirse BepInEx'i, ardından yamanın son
   sürümünü indirip kurar.
4. Oyunu başlatın. **İlk açılış 1-2 dakika sürebilir**; BepInEx gerekli dosyaları bir
   kereliğine oluşturur.

Yamayı güncellemek için aracı yeniden çalıştırıp "Yamayı Güncelle" butonuna, kaldırmak için
"Yamayı Sil" butonuna basın. Eski sürüm yama (oyun dosyalarını değiştiren) kuruluysa araç
önce özgün dosyaları `Yedek` klasöründen geri yükler.

### Steam Deck / Linux (Proton)

```bash
curl -LO https://raw.githubusercontent.com/SkipperSkipTR/TGYH-TR/main/install.sh
bash install.sh "<oyun klasörü>"
```

Ardından Steam'de oyunun başlatma seçeneklerine şunu ekleyin:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

### macOS (deneysel)

Aynı betik macOS sürümünde de çalışır; oyunun `.app` dosyasının bulunduğu klasörü verin.
Betik BepInEx'in macOS sürümünü kurar ve oyunun `run_bepinex.sh` ile başlatılması gerekir.
**Bu yol test edilmedi**; sorun yaşarsanız lütfen bildirin.

### Elle kurulum

1. Oyununuza uygun [BepInEx 6 (Unity IL2CPP)](https://builds.bepinex.dev/projects/bepinex_be)
   sürümünü oyun klasörüne çıkarın (Windows sürümü 32 bit olduğu için `win-x86`).
2. [Son sürümdeki](https://github.com/SkipperSkipTR/TGYH-TR/releases) `TGYHTurkishPatch-v*.zip`
   dosyasını oyun klasörüne çıkarın.

## Kaldırma

Kurulum aracındaki "Yamayı Sil" butonu ya da `bash install.sh --uninstall "<oyun klasörü>"`.
BepInEx'i kurulum aracı kurduysa o da kaldırılır; kendi kurduğunuz BepInEx'e dokunulmaz.

## Sorun giderme

- **Oyun ilk açılışta siyah ekranda bekliyor:** Normal; 1-2 dakika bekleyin.
- **Yazılar İngilizce:** *Ayarlar > Dil* menüsünden ilk seçenek olan Türkçe'yi seçin.
- **Yama çalışmıyor:** Oyun klasöründeki `BepInEx/LogOutput.log` dosyasıyla birlikte bir
  [sorun kaydı](https://github.com/SkipperSkipTR/TGYH-TR/issues) açın.

## Geliştirme

Eklentinin kaynak kodu, çeviri dosyaları ve sürüm hazırlama araçları bu depodadır.
Ayrıntılar için [DEVELOPMENT.md](DEVELOPMENT.md) (İngilizce).
