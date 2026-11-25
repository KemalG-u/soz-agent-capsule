> 🚧 **Durum: aktif geliştirme.** Yazar için çalışıyor; arayüzler değişebilir.

# Söz (soz-agent-capsule)

[English](README.md)

Söz, bir Windows masaüstü kapsülü ve yerel sunucudur. Kodlayan yardımcıyı (Claude Code, isteğe bağlı Codex) senin yerine çalıştırır ve "bitti" sözüne güvenmez. Yardımcı bitirdiğini söyleyince Söz, senin onayladığın kontrolleri çalıştırır (testler, oluşması gereken bir dosya, açılması gereken bir adres). Geçmezse yardımcıyı, kalan kontrolün adı ve çıktısıyla geri yollar (en çok 3 tur). İş yalnız kontroller geçtiyse yeşil görünür; kontrol yoksa "kanıtsız" yazar, asla "geçti" yazmaz.

## Neden var

Yardımcılar çoğu zaman kanıtsız "tamam" der ve bunu yakalamak için terminali izlemek yorucudur. Söz bu izlemeyi küçük bir panele taşır: düz Türkçe ilerleme satırları, tehlikeli komutlardan (klasör silme, proje dışına yazma) önce soran kurallı bir bekçi ve çalışma öncesi ile sonrasındaki test dosyalarını ve test sayısını karşılaştıran bir kanıt kapısı. Böylece testleri değiştirerek "testler geçti" denemez.

## Neler var

- Terminal yerine sade ilerleme satırlı iş paneli (masaüstü kapsülü ya da tarayıcı).
- Kanıt kapısı: yardımcı durunca onaylı kontrolleri çalıştırır, kalırsa geri yollar.
- Test kurcalama tespiti: değişen ya da silinen test dosyası, boşaltılmış test komutu ve düşen test sayısı işaretlenir, karar sana kalır.
- Modelsiz, kurallı bekçi: proje dışına yazma, korunan yollar, yasak ve önce sorulacak komutlar.
- Geçmiş konuşmalardan çıkan karar, kural ve yasak defteri (sen onaylamadan kural olmaz), sır süzgeçli geçmiş araması, harcama görünümü.
- İsteğe bağlı, yalnız okuyan telefon görünümü (yerel ağda, varsayılan kapalı).
- Her şey yerel makinede kalır; veri klasörü exe'nin yanındaki bir SQLite dosyasıdır (ya da `SOZ_DATA`).

## Gereksinimler

- Windows 10 ya da 11 (masaüstü kabuğu WebView2 ister; komut satırı ve sunucu düz .NET'tir).
- Kaynaktan derlemek için .NET 10 SDK (`global.json` 10.0.402'yi sabitler).
- Yerelde kurulu ve girişi yapılmış `claude` komutu (Claude Code). Söz senin kendi girişini kullanır, API anahtarı gerekmez. Codex isteğe bağlıdır.
- Node.js yalnız `ui/` arayüzünü yeniden derlemek için gerekir; derlenmiş paket `src/Soz.Server/wwwroot` içinde durur.

## Hızlı başlangıç

```
git clone https://github.com/KemalG-u/soz-agent-capsule.git
cd soz-agent-capsule
dotnet build Soz.sln
dotnet test Soz.sln
dotnet run --project src/Soz.Cli -- serve --open
```

`serve` yerel sunucuyu başlatır ve arayüzü tarayıcıda açar (varsayılan port 49300). Bir proje klasörü ekle, işi yaz, Ctrl+Enter'a bas. Komutu Claude Code oturumunun içinden değil, normal bir terminalden çalıştır: Söz, bir Claude oturumunun içinden gelen kabul ve ayar isteklerini bilerek reddeder. Masaüstü kabuğu `src/Soz.Shell` (`Soz-Masaustu.exe`) içindedir; yayın komutları kullanım kılavuzundadır.

Ayrıntılar: [docs/KULLANIM.tr.md](docs/KULLANIM.tr.md) tam kullanım kılavuzudur.

## Sınırlar ve doğrulanmamış iddialar

Söz bir güvenlik sınırı değil, bir özen katmanıdır. Amaç dürüst ama dalgın bir yardımcıyı görünür kılmaktır. Aynı Windows hesabında çalışan kararlı ve kötü niyetli bir yardımcıyı durdurmaz.

- Bekçi ve yalıtım iddiaları (bekçi kuralları, proje kodunu iş nesnesi içinde çalıştırma, ajan sürecinden gelen kabul ve ayar çağrılarını reddetme) **bağımsız olarak doğrulanmamıştır**. Yazar yalnız birim testleri ve kendi düşmanca incelemeleriyle denedi.
- Bekçi bazı şeyleri bilerek kaçırır: hedefi değişken olan komutlar, betik ya da `python -c` içinde yapılan işler, kısayollar, kodlanmış PowerShell, sembolik bağlantı üzerinden yazma, kabuk dışı araçlar (ör. MCP). Tam liste kılavuzda ("Bilinen sınırlar").
- Codex bağlayıcı üzerinden desteklenir, ama bekçisi yalnız aracın kendi işletim sistemi kumlamasıdır ve Windows'ta ne kadar sıkı olduğu doğrulanmadı.
- Sır süzgeci sezgiseldir, alışılmadık bir sırrı kaçırabilir.
- Telefon ucu yerel ağda şifresiz http kullanır; ortak ağlarda kapalı tut.
- Masaüstü kabuğunun gerçek pencere davranışı (tepsi menüsü, kısayol, klasör seçici) her sürümde elle tıklanarak denenmedi.
- Kurulum paketi (Velopack) imzasızdır, SmartScreen uyarabilir. Paketleme betikleri bu depoda yoktur.
- Kurulu uygulama koda yazılı bir güncelleme adresine bakar; yayın klasöründen çalışan kopya ağa hiç çıkmaz. `SOZ_UPDATE_URL` ile değiştirilir ya da `veri\shell-tercihleri.json` içine `"guncellemeDenetimi": false` yazılarak kapatılır.

## Bilinen sorunlar

- Uçtan uca betikler (`test/e2e*.ps1`, `test/e2e-codex.mjs`, `test/kanca-sure.mjs`, `ui/scripts/*.mjs`) yazarın yerel yerleşimine göre yazıldı; yayınlanmış bir derleme ve burada bulunmayan küçük bir örnek proje ister. CI'da çalışmazlar.
- Kanca röle programı `soz-kanca.exe` depoda yoktur (ikili dosya konmadı). `src/Soz.Kanca/derle.sh` ile derlenir (Windows ile gelen .NET Framework `csc.exe` yeter) ve `soz.exe`nin yanına konur; yoksa kancalar `soz.exe`nin kendisiyle çalışır (açılış daha yavaş).

## Test durumu

24 Kas 2026'da yazarın Windows 11 makinesinde, .NET SDK 10.0.402 ile, bu ağacın temiz bir kopyasında ölçüldü: `dotnet build Soz.sln` 0 hata ve 18 uyarıyla bitti; `dotnet test` `Soz.Capsule.Tests` içinde 554 testin 554'ünü, `Soz.Core.Tests` içinde 1878 testin 1878'ini geçti (Core takımı yaklaşık 7 dakika sürer). GitHub Actions iş akışı (`.github/workflows/ci.yml`) henüz çalışmadı, bu yüzden yeşil CI iddia edilmez.

## Lisans

Apache-2.0 (`LICENSE`, `NOTICE`, `THIRD-PARTY.md`). Telif (c) 2026 Kemal (KarıncaAI).

Yapay zekâ kullanımı: bu proje, yazarın yönlendirmesi ve denetimiyle yapay zekâ kodlama ajanlarıyla yazılmıştır.
