# Thank Goodness You're Here! Türkçe Yama

*Thank Goodness You're Here!* oyununu baştan sona Türkçeleştiren yama: altyazılar, menüler,
oyun içindeki tabelalar ve duvar yazıları, ara sahneler ve yazı tipi.

Yama oyunun dosyalarına dokunmaz. Oyun çalışırken devreye giren bir
[BepInEx](https://github.com/BepInEx/BepInEx) eklentisidir; bu sayede oyun güncellendiğinde
bozulmaz, kaldırdığınızda da geride hiçbir iz bırakmaz.

## Özellikler

- Tüm altyazılar ve menüler Türkçe
- Tabelalar, afişler ve duvar yazıları gibi oyun içi görseller Türkçe
- Ara sahne videoları Türkçe
- Türkçe karakterleri destekleyen, oyunun özgün görünümüne uygun yazı tipi
- *Ayarlar > Dil* menüsünde ilk sırada **Türkçe** seçeneği; dilediğiniz an oyunun diğer
  dillerine dönebilirsiniz

## Kurulum

### Windows

1. [**TGYH_TR.exe**](https://github.com/SkipperSkipTR/TGYH-TR/releases/download/Kurulum/TGYH_TR.exe)
   dosyasını indirip çalıştırın.
2. Kurulum aracı oyunu çoğu zaman kendiliğinden bulur. Bulamazsa **Oyun Klasörünü Seçin**
   düğmesine basıp oyun klasöründeki `Thank Goodness You're Here!.exe` dosyasını gösterin.
3. **Yamayı Yükle** düğmesine basın. Araç, gerekiyorsa önce BepInEx'i, ardından yamanın en
   güncel sürümünü indirip kurar.
4. Oyunu başlatın.

> **İlk açılış 1-2 dakika sürebilir.** BepInEx gerekli dosyaları yalnızca ilk seferde
> oluşturur; ekran bir süre siyah kalırsa beklemeniz yeterli.

Oyun Türkçe açılır. Açılmazsa *Ayarlar > Dil* menüsünden ilk seçenek olan **Türkçe**'yi seçin.

Yamanın eski sürümünü (oyun dosyalarını değiştiren sürüm) kullanıyorsanız ayrıca bir şey
yapmanıza gerek yok: kurulum aracı önce özgün dosyaları geri yükler, sonra yeni yamayı kurar.

### Steam Deck ve Linux (Proton)

Masaüstü modunda bir terminal açıp şu komutları çalıştırın:

```bash
curl -LO https://raw.githubusercontent.com/SkipperSkipTR/TGYH-TR/main/install.sh
bash install.sh "<oyun klasörü>"
```

Ardından Steam'de oyuna sağ tıklayıp *Özellikler > Başlatma Seçenekleri* alanına şunu yazın:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

### macOS (deneysel)

> Bu yöntem oyunun macOS dosyaları üzerinde incelendi, ancak **gerçek bir Mac'te henüz
> denenmedi**. Denerseniz sonucu bildirmeniz çok yardımcı olur.

**1. Oyun klasörünü bulun.** Aradığınız klasör, içinde `Thank Goodness You're Here!.app`
dosyası olan klasördür. Genellikle şuradadır:

- Steam: `~/Library/Application Support/Steam/steamapps/common/Thank Goodness You're Here!`
- Epic Games: `/Users/Shared/Epic Games/ThankGoodnessYoureHere`

**2. Apple Silicon (M1 ve sonrası) kullanıyorsanız Rosetta'yı kurun.** Yama oyunu Rosetta
üzerinden çalıştırır. Terminal'i açıp şu komutu bir kez çalıştırın (Intel Mac'lerde bu adımı
atlayın):

```bash
softwareupdate --install-rosetta
```

**3. Yamayı kurun.** Terminal'de aşağıdaki iki komutu çalıştırın; ikinci komuttaki yolu kendi
oyun klasörünüzle değiştirin:

```bash
curl -LO https://raw.githubusercontent.com/SkipperSkipTR/TGYH-TR/main/install.sh
bash install.sh "/Users/Shared/Epic Games/ThankGoodnessYoureHere"
```

**4. Oyunu `run_bepinex.sh` ile başlatın.** Kurulum, oyun klasörüne `run_bepinex.sh` adında
bir başlatıcı koyar. Yama yalnızca oyun bu başlatıcıyla açıldığında çalışır; oyunu doğrudan
simgesinden ya da mağaza uygulamasından açarsanız İngilizce açılır.

- **Terminal'den:**

  ```bash
  cd "/Users/Shared/Epic Games/ThankGoodnessYoureHere"
  ./run_bepinex.sh
  ```

- **Steam'den:** Oyuna sağ tıklayıp *Özellikler > Başlatma Seçenekleri* alanına, yolu kendi
  oyun klasörünüze göre düzelterek şunu yazın. Bundan sonra oyunu Steam'den her zamanki gibi
  başlatabilirsiniz:

  ```
  "/Users/<kullanıcı adınız>/Library/Application Support/Steam/steamapps/common/Thank Goodness You're Here!/run_bepinex.sh" %command%
  ```

İlk açılış 1-2 dakika sürebilir.

### Elle kurulum

Kurulum aracını kullanmak istemiyorsanız:

1. Oyununuza uygun [BepInEx 6 (Unity IL2CPP)](https://builds.bepinex.dev/projects/bepinex_be)
   sürümünü indirip oyun klasörüne çıkarın. Oyunun Windows sürümü 32 bit olduğu için
   `win-x86` paketini seçin.
2. Oyun klasöründe `BepInEx/config/BepInEx.cfg` dosyasını aşağıdaki içerikle oluşturun.
   Dosya zaten varsa `UnityLogListening` değerini `false` yapın.
   **Bu ayar yapılmazsa oyun açılırken çöker.**

   ```ini
   [Logging]
   UnityLogListening = false

   [Logging.Console]
   Enabled = false
   ```
3. [Sürümler](https://github.com/SkipperSkipTR/TGYH-TR/releases) sayfasındaki en güncel
   `TGYHTurkishPatch-v*.zip` dosyasını indirip oyun klasörüne çıkarın.

## Güncelleme

Kurulum aracını yeniden çalıştırıp **Yamayı Güncelle** düğmesine basın. Steam Deck, Linux ve
macOS'ta `install.sh` betiğini yeniden çalıştırmanız yeterli.

## Kaldırma

- **Windows:** Kurulum aracında **Yamayı Sil** düğmesine basın.
- **Steam Deck, Linux, macOS:** `bash install.sh --uninstall "<oyun klasörü>"`

BepInEx'i kurulum aracı kurduysa yamayla birlikte o da kaldırılır. Kendi kurduğunuz BepInEx'e
ve diğer eklentilerinize dokunulmaz.

## Sık karşılaşılan sorunlar

**Oyun ilk açılışta siyah ekranda bekliyor.**
Normaldir; 1-2 dakika bekleyin. Sonraki açılışlar hızlıdır.

**Oyun açılır açılmaz kapanıyor.**
Oyun klasöründeki `BepInEx/LogOutput.log` dosyasında "Class::Init signatures have been
exhausted" yazıyorsa `BepInEx/config/BepInEx.cfg` içindeki `UnityLogListening` değeri `false`
olmalıdır. Kurulum aracını yeniden çalıştırmak bunu kendiliğinden düzeltir.

**Yazılar hâlâ İngilizce.**
*Ayarlar > Dil* menüsünden ilk seçenek olan Türkçe'yi seçin.

**Bazı görseller bir an İngilizce görünüp sonra Türkçeye dönüyor.**
Görseller, oyun onları yükledikçe değiştirilir; yavaş disklerde kısa bir gecikme olabilir.

**Sorun devam ediyor.**
Oyun klasöründeki `BepInEx/LogOutput.log` dosyasını ekleyerek bir
[sorun kaydı](https://github.com/SkipperSkipTR/TGYH-TR/issues) açın. Oyun sürümünüzü ve oyunu
hangi mağazadan aldığınızı da yazarsanız çözüm hızlanır.

## Katkıda bulunmak

Çeviri hatası, yazım yanlışı ya da Türkçeleştirilmemiş bir görsel fark ederseniz
[sorun kaydı](https://github.com/SkipperSkipTR/TGYH-TR/issues) açabilirsiniz.

Eklentinin kaynak kodu, çeviri dosyaları ve sürüm hazırlama araçları bu depodadır. Derleme ve
sürüm yayımlama adımları için [DEVELOPMENT.md](DEVELOPMENT.md) dosyasına bakın (İngilizce).

## Teşekkürler

Yama, [BepInEx](https://github.com/BepInEx/BepInEx) üzerinde çalışır; BepInEx bu depoda
dağıtılmaz, kurulum sırasında kendi sunucusundan indirilir.

*Thank Goodness You're Here!*, Coal Supper tarafından geliştirilmiş ve Panic tarafından
yayımlanmıştır. Bu yama bir hayran çalışmasıdır; geliştirici ya da yayıncı ile bir bağı
yoktur.
