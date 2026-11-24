# Söz

Bu kılavuz Türkçedir ve Söz'ün kullanıcı belgesidir. Eski bölümlerde geçen "ajan" yardımcı (Claude Code, Codex vb.), "kanıt" deneme sonucu, "ölçüt" kontrol demektir. Ekranda artık "yardımcı", "deneme sonucu" ve "kontrol" yazar.

## Ne işe yarar
Sabah masana oturdun. Bir projede "giriş sayfasındaki yazım hatalarını düzelt" diye bir iş var. Eskiden bir terminal açar, Claude'u başlatır, işi yazar, arada gelip ne yaptığına bakar, bitti dediğinde testleri kendin çalıştırır, tutmazsa geri yollardın.

Şimdi Söz'ü açarsın (ekranın köşesindeki kapsüle tıkla ya da Ctrl+Alt+J). İşi kendi cümlenle yazarsın, Ctrl+Enter'a basarsın, işine dönersin. Söz ajanı arka planda kendisi çalıştırır. Ekranda ajanın ne yaptığını düz Türkçe satırlarla görürsün: "hesap.js dosyasını okuyor", "testleri çalıştırıyor", "2 dosyayı değiştirdi". Terminal açılmaz, komut yazmazsın.

Ajan "bitti" dediğinde Söz sözüne güvenmez, testleri kendisi çalıştırır. Geçerse iş yeşil "Kanıtıyla bitti" olur ve kanıtı (çıktı, ekran görüntüsü) önünde durur. Geçmezse ajanı hangi testin kaldığını söyleyerek geri yollar, en çok 3 tur. Yine geçmezse "Kaldı" der, asla "geçti" demez. Test bulunmayan klasörde iş "kanıtsız" diye işaretlenir.

Ajan tehlikeli bir şey yapmak isterse (klasör silmek, proje dışına yazmak) bekçi durdurur, sana "Evet / Hayır" diye sorar. Cevabını verince ajan kaldığı yerden sürer. Ajan sana bir şey sorarsa cevabı iş ekranından yazarsın, aynı konuşma devam eder. Yolda fikrin değişirse "Durdur" de. Söz önce "Bu iş durdurulsun mu? Sonra "Aynı işe yeni bir şey ekle" ile sürdürebilirsin." diye sorar. "Evet, durdur" dersen yardımcının süreci kapanır, "Vazgeç" dersen iş çalışmaya devam eder. Her şey yalnız bu bilgisayarda kalır.

## İlk 4 hareket
Söz'ü ilk açtığında klasör ve iş yoktur. Dört hareketle ilk kanıtlı iş çalışır:
1. **İşi yaz.** Büyük kutuya ne istediğini kendi cümlenle yaz ("Toplama fonksiyonundaki hatayı düzelt, testler geçsin").
2. **Klasörünü göster.** Ctrl+Enter'a basınca Söz hangi klasörde çalışacağını sorar. Masaüstü uygulamasında "Klasör seç" düğmesi klasör seçme penceresini açar. Yalnız tarayıcıda çalışıyorsan klasörün yolunu yazıp Enter'a basarsın.
3. **Bir kez onayla.** Söz klasöre bakar ve "İş bitince şunu çalıştırıp deneyeceğim: `npm test`. Olur mu?" diye sorar. Enter'a basarsan ölçüt ve bekçi birlikte açılır. Sen onaylamadan hiçbir komut çalışmaz. Test bulamazsa bir cümle seçersin ("Şu adres açılsın" gibi) ya da "Kanıtsız devam et" dersin.
4. **Ctrl+Enter.** İş başlar. Sonraki işlerde klasör ve onay hazırdır: kısayol (Ctrl+Alt+J), yaz, Ctrl+Enter, yani 3 hareket.

Ajan olarak Claude Code kullanılır: Söz, bu bilgisayarda kurulu `claude` komutunu ve senin kendi girişini (aboneliğini) kullanır, ücretli API anahtarı gerekmez. `claude` kurulu değilse ya da giriş yapılmamışsa iş kartı bunu sade bir cümleyle söyler.

Klavye: `n` yeni iş, `j`/`k` listede gezin, `Enter` aç, `r` ajana cevap yaz ya da durmuş işe yeni bir şey ekle, `s` durdurma sorusunu açar (hemen durdurmaz, "Evet, durdur" ya da "Vazgeç" diye sorar), `e` ya da `h` bekçi sorusuna Evet ya da Hayır (yalnız izin kartı odaktayken çalışır), `Ctrl+K` komut paleti, `?` kısayol listesi, `Esc` geri.

## Nasıl açılır
Yayın klasörü kendi .NET çalışma zamanını taşır. Ayrıca bir şey kurman gerekmez.

- Masaüstü: `<yayın klasörü>\Soz-Masaustu.exe` dosyasına çift tıkla. Ekranın kenarında küçük bir kapsül belirir, tepsiye simge konur. Kapsüle tıklayınca panel açılır. `Ctrl+Alt+J` paneli açar ve "Ne yaptırmak istiyorsun?" kutusuna odaklanır. 0.3.5'ten beri: tepsi simgesinin menüsünde "Güncellemeleri denetle" durur, kapsülü gizlediysen "Küçük pencereyi göster" satırı da çıkar. Panelde sağ tık yalnız kopyalama ve yapıştırma işlerini sunar (tam tarayıcı menüsü yok). Esc yalnız arayüzdeki açık pencereyi ya da alt sayfayı kapatır, paneli gizlemez. Bu dördü kodla ve birim testle doğrulandı, gerçek pencerede tek tek denenmedi.
- Yalnız tarayıcı: `<yayın klasörü>\soz.exe serve` komutunu çalıştır ve ekrana yazılan adresi aç (genelde `http://127.0.0.1:49300`). Port doluysa sonraki boş port seçilir.

Komut satırı aracının adı `soz.exe`, masaüstü kabuğunun adı `Soz-Masaustu.exe`. İkisi aynı klasörde durur, aynı veriyi ve aynı arayüzü kullanır.

## Kurulum ve güncelleme
Söz iki şekilde çalışır. Birincisi yukarıdaki gibi yayın klasöründen (geliştirme). İkincisi kurulu sürümdür: `Soz-Kur.exe` ile kurulur, `%LOCALAPPDATA%\Soz` klasörüne yerleşir, masaüstüne ve başlat menüsüne kısayol koyar. Paket imzasızdır, Windows SmartScreen uyarı gösterebilir.

**Programın kendi güncellemesi.** Kurulu Söz, güncelleme adresini koda yazılı bulur. Açılıştan 20 saniye sonra bir kez, sonra 6 saatte bir sessizce bakar. Tepsi menüsündeki "Güncellemeleri denetle" satırı elle bakar. Yeni sürüm varsa "Yeni sürüm var: x. Şimdi kurulsun mu?" diye sorar, kurma her zaman senin onayınla olur. Çalışan iş varsa soru bunu söyler ("Çalışan N iş durdurulacak"). Adres sırası: ortam değişkeni `SOZ_UPDATE_URL`, sonra `veri\shell-tercihleri.json` içindeki `guncellemeAdresi`, sonra koda yazılı adres. Denetimi kapatmak için aynı dosyaya `"guncellemeDenetimi": false` yaz. Kurulumla gelmeyen kopya (yayın klasörü) ağa hiç çıkmaz. Bu yol kodla ve birim testle doğrulandı, gerçek bir sürüm geçişinde uçtan uca denenmedi.

**Önemli: verin kurulum klasörünün içindedir.** Kurulu sürümde veri `%LOCALAPPDATA%\Soz\veri` klasöründe durur (iş kayıtları, deneme sonuçları, ayarlar). Bu yüzden:
- Kurulu sürümün üstüne `Soz-Kur.exe` çalıştırma. Kurulum kökünü ve içindeki veriyi silebilir (bu yol denenmedi, tehlike olduğu için kaçınıldı).
- Yükseltmek için önce Söz'ü kapat (tepsiden "Çıkış"). Sonra bütün `%LOCALAPPDATA%\Soz` klasörünün yedeğini al. Sonra `%LOCALAPPDATA%\Soz\Update.exe apply -p "<yeni sürümün Soz-<sürüm>-full.nupkg dosyası>"` komutunu çalıştır. Ya da uygulamanın kendi güncelleme denetimini kullan.

#### Geri dönüş (eski sürüme dönmek)
Velopack yalnız daha yeni sürümü teklif eder, kodda sürüm düşürme yolu yoktur. Kanıtlı yol yedekle değiştirmektir:
1. Tepsiden "Söz'ü kapat". Görev Yöneticisi'nde `Soz-Masaustu.exe` ve `Soz-Kapsul.exe` kalmadığını gör.
2. Şimdiki `%LOCALAPPDATA%\Soz` klasörünün tamamını kopyala (veri bunun içinde, `veri\`).
3. `%LOCALAPPDATA%\Soz` klasörünü önceki sürümün tam yedeğiyle değiştir.
4. Yeni sürüm veriyi yeni şemaya taşıdıysa eski sürüm onu açmaz ve "Bu kayıtlar Söz'ün daha yeni bir sürümüyle yazılmış" der. O zaman veri de geri konur: `veri\soz.db`, `soz.db-wal`, `soz.db-shm` kenara alınır, `veri\yedek\once-v<n>-<tarih>-<rastgele>.db` dosyası `veri\soz.db` adıyla kopyalanır. Göçten sonra yazılan kayıtlar bu yolla kaybolur.
5. Yeni sürüm yeniden önerilmesin istenirse `veri\shell-tercihleri.json` içine `"guncellemeDenetimi": false` yaz.
Eski paketle `Update.exe apply -p` ile sürüm düşürmek DENENMEDİ, yedek alınmış bir denemede bir kez sınanmalı. Eski `Soz-Kur.exe`'yi kurulu sürümün üstüne çalıştırma, veriyi silebilir.
- Kaldırırsan Söz'ün kaldırma adımındaki `VeriKorumasi` verini `%LOCALAPPDATA%\Soz-veri` klasörüne alır.

### Kurulum paketi
Kurulu sürüm Velopack ile paketlenir (`vpk pack`). Paketleme betikleri bu depoda yoktur; yalnız yayın komutları aşağıdaki "Geliştirme komutları" bölümündedir. Paket imzasızdır.

## Ayrıntılar

### Terminalden de çalışmak istersen
Terminalde kendin `claude` açacaksan Söz kancalarını o klasöre kurmalısın. Arayüzde Ayarlar sayfasındaki "Bu klasörde terminalden de çalışacağım" düğmesi bunu tek tıkla yapar. Komut satırından:
```
"<yayın klasörü>\soz.exe" hooks install --settings "<proje>\.claude\settings.json"
```
Bu yalnız verdiğin dosyaya yazar, var olan ayarlarını korur ve önce `settings.json.bak` yedeği alır. İçine beş giriş eklenir: oturum başında defter paketi (SessionStart), istemi kaydetme ve işi oturuma bağlama (UserPromptSubmit), ajan durunca kanıt kapısı ve kuyruk (Stop), ajan seni beklerken işaret (Notification), araç çağrısından önce bekçi (PreToolUse). Kaldırmak için `install` yerine `uninstall` yaz. Söz'ün kendi başlattığı işlerde bu kurulum gerekmez: kancalar projeye yazılmaz, Söz kendi veri klasöründeki ayar dosyasını ajana verir.

### Öteki sayfalar
Ana yüzey iş ekranıdır: solda iş listesi (Sıra bende, Çalışıyor, Kanıtıyla bitti, Kaldı, Kanıtsız), sağda seçili işin akışı. Öbür sayfalar soldaki ince şeritten ya da `Ctrl+K` paletinden açılır.

**İş listesi ve akış.** Söz'ün senden istediği (onay, cevap) her zaman en üstte "Sıra bende" grubundadır. Akışta senin sözün, ajanın sade satırları, kanıt kartı ve cevap kutusu durur. Teknik ayrıntı (komutun kendisi) satırın yanındaki "ayrıntı" ile açılır. İşin ölçütleri ve ajana giden metin akışın altında katlı durur. Adres çubuğuna olmayan bir iş numarası yazarsan (`#/is/987654` gibi) Söz "Bu işi bulamadım. Silinmiş olabilir ya da adres yanlış. İşler listesinden devam edebilirsin." der ve başka bir işi açmaz. Listede görünmeyen eski bir işin adresi doğru işi açar.

**Kanıt kapısı.** Ajan durduğunda yalnız AÇIK ölçütleri, kendi süre bütçesiyle, proje klasöründe çalıştırır. Hepsi geçerse iş "Kanıtıyla bitti", biri kalırsa ajan kalan ölçütün adı ve çıktısıyla geri yollanır (en çok 3 tur). Ajanın süreci hiç kapıdan geçmeden biterse Söz kapıyı kendisi çalıştırır. Ölçüt yoksa ya da sonuç belirsizse "Kanıtsız" yazar, asla "geçti" yazmaz. Ajanın süreci soru sorarak biterse kapı koşmaz, iş "Cevap bekliyor" olur ve sorunun metni iş ekranında görünür. Cevabını yazınca aynı konuşma kaldığı yerden sürer.

**Kanıt tabanı.** İş başlarken Söz projenin test tanımını (ör. `package.json` içindeki `scripts`, `*.csproj`, `pytest.ini`) ve test dosyalarını (`*.test.*`, `test/` altı vb.) parmak iziyle kaydeder. Ajan işi bitirince testler geçse bile Söz tabanla karşılaştırır. Ajan test komutunu boşalttıysa ya da var olan bir test dosyasını değiştirdiyse ya da test sayısı öncekinden düştüyse kart "Kanıt sayılmadı" der ve hüküm yeşil olmaz. Ajan bir kez geri yollanır ("eski hâline getir ya da nedenini yaz"). Israr ederse iş "Kararını bekliyor" olur: "Değişikliği kabul et" dersen iş "Sen onayladın" diye biter (yeşil "Kanıtıyla bitti" değil), "Kabul etme" dersen "Kaldı" olur. Yalnız yeni test dosyası eklenmesi hükmü etkilemez, karta bilgi notu düşer. İş "geçti" dedikten sonra aynı süreçte kod yeniden bozulursa son kapı sonucu esastır ve iş "Kaldı" olur. Ölçütler iş verildikten sonra senin elinle değiştiyse hüküm "Ölçüt değişti" olur ve kanıt sayılmaz.

**Ölçüt ekleme.** Akıştaki "Bu klasörün ölçütleri" bölümünde "Öner" projede test ve derleme komutu arar (yalnız dosyalar okunur, komut çalışmaz). Öneriler KAPALI gelir, doğru olanı sen açarsın. Bir kontrolü silersen onay sorulmaz, ama birkaç saniye "Geri al" düğmesi görünür ve silineni geri getirir. Elle eklemek için cümle seç: "Testler geçsin", "Şu adres açılsın", "Sayfada şu yazı görünsün", "Şu dosya oluşsun", "Dosyada şu yazı geçsin", "Sayfanın ekran görüntüsünü al".

**Bekçi.** Kurallı ve modelsizdir. Proje dışına yazma, korunan yollar, yasak komutlar ve önce sorulacak komutlar için kurallar tutar. İlk klasör onayında "temkinli başlangıç" şablonu da açılır. Bekçi sayfasındaki "Dene" kutusu bir komut için ne karar vereceğini gösterir. Her durdurma kaydedilir. Bir kuralı silince de birkaç saniye "Geri al" çıkar. Koruma anahtarı (Durdur ya da Sor) kalıcıdır, kaydedilemezse "Olmadı, geri alındı" der. Koruma olayları sade yazılır, örneğin "«rm -rf build» komutuyla klasörü içindekilerle birlikte silmeye çalıştı. Devam edilsin mi?". Kural listesindeki açıklamalar hâlâ kalıp metin gösterebilir. Projenin içindeki `node_modules`, `bin`, `obj`, `dist`, `.next`, `target`, `__pycache__` silinirken sorulmaz.

**Defter.** Konuşmalarından çıkan karar, kural ve yasak önerilerini toplar. Sen onaylamadan hiçbir şey kural olmaz. Onaylıların her yeni oturumun başında kısa bir paket olarak verilir. Aynı şeyi yeniden söylediğinde seni uyarır.

**Ara.** "Ne demiştim" araması. Geçmiş cümlelerini bulur, sır içeren parçaları gizler. Eski Claude ve Codex kayıtlarını isteğe bağlı içe alabilirsin (ilk açılıştaki bağlantı ya da Ayarlar'daki "Şimdi içe al", komut satırında `soz.exe import`). Kayıtların okunur, hiçbiri değiştirilmez. İlk iş içe almayı beklemez.

**Harcama.** Hangi gün, hangi model ve alt ajanların ne kadar token kullandığını gösterir. Fiyatları Ayarlar'da model başına sen girersin. Girmediğin modelde "fiyat girilmedi" yazar. Tutar API eşdeğeri tahmindir, abonelikte gerçek fatura değildir.

**Hakem (isteğe bağlı).** Ayarlar'daki anahtar açılırsa ajanın son sözünü sınıflamak için kısa bir soru Claude'a gider ve aboneliğin kullanılır. Kapalıyken yalnız kurallar çalışır (varsayılan kapalı).

**Dikte.** Masaüstü kabuğunda iş kutusunun yanındaki "Söyle" düğmesi, Ayarlar'da yolu verilen dikte programını açıp kapatır. Dikte odaktaki kutuya yazar.

**Aynı anda çalışan iş ve sıra.** Varsayılan en çok 3 iş aynı anda çalışır. Ayarlar'daki "Aynı anda en çok kaç iş" 1 ile 6 arası değişir. Sınır doluysa yeni iş "Sırada" görünür ve bir yuva boşalınca kendiliğinden başlar. Bir iş en çok 90 dakika sürer.

**Onaylar.** Ajan birden çok onay isterse onaylar tek tek ve en eskiden başlayarak sorulur ("2 onaydan 1"). Hepsi cevaplanınca TEK süreç başlar. Onay beklerken "Durdur" dersen bekleyen onayların hepsi reddedilir ve iş "Durduruldu" olur.

**Aynı işe yeni bir şey ekle.** Biten ya da durdurulan işte "Aynı işe yeni bir şey ekle" düğmesi (kısayol `r`) aynı konuşmayı yeni tur olarak açar, yardımcı önceki yaptıklarını hatırlar. Basınca "Yardımcıya başka ne yaptırmak istiyorsun?" kutusu açılır, yazıp "Devam et" ile gönderirsin (dar ekranda yalnız gönder simgesi görünür). Eski sürümlerde bu düğmenin adı "Bu işe devam et" idi. Yardımcıyla hiç konuşmamış işte devam edilemez.

**Söz kapandığı için yarıda kalan iş.** Söz kapanırken çalışan bir iş varsa kapatırken sorar ve bu soruda da aynı düğme adı geçer. Söz yeniden açılınca kapanmadan önce yarım kalan işte "Söz kapandığı için bu iş yarıda kaldı. Sen durdurmadın." kartı görünür ("Yardımcının o ana kadar yaptıkları duruyor. Sürdürmek için "Aynı işe yeni bir şey ekle" düğmesine bas."). İş listesinde satırı "Söz kapandığı için iş yarıda kaldı." der.

**Yarıda kaldı.** Yardımcı çalışırken bir hata verip işi bırakırsa iş "Başlatılamadı" değil "Yarıda kaldı" grubuna girir. Kart "Yardımcı çalışırken bir hata verdi ve işi yarıda bıraktı. İş kaybolmadı. Sorunu giderince yeniden dene." der, tek düğmesi "Yeniden dene"dir. Ham hata metni "Teknik ayrıntı" düğmesinin altındadır, ekranda görünmez.

**Başlatılamadı.** `claude` bulunamazsa, giriş yapılmamışsa ya da süreç hemen çökerse iş "Kaldı" değil "Başlatılamadı" olur ve nedenini sade cümleyle söyler. Giriş yapılmamışsa cümle kartta bir kez görünür ("Claude Code uygulamasını bir kez aç ve hesabınla giriş yap, sonra işi yeniden başlat") ve "Ayarları aç" düğmesi yanında durur. Ayarlar'daki "Claude programının yeri" alanından yol verebilirsin (`.cmd` kısayolu kabuk üzerinden çalıştırılmaz, içindeki `.exe` ya da node dosyası bulunur).

**Eklenemeyen klasörler.** Sürücü kökü, Windows ve Program Files klasörleri, ProgramData, kullanıcı klasörünün kendisi, ağ (UNC) yolu, göreli yol ve `.claude`/`.codex` klasörleri proje olarak eklenemez. Arayüzde kutunun altında sade cümleyle söylenir. Bir dosyanın yolunu yazarsan "Bu bir dosya, klasör değil. Klasörün yolunu yaz." der, olmayan klasörde "Bu klasör bulunamadı: ..." der. Kullanıcının kendi `.claude` klasörüne kanca kurulumu da her koşulda reddedilir.

**Üst sayaçlar.** "Çalışıyor" yalnız gerçekten canlı süreçleri sayar. "Seni bekleyen" cevap, karar ve onay bekleyen işleri ile görülmemiş "Kaldı" işleri sayar. Biten iş sabah raporunda "bekliyor" görünmez. İşler sayfası en yeni 50 işi yükler. 50'den fazla iş varsa "Seni bekleyenler" sayacı "50+" yazar (sunucu durum başına toplam vermediği için tam sayı henüz yok), listenin sonunda "En yeni 50 iş gösteriliyor, toplam 60 iş var." gibi bir not ve "Daha fazla göster" düğmesi çıkar. Hepsi yüklenince sayaç kesin sayıyı gösterir. "Verdiğim İşler" sekmesindeki sayı sunucunun toplamıdır.

**Sabah özeti.** Sayfadaki "Aralık" seçimi (Son 16 saat, Son 24 saat gibi) saklanır. Bir işin bağlantısına basıp Geri dönünce ya da sayfayı yenileyince seçtiğin aralık korunur.

**İlerleme satırları.** Yollar dosya adına iner, komutlar "testleri çalıştırıyor" gibi kategoriyle yazılır. Ham komut yalnız ayrıntıda görünür ve parola benzeri değerler maskelidir.

## Ajan araçları
Söz işi Claude Code ile ya da Codex ile çalıştırabilir. Araç bir "bağlayıcı" arkasındadır: Söz aracı bulur, sürümünü okur, başlatır, olay akışını sade Türkçe satırlara çevirir, durdurur, aynı oturuma geri döner ve harcamayı okur. Aracın kendi ayrıntısı yalnız kendi bağlayıcısında durur.

**Hangi araç hazır.** Ayarlar'daki "Ajan araçları" bölümü bulunan araçları, sürümlerini, yollarını ve neleri yapabildiklerini gösterir. Komut satırında `soz.exe agents` (ve `--json`) aynı bilgiyi verir. Araç bulunamazsa nedeni sade cümleyle yazar. Yol verebilirsin: Claude için "Claude programının yeri", Codex için "Codex programının yeri". `.cmd` kısayolları kabuk üzerinden çalıştırılmaz. Söz kısayolun içindeki `.exe` ya da node dosyasını bulur ve kısayolun `set "AD=değer"` satırlarıyla verdiği ortam değişkenlerini aracı başlatırken kendisi verir. Böylece bir kısayolun taşıdığı ayar kaybolmaz.

**Seçim.** Birden çok araç bulunduysa yeni iş kutusunda klasör düğmesinin yanında ajan düğmesi belirir. Tek araç varsa kutu eskisi gibi sade kalır. Ayarlar'daki "Yeni işte varsayılan ajan", seçmediğinde çalışacak aracı belirler. İş listesinde her satırda küçük bir ajan adı görünür. Komut satırında: `soz.exe job new --agent codex` ve `soz.exe job start <no> --agent codex`. Bir iş ajanla konuşmaya başladıktan sonra aracı değişmez, çünkü oturum o araca aittir.

**Neyi aynı bırakır.** Başlangıç ölçümü ("Başlamadan önce 2 testten 1'i geçmiyor"), kanıt tabanı, kanıt kartı, hile tespiti ve kabul akışı iki araçta da aynıdır. Kabul yalnız senden gelir.

**Bekçi ve kanıt kapısı Codex'te nasıl.** Claude Code'un araçtan önce çalışan yerli kancası ve ajan durunca çalışan kancası vardır. Codex'te kullanıcının kendi ayar klasörüne yazmadan bunu kurmanın yolu yoktur ve Söz Codex'in ayar klasörüne HİÇBİR ŞEY yazmaz. Bu yüzden:
- Bekçi Codex'te "sınırlı" kademesindedir. Komut komut izin sorulmaz. Codex `workspace-write` kumlama kipinde ve `approval_policy="never"` ile çalışır: yazma proje klasörüyle sınırlıdır ve bu sınırı aracın kendi işletim sistemi kumlaması uygular. İş başlığında "Bekçi: sınırlı (Codex)" etiketi görünür.
- Kanıt kapısı "dış döngü" ile çalışır. Codex bir turu bitirince Söz ölçütleri kendisi koşar. Kalırsa kalan ölçütün adı ve çıktısıyla aynı oturuma `exec resume` ile geri yollar. En çok 3 kapı turu (yani en çok 2 geri yollama), sonra iş "Kaldı" olur. Test dosyası değiştiyse ya da test sayısı düştüyse bir kez geri yollanır, ısrar ederse iş "Kararını bekliyor" olur. Claude'daki kanca mantığıyla aynıdır.
- Codex soru sorarak biterse iş "Cevap bekliyor" olur ve cevabın aynı oturuma `exec resume` ile gider. Kapı o turda koşmaz.

**Harcama.** Claude'un sonuç olayındaki dolar tutarı koşuya ve işe yazılır. Codex dolar vermez, token sayıları (girdi, önbellekten okunan, çıktı, akıl yürütme) yazılır. İkisi de kendi birimiyle tutulur, birbirine çevrilmez.

**Ortam.** Her aracın faturalı anahtarları ayrı ayrı süzülür: Claude başlatılırken `ANTHROPIC_*`, Codex başlatılırken `OPENAI_*`, `AZURE_OPENAI_*` ve `CODEX_API_KEY` çocuk sürece verilmez. Böylece iş, aboneliğin yerine kazara anahtarla çalışmaz. Kullanıcının `CODEX_HOME` değişkenine Söz dokunmaz.

**Gerçek Codex ile görülenler (sürüm 0.167.0, kısa duman denemesi).**
- Olay biçimi doğrulandı. İlk satır `thread.started` (oturum kimliği), sonra `turn.started`, ajanın sözü `item.completed` içinde, tur sonu `turn.completed` ve harcama sayıları. Komut için önce `item.started` sonra `item.completed` gelir. Söz bu çifti tek satıra çevirir.
- `exec resume` aynı kimlikle `thread.started` olayını yeniden basar. Söz ilk kimliği tutar, yeni oturum açmaz.
- Codex dosya yazarken ayrı bir `file_change` olayı vermeyebilir (PowerShell komutuyla yazıyor). Bu yüzden dosya değişikliği olaydan değil diskten ölçülür.
- Çıktıya JSON olmayan günlük satırları (`ERROR codex_core::...`) karışır. Söz bunları yutar, akışa ve ekrana sızdırmaz. Bir koşu hata ile biterse en son böyle bir satır nedenin yanına yazılır.
- Tur sonu olayı gelmeden süreç kapanırsa koşu hata sayılır ve nedeni yazılır. İş asılı kalmaz ve yeşil olmaz.

**Windows kum havuzu engeli.** Bu bilgisayarda Codex ayarı `[windows] sandbox = "elevated"` idi. Başsız çalışırken bu kip her komutu `orchestrator_helper_launch_canceled ... 1223` hatasıyla düşürüyor, Codex ise yine de "yazdım" diyebiliyor, dosya yazılmıyor. Söz bu yüzden Codex'i her başlatmada ve her `exec resume` çağrısında `-c windows.sandbox="unelevated"` ile çalıştırır. Ayar dosyasına hiçbir şey yazılmaz, değer her çağrıda komut satırıyla verilir. Gerçek Codex ile bu bayrakla dosya yazıldığı görüldü.
- Yine de hiçbir komut koşamazsa ve dosya da değişmediyse iş "Başlatılamadı" olur ve sade neden yazılır: "Codex bu bilgisayarda komut çalıştıramadı (Windows kum havuzu kurulumu eksik)". Zaman çizgisinde ilgili komut satırı "komut çalıştırılamadı" diye görünür.
- "Yazdım" deyip dosyayı yazmayan ajan yeşil olmaz. Ölçüt dosyanın varlığıysa kapı üç turda "Kaldı" der.
- Kum havuzunun ne kadar sıkı olduğu Windows'ta hâlâ tam doğrulanmadı. `unelevated` kipi yönetici kurulumunu gerektirmez, ama `elevated` kadar sıkı olmayabilir. Yönetici kurulumu ayrı bir karardır ve Söz kendiliğinden yapmaz.

**Sınırlar.** Bağlayıcı gerçek Codex ile yalnız kısa bir duman denemesinde görüldü, tam testler sahte bir `codex` betiğiyle (`test` klasöründe `sahte-codex`, gerçek olay biçimlerine göre) koşar. Sahte betik gerçek Codex değildir. Hermes, OpenCode, Antigravity ve Agent Client Protocol üzerinden ajanlar için yeni bağlayıcı yazmak gerekir: arayüz hazırdır, bu bağlayıcılar yoktur.

## Telefon
Varsayılan olarak KAPALIDIR. Ayarlar'da açınca bilgisayar aynı Wi-Fi'deki telefonun erişebileceği ikinci bir port açar. Ayarlar'daki QR kodunu telefonla okutursun, telefon bir eşleştirme çerezi alır.

Telefondan yalnız iki şey yapılır: seni bekleyen ajanları ve sabah özetini görmek, ve kuyruğa mesaj bırakmak. Bıraktığın mesaj bilgisayarda Şu An sayfasında "Yardımcıya bıraktığın mesajlar" başlığı altında görünür (örnek satır: "Çiçek Dükkanı, mesajın metni, Telefondan · az önce" ve "Yardımcı durunca alacak."). Bilgisayardan bırakılan mesajın yazarı "Bilgisayardan" olur. Bekleyen mesaj yoksa bölüm hiç görünmez ve mesajlar "Seni bekleyenler" sayısını değiştirmez. Mesaj yardımcıya ulaşınca bölümün kalkması tarayıcıda denenmedi. Ayarlar, defter, arama, içe alma, kanıt dosyaları ve canlı olay akışı yerel ağdan ERİŞİLEMEZ. Telefona giden metinler sır süzgecinden geçer ve en çok 200 karakterdir.

Riskler:
- Aynı ağdaki herkes bu portu görebilir. Kodu bilmeyen içeri giremez, ama ortak ve güvenmediğin ağlarda açma.
- Bağlantı şifresizdir (http). Aynı ağda trafiği dinleyen biri telefon ekranındaki kısa metinleri görebilir.
- AÇIK RİSK: bağlantı şifresiz http olduğu için aynı ağı dinleyen biri oturum çerezini ele geçirebilir ve çerezle ajana kuyruk mesajı gönderebilir (ajan o mesajı çalıştırabilir). Telefonu yalnız güvendiğin ev ağında aç. Ortak, otel, kafe ya da iş ağında açma. Şüphelenirsen "kodu yenile" de.
- Telefondan gelen kuyruk mesajı ajana iletilirken başına kaynağı yazılır ("Telefondan gelen mesaj: ..."). Böylece ajan ve sen mesajın telefondan geldiğini görürsün.
- QR kodunu ya da adresi kimseyle paylaşma. Şüphelenirsen Ayarlar'da "kodu yenile" de. Eski telefonların bağlantısı hemen düşer.
- İlk açılışta Windows güvenlik duvarı izin sorabilir. İzin vermezsen telefon bağlanamaz, bilgisayardaki kullanım etkilenmez.

## İki ilke: varsayılan kuşku, kabul yalnız senden
**Varsayılan kuşku.** Söz iş başlamadan önce projenin bütün dosyalarının anlık görüntüsünü alır, iş bitince yeniden alır. Sayılan dosyaların çoğu zararsızdır (kaynak kodu, metin, resim): yalnız sayılır, içeriğine bakılmaz. Bilinmeyen ya da sınamayı etkileyebilecek değişiklik kuşkulu sayılır: test dosyası (adı gerçekten test olan: `test/`, `*.test.*`, `*_test.*`, `test_*`, `*Test.cs`), test komutunun çalıştırdığı betik, sınama ayarı (`package.json`, `.npmrc`, `pytest.ini`, `tsconfig*.json`, `jest.*`, `vitest.*`), çalıştırıcının kendiliğinden yüklediği adlar (`conftest.py`, `sitecustomize.py`, kökteki `pytest.py`, `mocha.js` gibi araç adını gölgeleyen dosyalar) ve tanınmayan türler. Şunlar kuşku doğurmaz: kaynak kodu, metin, resim, yazı tipi, stil, dil dosyaları, `.gitignore`, `README`, kullanılmayan `package.json` betikleri, sürüm ve açıklama alanları, kilit dosyasıyla birlikte bağımlılık eklemek. Kilit dosyaları ve `eslint`, `prettier`, `.github/workflows` gibi sınamayı etkilemeyen yapılandırmalar kartta bilgi notu olarak görünür. `package.json` alan alan karşılaştırılır. Kuşkulu bir değişiklik varsa iş "geçti" olmaz, "senin kararını bekliyor" olur ve kartta neden, değişen dosya ve en çok 12 satırlık önce/sonra farkı yazar. Proje 50 bin dosyayı aşarsa Söz her şeyi izleyemez, bunu açıkça söyler ve işi yine "geçti" yapmaz.

**Bağımlılık klasörleri.** `node_modules`, `.venv`, `venv` ve `vendor` dosya dosya taranmaz. Her üst düzey paket için dosya sayısı, toplam boyut ve en son değişme zamanından bir parmak izi alınır (2.000 paketli sahte klasörde ilk tarama 355 ms, ikinci tarama 61 ms ölçüldü). Kilit dosyası değişmeden bir paketin içeriği değişirse kart "bağımlılık klasörü elle değiştirildi" der ve iş senin kararına kalır. Kilit dosyası da değiştiyse (ör. `npm install`) yalnız bilgi notu olur. `dist` ve `build` yalnız ölçüt ya da test oradan yükleme yapıyorsa ve kendisi derlemiyorsa izlenir.

Ajan başlamadan önce Söz testleri bir kez kendisi çalıştırır ve sonucu karta yazar: "Başlamadan önce 2 testten 1'i geçmiyor. Şimdi 2'si de geçiyor." Böylece "geçti" cümlesi neyle karşılaştırıldığını gösterir. `baselineRun` ayarıyla kapatılabilir.

**Kabul ve ayar yalnız senden.** "Bu sonucu kabul ediyorum" ve ayar değiştirme isteği yalnız senin elinle yapılabilir. Söz isteği yapan sürece bakar: Söz'ün kendi başlattığı ajan işinin içindeyse ya da ata zincirinde bir `claude` süreci varsa istek reddedilir (403, "Bu işlem yalnız senin elinle yapılır."). Ajan `soz job accept` ya da `curl` ile kendi işini kabul edemez. Ajanla ilgisi olmayan süreçler (senin terminalin, panel, tarayıcın) normal çalışır. Kabul, ekranda gördüğün kartı onaylar: kart klasörün görüntüsünün kimliğini taşır, kabul isteği bu kimliği geri yollar. Klasör kart çizildikten sonra değiştiyse Söz kabulü reddeder (409), yeni karşılaştırmayı yapar ve yeni kartı gösterir. Kabul panelden değil de komut satırından ya da başka bir programdan geldiyse kart sarı renkte "Bu kabul panelden gelmedi" der ve kabulü yapan programın adını yazar. Kabul edilen iş "Sen onayladın" diye ayrı sayılır, sabah raporunda ve durum sayılarında "Kanıtıyla bitti" ile karışmaz ve kabulün hangi yoldan geldiği (arayüz ya da komut satırı) kayıtlıdır.

## Tehdit modeli ve sınırlar
Söz bir kasa değil, bir özen katmanıdır. Amaç, dürüst çalışan ama dalgın ya da hatalı bir ajanın "bitti" demesini ve senin bunu fark etmeni sağlamaktır. Aynı Windows hesabında çalışan, kararlı ve kötü niyetli bir ajana karşı tam bir engel yoktur ve olduğunu söylemiyoruz:
- Ajan, Windows yönetim araçları (WMI, zamanlanmış görev) üzerinden kendi sürecini Söz'ün ajan iş nesnesinin dışında başlatabilir. Bekçi bu yolları (`wmic process call create`, `Invoke-CimMethod`, `[wmiclass]`, `schtasks /create`, `Register-ScheduledTask`, `Start-Process`, COM ile süreç başlatma, `setx`, `npm config set`, `git config core.hooksPath`, `.git/hooks` ve kullanıcı profilindeki `.npmrc` ile `.gitconfig` yazımı) Söz'ün yürüttüğü işte reddeder ve olay kaydına yazar. Bu bir listedir. Değişkenle kurulan komut, başka bir yorumlayıcı ya da betik içinden yapılan çağrı listeye takılmaz. Denetimde bu yolla dışarı çıkan bir süreç Söz'ün kabul ve ayar uçlarını insan gibi çağırabiliyordu. Bu yüzden eski "ajan iş nesnesinden çıkamaz" iddiası geçersizdir.
- Ajan Söz'ün veritabanına (`verisoz.db`) doğrudan yazabilir. Hesap aynı olduğu için bunu tam engellemek mümkün değildir. Söz değişikliği olay kaydına yazar ve işin ölçütleri değiştiyse hükmü "belirsiz" yapar. Satırlara imza atmak denendi mi: hayır. Hüküm satırlarını kancalar ayrı süreçte yazdığı için sunucunun belleğinde tuttuğu bir anahtarla imzalamak pratik değil, kalıcı anahtar ise aynı hesapta okunabilir. Bu yüzden yapılmadı.
- Projenin kendi kodu (test dosyaları, betikler, bağımlılıklar) ajan kodu sayılır. Söz bu kodu çalıştırırken (başlangıç ölçümü, sunucu kapısı, kanıt komutları) kodu ajan iş nesnesinin içinde ve `SOZ_RUN_ID` ortamıyla çalıştırır, böylece koddan gelen kabul isteği de ajan sayılır ve reddedilir.
- Gerçek ayrım, ajanı ayrı bir Windows kimliğiyle (ayrı kullanıcı ya da kısıtlı jeton) çalıştırmaktır. Bu yol haritasındadır, bu sürümde yoktur.

## Bilinen sınırlar
Bekçinin kaçırabildikleri (dürüst liste):
- Komutun içinde değişken ya da genişletme varsa (`rm -rf $DIR`) hedef bilinemez. Bu komut engellenmez, olay da yazılmaz.
- Bir betiğin içinde yapılan iş görünmez. `npm run temizle` ya da `python temizle.py` içindeki silme bekçiye yalnız betik adı olarak gelir.
- `python -c`, `node -e` gibi satır içi kodla yazma ve silme yakalanmaz.
- Kısayol (alias), kabuk işlevi, kodlanmış PowerShell komutu ve sembolik bağlantı üzerinden yapılan yazma yakalanmaz.
- Komut zincirinde klasör değiştirme izlenir: `cd`, `cd -P`, `pushd/popd`, `Set-Location` (`sl`, `chdir`), `cd /d`, alt kabuk `( ... )`, `bash -c`, `cmd /c`, `powershell -Command`, `env -C`, `git -C`, `tar -C`, `npm --prefix` içindeki göreli yollar. Sınırları:
  - `cd $X` ya da `cd -` gibi hedefi bilinemeyen bir klasör değiştirmeden sonra gelen GÖRELİ yazma hedefi engellenmez, "Bekçi soruyor" diye sorulur. Mutlak hedefler normal değerlendirilir, sonraki mutlak bir `cd` durumu düzeltir. Bu hedeflerde korunan yol ve "önce sor" kalıpları uygulanamaz.
  - Boru (`|`) ve arka plan (`&`) içindeki `cd` gerçekte kabuğun klasörünü değiştirmez ama bekçi değiştirmiş sayar (yanlış alarm yönünde). `cd` başarısız olursa bunu bilemez, başarılı varsayar.
  - `cd` sonrası yolu görünmeyen komutların yan etkisi (`git clean`, `npm install`, `make`, `dotnet build`) görünmez. `make -C`, `npm run --prefix` ve `Start-Process -WorkingDirectory` ile başlayan sürecin içeriği görünmez. Yalnız `git -C`, `tar -C`, `npm --prefix` ile yazan alt komutların hedef klasörü yazma hedefi sayılır.
  - cmd'e özgü `cd\` ve `cd/` kısaltmaları izlenmez.
- Proje içinde dışarıyı gösteren bir sembolik bağlantı açmak (`ln -s ~/.ssh ./x`) bekçiden geçer, çünkü bağlantının hedefi yazma sayılmaz. Sonra bağlantı üzerinden yazılırsa yakalanmaz.
- Yalnız Write, Edit, NotebookEdit ve Bash/PowerShell komutlarına bakılır. Başka araçlar (ör. MCP araçları) denetlenmez.
- Aynı kullanıcı hesabında çalışan bir ajan Söz'ün ölçüt ve kapı ayarlarını doğrudan sqlite ile değiştirmeye çalışabilir. API ve komut satırında kabul ve ayar çağıran kimliğine göre reddedilir, ama bu hesap altında veritabanı dosyasına doğrudan yazmayı tam engellemek mümkün değildir. Hedef fark etmek ve göstermektir. Her ölçüt/kapı değişikliği olay kaydına yazılır, iş verildikten sonra ölçüt değiştiyse ya da kapandıysa hüküm "belirsiz" olur (asla "geçti" değil), "Temkinli" şablon Söz kayıtlarına ve `.claude/settings*.json` dosyalarına yazmayı engeller, ayar değiştiren komutları ve Söz API çağrılarını sorar. `python`/`node` ile yapılan doğrudan değişiklik yakalanmaz.
- "Önce sor" kararı Claude Code'un kendi izin penceresiyle sorulur. O pencereyi kapatan ayarlarda soru görünmeyebilir.

Diğer sınırlar:
- Telefondan duran bir ajana ANINDA cevap verilemez. Mesaj kuyruğa girer ve ajanın bir sonraki duruşunda iletilir. Uygulama kapalıyken telefona anlık bildirim gitmez, sayfa açıkken yenilenir.
- Codex için kanca yoktur. Codex kayıtları içe alınır ve harcamada görünür. Söz'ün başlattığı Codex işinde kanıt kapısı Söz'ün kendi dış döngüsüyle çalışır, bekçi ise yalnız işletim sistemi sınırıdır ("sınırlı", bkz. Ajan araçları). Terminalde kendi açtığın Codex oturumunu Söz denetlemez.
- Model kullanan özellikler (Sade anlat, model destekli hakem) varsayılan KAPALIDIR. Açarsan kendi Claude aboneliğindeki `claude -p` komutunu kullanır.
- Kanca bir kez bile çalışmazsa Söz bunu fark eder. Ayarlar'daki "Kancalar" bölümü hangi projede kurulu olduğunu, son 24 saatteki çalışma ve hatayı gösterir. Kanıt kapısı çalışmıyorsa iş ekranı bunu tek satırla söyler.
- Windows 10 ve 11 içindir. Panel için WebView2 gerekir (Windows 11'de hazır gelir). Yayın klasörü yaklaşık 150 MB'tır çünkü çalışma zamanını taşır.
- Sır süzgeci sezgiseldir. Alışılmadık biçimdeki bir sırrı kaçırabilir. Zararsız uzun bir dizgiyi de sır sayabilir (güvenli yönde yanılır).
- Kanca gecikmesi: her araç çağrısından önce bekçi kancası çalışır. Önceki turda bu yayın exe'siyle 10'ar denemede ölçülen süre ortalama 135 ms (en az 109, en çok 175) idi, hedeflenen 60 ms'ye inilemedi. Bu turda bütün dosya anlık görüntüsü eklendi: yalnız tarama 50 dosyada 1 ms, 2.000 dosyada yaklaşık 20 ms, 20.000 dosyada yaklaşık 235 ms sürer. Stop kancasının toplamı (projenin kendi testlerini çalıştırmak dahil) 50 dosyalı projede 730 ile 810 ms, 2.000 dosyalı projede 855 ile 953 ms ölçüldü, yani 2.000 dosyada anlık görüntünün eklediği fark yaklaşık 110 ms'dir. Ajan çok araç çağırıyorsa bu süre birikir: 100 araç çağrısı yaklaşık 13 saniye ekler. Denetim 4 turunda (bağımlılık parmak izi ve proje kodunun ajan iş nesnesinde çalışması sonrası) Stop kancasının toplamı 52 dosyalı projede 877 ile 911 ms, 2.000 dosyalı projede 988 ile 1.487 ms ölçüldü (test koşusu dahil, tek denemeler).
- Bellek: sunucu boşta 50 ile 65 MB arasında kullanır (yayın exe'si, işsiz, ilk 90 saniyede ölçüldü). Masaüstü kabuğunda panel açıkken tarayıcı bileşeni yüzünden toplam yaklaşık 480 MB kullanılır. Panel 5 dakika gizli kalırsa bu bileşen bırakılır ve yalnız kapsül kalır (yaklaşık 90 MB). Bu iki kabuk sayısı önceki turdan kalmadır, bu turda yeniden ölçülmedi.
- Test çıktısı tanınmayan bir araçtansa (Söz'ün bildiği biçimlerin dışında) kanıt kapısı yalnız komutun hatasız bittiğini görür. Kart "Test sayısını okuyamadım, yalnız komutun hatasız bittiğini gördüm" notunu taşır ve "geçti" olur. Test sayısı düştü ya da test atlandı denetimi bu durumda çalışmaz. `0 test` yazan tanınan çıktılar (mocha, go, cargo, dotnet, vitest, bun, TAP) "kanıtsız" sayılır.
- Kanıt tabanı artık bütün dosyalara bakar (varsayılan kuşku), ama bazı sınırlar kalır. Zararsız sayılan dosyalar (ör. kaynak kodu) yalnız sayılır, içeriği karşılaştırılmaz. Bu yüzden testin bağımlı olduğu bir kaynak dosyasını değiştirmek zaten amaçtır ve kuşku doğurmaz. Bu dar bir açıktır: kaynak koduna gömülü ve testin yüklediği bir yük Söz'ün koştuğu testte çalışır, ama artık ajan iş nesnesi içinde çalıştığı için kabul ya da ayar uçlarını çağıramaz. `.git` ve bağlantı (junction, sembolik bağlantı) klasörleri izlenmez. Bağımlılık klasörleri parmak izi ile izlenir (dosya içeriği karşılaştırılmaz, bkz. Bağımlılık klasörleri). Test ortamını Söz'ün hiç görmediği bir yerden (ör. makine geneli ortam değişkeni, proje dışındaki bir dosya) etkileyen ajan taban tarafından görülemez.
- Terminalden kendi başlattığın bir `claude` oturumundan Söz'ün API'sine gidilirse ata zinciri kontrolü onu ajan sayar. Ajan bir işlemi kendi sürecini yetim bırakarak (ata sürecini kapatarak) bu kontrolü aşmaya çalışırsa Söz'ün kendi başlattığı ajanlar normalde ajan işinin içinde kalır ve yakalanır (WMI ile çıkmadıkları sürece), ama senin terminalinden başlatılmış bir `claude` bu yedek kontrolü aşabilir. Söz'ün başlattığı işlerde bu yol daha dardır ama kapalı değildir: ajan WMI ile iş nesnesinden çıkabilir (bkz. Tehdit modeli ve sınırlar).
- Claude Code içinden başlatılan bir tarayıcı ya da betik de ajan sayılır ve kabul isteği yapamaz. Tanıma dardır: yalnız Claude Code'un kendi programı ya da cli betiği sayılır. Yol ya da argümanında yalnız "claude" geçen sıradan bir node betiği ve Claude Desktop uygulaması ajan sayılmaz. Reddedilen istek şu cümleyi alır: "Bu istek bir Claude oturumunun içinden geldi. Söz'ü Claude dışındaki bir pencereden aç." Panelin kendi tarayıcı bileşeni (WebView2) masaüstü kabuğunun alt süreci olduğundan ata zincirinde `claude` bulunmaz, kod okumasıyla ajan sayılmaz. Gerçek pencerede tıklayarak denenmedi.
- Söz'ün kendi başlattığı işlerde ajan olarak Claude Code ya da Codex kullanılır. Bekçi yalnız Claude Code'da komut komut çalışır. Codex'te yazma sınırı aracın kendi kumlamasındadır ve Windows'ta bunun ne kadar sıkı olduğu bu sürümde doğrulanmadı. Codex'in kullanıcı düzeyindeki kendi kancaları (varsa) `exec` sırasında çalışabilir, Söz bunu denetlemez.
- Dikte yalnız açıp kapama yapar: Söz dikte programını başlatır ya da kapatır, söylediğin metni kendisi duymaz ve işlemez. Metin odaktaki kutuya dikte programından gelir.
- Telefon özelliği gerçek bir telefonla denenmedi. Güvenlik sınaması (56 madde) bu bilgisayarın yerel ağ adresine karşı yapıldı.
- Bu sürümün yürütücüsü (ajanı Söz'ün başlatması) sahte bir `claude` betiğiyle ve uçtan uca denemelerle sınandı. Gerçek `claude` ile uçtan uca deneme ayrıca yapılmalıdır. Claude Code'un komut satırı bayrakları değişirse yürütücü başlatma hatası verir ve iş kartı bunu sade bir cümleyle söyler.
- Masaüstü kabuğunda klasör seçici penceresinin açılması ve Ctrl+Alt+J ile iş kutusuna odaklanma gerçek pencerede, tıklayarak ya da tuşa basarak denenmedi. Kabuk iletileri pencere açmayan öz sınamada (`--selftest`), arayüz tarafı ise tarayıcıda sınandı.
- Aynı kullanıcı hesabında çalışan ajan Söz'ün veritabanını doğrudan kurcalayabilir. Bunu tam engellemek bu hesap altında mümkün değildir. Söz değişikliği olay kaydına yazar ve işin ölçütleri değişmişse hükmü "belirsiz" yapar.
- 0.3.5 kabuk değişiklikleri (sağ tık menüsü, Esc, tepsi satırları, WebView2 çöküş koruması) gerçek pencerede tek tek denenmedi. Geri tuşu yalnız başsız Edge'de denendi. Programın kendini güncellemesi ilk kez 0.3.5'ten sonraki sürümde sınanacak.
- 24 Kas 2026 tıklama sınamasında şunlar denenmedi: kabuk küçük penceresi ve tepsi simgesi, gerçek ses (mikrofon), gerçek yardımcıyla uçtan uca iş, `.ps1` uçtan uca betikleri, `Soz-Kur.exe` ile sıfırdan kurulum, paket imzası. Ayrıca İngilizce arayüzde bazı cümleler Türkçe kalıyor.

## Veri nerede
Söz veri klasörünü şu sırayla seçer:
1. `--data <klasör>` verildiyse o.
2. `SOZ_DATA` ortam değişkeni doluysa o.
3. exe'nin yanında `veri\` klasörü VARSA o.
4. exe'nin bir üst klasöründe `veri\soz.db` VARSA o klasör. Bu yüzden yayın klasöründeki soz.exe, bir üst klasördeki veri klasörünü kendiliğinden bulur.
5. Hiçbiri yoksa exe'nin yanında yeni bir `veri\` klasörü açılır.

Kurulu sürümde (`Soz-Kur.exe` ile kurulan) veri `%LOCALAPPDATA%\Soz\veri` klasöründe durur (24 Kas 2026 yükseltmesinde 136 MB, yerinde kaldı). Bu klasör kurulum kökünün içindedir, bu yüzden kurulum kökünü silen bir işlem veriyi de siler.

Seçilen klasör ve nedeni `soz.exe doctor` çıktısında ve Ayarlar sayfasında yazar. Klasörde `soz.db` (veritabanı), `kanit\` (kanıt çıktıları ve ekran görüntüleri), `webview2\` (panelin tarayıcı profili), `soz-hook.log`, `soz-shell.log` ve `sunucu.json` durur. Hiçbir veri dışarı gönderilmez.

## Kaldırma
1. Kanca kurduğun her proje için: `"<yayın klasörü>\soz.exe" hooks uninstall --settings "<proje>\.claude\settings.json"`. Yalnız Söz'ün eklediği girdiler silinir.
2. Masaüstü kabuğunu tepsideki simgeden "Çıkış" ile kapat.
3. `<yayın klasörü>\` klasörünü sil.
4. Verini de silmek istersen veri klasörünü sil (yukarıdaki sıraya bak, `soz.exe doctor` gösterir).
Bu dört adım yayın klasöründen çalıştırılan Söz içindir: o durumda Söz kayıt defterine (registry) yazmaz, başlangıçta kendiliğinden açılmaz, başka yere dosya bırakmaz.

Kurulu sürümü (`Soz-Kur.exe` ile kurulan, `%LOCALAPPDATA%\Soz`) kaldırırken Söz'ün kendi kaldırma adımı çalışır. Kısayollar kalkar. `VeriKorumasi` adımı verini `%LOCALAPPDATA%\Soz-veri` klasörüne alır. Kaldırma kurulu sürümde denenmedi, yalnız yükseltme denendi ("Kurulum ve güncelleme" bölümüne bak).

## Geliştirme komutları
Bu proje .NET 10 SDK ister (`global.json`). Yolları boşluk içeriyorsa tırnakla yaz. Hata ayıklama (Debug) exe'leri `DOTNET_ROOT` ister, yayın exe'leri istemez.

```
# arayüz (çıktı src\Soz.Server\wwwroot\ içine gider)
cd ui
npm install
npm run check
npm run build

# derleme ve birim testleri
dotnet build Soz.sln
dotnet test Soz.sln

# yayın (önce arayüzü derle). SIRA ÖNEMLİ: önce Shell, sonra Cli
# Ters sırada Shell yayını soz.exe, soz.dll ve soz.runtimeconfig.json dosyalarını çerçeveye bağımlı kopyayla ezer, soz.exe açılmaz
dotnet publish src\Soz.Shell -c Release -r win-x64 --self-contained true -o <yayın klasörü>
dotnet publish src\Soz.Cli -c Release -r win-x64 --self-contained true -o <yayın klasörü>

# uçtan uca (sentetik veri). -Command ile değil -File ile çalıştır
powershell -NoProfile -File test\e2e.ps1 -Exe "<yayın klasörü>\soz.exe"
powershell -NoProfile -File test\e2e-kanit.ps1 -Exe "<yayın klasörü>\soz.exe"
# ajanı Söz'ün başlattığı akış (sahte claude ile). KENDİ terminalinden çalıştır: Claude Code içinden başlatılırsa kabul ve ayar istekleri 403 alır
powershell -NoProfile -File test\e2e-yurutucu.ps1 -Exe "<yayın klasörü>\soz.exe"

# Codex bağlayıcısı, sahte codex ile (kendini insan eşdeğeri süreçte yeniden başlatır, gerçek codex çağrılmaz)
node test\e2e-codex.mjs "<yayın klasörü>\soz.exe"

# kanca gecikmesi ölçümü
node test\kanca-sure.mjs "<yayın klasörü>\soz.exe"

# telefon güvenlik sınaması, makinenin kendi yerel ağ adresine karşı (sunucuyu betik açar ve kapatır)
node test\telefon-lan.mjs "<yayın klasörü>\soz.exe"

# kabuk denemeleri (başsız, sentetik veri)
Soz-Masaustu.exe --selftest --data "<sentetik veri klasörü>"
Soz-Masaustu.exe --smoke "<çıktı.json>" --smoke-panel --data "<sentetik veri klasörü>"

# tarayıcı gezisi, gerçek API ile (sunucuyu betik açar ve kapatır, Edge gerekir)
cd ui
node scripts\gercek-api.mjs "<soz.exe>" "test\ornek-veri" "<boş veri klasörü>" "<ekran klasörü>"

# arayüz kabulü: örnek veriyle (önce `npx vite preview --port 49412` açık olmalı) ve yayın sunucusu + sahte claude ile
BASE=http://localhost:49412 node scripts\kabul.mjs
SOZ_EXE="<yayın klasörü>/soz.exe" DIST="<yayın klasörü>/wwwroot" OUT="<ekran klasörü>" node scripts\gercek.mjs
```

`soz.exe serve` sınamalarda `--exit-after <saniye>` ve `--exit-with-parent <süreç no>` alır, böylece arkada sunucu kalmaz. Bütün komutların listesi: `soz.exe help`.

Klasörler: `src\Soz.Core` çekirdek, `src\Soz.Server` API ve `wwwroot` (önceden derlenmiş arayüz), `src\Soz.Cli` `soz.exe`, `src\Soz.Shell` `Soz-Masaustu.exe`, `src\Soz.Capsule` kapsül penceresi, `src\Soz.Kanca` kanca rölesi kaynağı, `ui` Svelte arayüzü, `test\Soz.Core.Tests` ve `test\Soz.Capsule.Tests` birim testleri, `test\ornek-veri` sentetik kayıtlar. Uçtan uca betikler (`test\e2e*.ps1`, `test\e2e-codex.mjs`, `ui\scripts\*.mjs`) yazarın yerel yerleşimine göre yazılmıştı ve bu depoda ayarlanmamıştır (bkz. README "Known issues").
