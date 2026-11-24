// f1: sınayıcı kusurlarının düzeltmeleri (s1 K-01..K-19, katalog şüpheleri). tr9 üstüne yayılır, en son bu dosya kazanır.
// Kurallar: sade günlük Türkçe, uzun tire ve noktalı virgül yok, teknik sözcük yok, ham hata metni yok.
export const tr10 = {
  // ---- ortak hata cümleleri: aynı hata her ekranda aynı sözü söyler (hata.ts) ----
  errNotFound: 'Bunu bulamadım. Silinmiş ya da kapanmış olabilir.',
  errAlreadyDone: 'Bu zaten yapılmış.',
  errServerFail: 'Söz bir hata verdi, yapılamadı.',
  errConflictGeneric: 'Bu şu an yapılamıyor. Biraz sonra yeniden dene.',
  errHelperMissing: 'Yardımcı bu bilgisayarda kurulu görünmüyor. Ayarlar sayfasında yolunu kontrol et.',

  // ---- oturum özeti ----
  ozGone: 'Bu çalışmanın kaydını bulamadım.',
  ozUnsure: 'Bu özetten emin değilim: {why}',
  ozUnsureNoWhy: 'Bu özetten emin değilim.',
  ozUnreadable: 'Bu çalışmanın kaydını okuyamadım, o yüzden ne yaptığını söyleyemiyorum.',
  ozNoDid: 'Henüz yaptığı bir şey görünmüyor.',
  ozNoTried: 'Bir deneme görünmüyor.',
  ozMore: 'Devamını göster',
  ozLess: 'Kısalt',

  // ---- sade anlat ----
  explainCost: 'Bu, Claude kotandan biraz harcar.',

  // ---- kaybolan cevap: kart başka yerden kalktı, yazılan metin durur ----
  lostReplyNote: 'Bu soru artık beklemiyor. Yazdığın aşağıda duruyor, kopyalayabilir ya da kapatabilirsin.',
  lostReplyMoved: 'Bu soru listede başka yere geçti. Yazdığın aşağıda duruyor, kopyalayabilir ya da kapatabilirsin.',
  lostReplyMovedChip: 'Yazdığın duruyor',
  lostReplyLabel: 'Yazdığın cevap',
  lostReplyClose: 'Kapat',

  // ---- tanıtım örneği dolu kutuyu ezmez ----
  introReplaceAsk: 'İş kutusunda yazdığın bir şey var. Yazdığın silinsin mi?',
  introReplaceYes: 'Evet, sil ve örneği koy',
  introReplaceNo: 'Hayır, kalsın',

  // ---- sayılar ve listeler aynı kaynaktan ----
  liveSwarmAbove: '{n} tanesi yukarıda seni bekliyor',
  kqTitle: 'Yardımcıya bıraktığın mesajlar',
  kqLead: 'Yardımcı durunca alacak.',
  kqFromPhone: 'Telefondan',
  kqFromPanel: 'Bilgisayardan',
  kqNoProject: 'Bir yardımcı penceresi',
  siraOutsideNote: 'Başka pencerelerde seni bekleyen {n} yardımcı daha var. Onları Şu An sayfasında görürsün.',
  siraOutsideLink: 'Şu An sayfasını aç',
  attBudgetQuiet: ' {n} kez de sustum.',
  attBudgetFull: ' Bugünkü sınır doldu. Yeni sorular için artık bildirim göndermem. Bekleyenler burada görünmeye devam eder.',

  // ---- iş bulunamadı (adres ya da kısayol var olmayan bir işi gösteriyor) ----
  jobNotFound: 'Bu işi bulamadım.',
  jobNotFoundLead: 'Silinmiş olabilir ya da adres yanlış. İşler listesinden devam edebilirsin.',
  jobNotFoundBack: 'İşler listesine dön',
  jobLoading: 'İş açılıyor…',

  // ---- durdurma ve izin kısayolları ----
  stopAsk: 'Bu iş durdurulsun mu? Sonra "Aynı işe yeni bir şey ekle" ile sürdürebilirsin.',
  startNeedsFolder: 'Klasör seçmeden başlatamam. Yazdığın iş kutuda duruyor.',
  startedInFolder: '{f} klasörü seçildi.',
  stopAskYes: 'Evet, durdur',
  stopAskNo: 'Vazgeç',
  kbStop: 'Seçili işi durdur (önce sorar)',
  kbYes: 'İzin kartı seçiliyken Evet',
  kbNo: 'İzin kartı seçiliyken Hayır',

  // ---- klasör ekleme ----
  folderAddedOk: '{n} eklendi.',

  // ---- bekleyen karta tıklayınca ----
  waitCardOpenJob: 'Tıklarsan bu işin sayfası açılır.',
  waitNoLongerWaiting: 'Bu artık beklemiyor.',
  hitNoteLead: 'Bildirimdeki iş cevaplanmış ya da kapanmış olabilir.',
  pcSaid: 'Yardımcı dedi: “{s}”',
  pcFoundTry: 'Söz denedi: {s}.',
  pcFoundTryAll: 'Söz denedi: {a} kontrolden {b} tanesi geçti.',

  ckRemoved: '“{l}” kaldırıldı.',
  undoBtn: 'Geri al',
  ckSwitchOnTip: 'Bu kontrol açık. Kapatırsan işlerde bakılmaz.',
  ckSwitchOffTip: 'Bu kontrol kapalı. Açarsan işlerde yeniden bakılır.',
  ckGateOnTip: 'Açık: testler geçmeden iş bitmiş sayılmaz. Kapatmak için tıkla.',
  ckGateOffTip: 'Kapalı: testler geçmese de iş bitebilir. Açmak için tıkla.',
  ckNeedCmd: 'Önce çalışacak komutu yaz.',
  ckNeedPath: 'Önce dosyanın yolunu yaz.',
  ckNeedUrl: 'Önce adresi yaz.',

  guardRuleNoChange: 'Bu kural değiştirilemedi.',
  actDenyTip: 'Durdur: kural bir şeyi bulursa yardımcıyı durdurur.',
  actAskTip: 'Sor: kural bir şeyi bulursa önce sana sorar.',
  tToolBash: 'komut çalıştırma',
  tToolWrite: 'dosya yazma',
  tToolEdit: 'dosya değiştirme',
  tToolRead: 'dosya okuma',
  tToolNotebook: 'not defteri değiştirme',
  guardTryNeedCmd: 'Önce denenecek komutu yaz.',
  guardTryNeedPath: 'Önce denenecek dosyanın yolunu yaz.',

  reportSaved: 'Resim indirildi: {f}',

  evOtherGroup: 'Başka bir olay',
  evOtherLine: 'Başka bir olay oldu.',

  searchNoRecords: 'Kayıtları henüz okumadım.',
  searchReadNow: 'Şimdi oku',
  searchReading: 'Okuyorum…',

  themeNotSaved: 'Kaydedilemedi, eski hâline döndü.',
  stNotNumber: 'Buraya sayı yaz.',
  pjFolderReady: 'Kutuda seçili, ne yaptıracağını yazabilirsin.',
  introSettingOpen: 'Tanıtım zaten açık.',
  stReverted: 'Seçim eski değerine döndü.',

  errFieldRange: '{f}: {a} ile {b} arasında olmalı.',
  errSettingRejected: 'Bu ayar kaydedilemedi. Girdiğin değeri kontrol et.',

  notInstalledShort: 'Kurulu değil',
  noHelperInstalled: 'Bu bilgisayarda kurulu yardımcı yok. Önce Claude Code gibi bir yardımcıyı kur, sonra buradan seç.',

  hooksNotInstalled: 'Terminal desteği kurulu değil',
  hooksCmdWhy: 'Çoğu kişi için yukarıdaki klasör düğmesi yeter, aynı işi tek tıkla yapar. Bu komutlar yalnız düğmeyi kullanamayanlar ve komut satırını bilenler içindir.',
  hooksPathNote: 'Örnekteki yol sana ait değil. Kendi klasörünün yoluyla değiştir.',
  hooksInstallTitle: 'İleri düzey: komut satırıyla kurulum',

  rowStoppedShutdown: 'Söz kapandığı için iş yarıda kaldı.',
  shutdownTitle: 'Söz kapandığı için bu iş yarıda kaldı. Sen durdurmadın.',
  shutdownBody: 'Yardımcının o ana kadar yaptıkları duruyor. Sürdürmek için aşağıdaki "Aynı işe yeni bir şey ekle" düğmesine bas.',

  panelResetBrowser: 'Bu yalnız masaüstü uygulamasında çalışır.',
  applyToProjectNamed: 'Bu klasöre uygula: {n}',

  rowHalf: 'Yarıda kaldı',
  grpHalf: 'Yarıda kaldı',
  rowHalfLine: 'Yardımcı çalışırken hata verdi, iş yarıda kaldı.',
  halfBody: 'Yardımcı çalışırken bir hata verdi ve işi yarıda bıraktı.',
  blockedRawNote: 'Yardımcı başlatılamadı.',

  rejectNote: 'Neyi beğenmedin? Yaz, yardımcıya gönderip devam ettireyim. Boş bırakıp kutudan vazgeçebilirsin.',
  rejectSend: 'Gönder ve devam ettir',
  guardTag: 'Komut koruması',
  guardTagFor: 'Komut koruması: {l} ({a})',
  protFull: 'Geri alma noktası var: işten önceki hâlini sakladım, istersen geri alırım.',
  protLimited: 'Geri alma kısmen mümkün.',
  protNone: 'Geri alma noktası yok.',

  approveToast: 'İzin verdin, yardımcıya iletildi.',
  denyToast: 'İzin vermedin, yardımcıya iletildi.',

  listWaitMore: '{n} tane daha Şu An sayfasında bekliyor',

  doneUnrelated: 'İş metninde bu dosyadan söz edilmiyor, o yüzden kendiliğinden seçmedim.',
  doneVague: 'Hangi dosyaya bakacağını söylemiyor, o yüzden kendiliğinden seçmedim.',

  folderUnc: 'Ağdaki klasörler henüz eklenemiyor. Klasörün bu bilgisayardaki yolunu yaz.',
  folderEmpty: 'Klasör yolunu yaz ya da Göz at ile seç.',
  folderAlready: 'Bu klasör zaten ekli.',

  // ---- tek sözcük: "Olmadı" (iş listesi, kart ve bildirimde "Kaldı" yok) ----
  pcFailed: "Olmadı",
  rejectedToast: "Kabul etmedin, iş olmadı olarak işaretlendi",
  decisionNote: "Kabul edersen iş “Sen onayladın” diye biter. Etmezsen “Olmadı” olur.",
  decNoteApproved: "Kabul edersen yardımcının değiştirdiği testler bundan sonra senin onayladığın testler olur ve iş “Sen onayladın” diye biter. Reddedersen iş “Olmadı” olur ve yardımcıya ne düzeltmesi gerektiğini yazabilirsin. Geri al, klasörü işe başlamadan önceki hâline döndürür.",
  decNoteAgentTests: "Kabul edersen iş “Sen onayladın” diye biter. Reddedersen iş “Olmadı” olur ve yardımcıya ne düzeltmesi gerektiğini yazabilirsin. Geri al, klasörü işe başlamadan önceki hâline döndürür.",
  pcSumFailed: "{n} test olmadı",
  mrNightFailed: "{n} olmadı",
} as const
