// f7: "kişi neye basacağını biliyor mu" düzeltmeleri (u1 TASARIM.md A1-A4, O1-O12, D1-D10). Yalnız yeni anahtarlar, tr10 üstüne yayılır.
// Kurallar: sade günlük Türkçe, uzun tire ve noktalı virgül yok. Sözlük: klasör (proje değil), yardımcı (yapay zekâ değil).
export const tr11 = {
  // O5: iş başlayınca görünür onay
  jobStartedToast: 'İş başladı: {n}',
  jobStartedWatch: 'İzle',
  recentGivenLabel: 'Az önce verdiğin',
  // A4: başlamamış iş
  draftTitle: 'Bu iş henüz başlamadı.',
  draftBody: 'Başlatırsan yardımcı işe şimdi başlar. Vazgeçersen iş başlamaz ve klasörde hiçbir şey değişmez.',
  draftCancel: 'Vazgeç',
  draftCancelled: 'İşten vazgeçildi. Klasörde hiçbir şey değişmedi.',
  // O6: olumsuz sonuçta sıradaki adım
  redoTitleNoWork: 'Yardımcı işi yapmamış görünüyor.',
  redoTitleFailed: 'Bu iş olmadı.',
  redoBody: 'Yardımcıya yeniden yaptırabilirsin. Aşağıdaki kutuya hazır bir cümle koyarım, istersen değiştirip gönderirsin.',
  redoBtn: 'Yardımcıya yeniden yaptır',
  redoDraftNoWork: 'Klasörde hiçbir değişiklik olmadı, iş yapılmamış görünüyor. Lütfen işi gerçekten yap, dosyaları değiştir ve bitince neyi değiştirdiğini kısaca yaz.',
  redoDraftFailed: 'Söz’ün denemesi geçmedi. Lütfen neden geçmediğine bak, düzelt ve bitince neyi değiştirdiğini kısaca yaz.',
  // A3: geri alma notu kısa
  protFullShort: 'Geri alma noktası var',
  // D1 ve O12: iş kutusu
  barKbHint: 'Ctrl+Enter ile başlat',
  nightOn: 'Açık',
  nightOff: 'Kapalı',
  // O8: bekleyen özeti
  expectSummaryGo: 'Göster',
  // O3: Klasörlerim üstünde Söz'e eklenen klasörler
  pjMineTitle: 'Söz’e eklediğin klasörler',
  pjMineJobs: '{n} iş',
  pjMineJobsMore: 'en az {n} iş',
  pjMineGive: 'İş ver',
  pjMineNone: 'Henüz klasör eklemedin. İlk işini verirken klasörü seçersin.',
  // O10: yardımcı durumu tek kaynaktan
  agentNotReady: 'kurulu ama hazır değil',
  // O12: kod sözcüğü yerine
  testCmdPlain: 'Klasörün kendi testleri',
  addFolderItemFirst: 'Klasör seç...',
} as const

export const en11 = {
  addFolderItemFirst: 'Choose a folder...',
  jobStartedToast: 'Job started: {n}',
  jobStartedWatch: 'Watch',
  recentGivenLabel: 'Just given',
  draftTitle: 'This job has not started yet.',
  draftBody: 'If you start it, the helper begins now. If you cancel, the job does not start and nothing in the folder changes.',
  draftCancel: 'Cancel',
  draftCancelled: 'Job cancelled. Nothing in the folder changed.',
  redoTitleNoWork: 'The helper does not seem to have done the job.',
  redoTitleFailed: 'This job did not work.',
  redoBody: 'You can have the helper do it again. I put a ready sentence in the box below, change it if you like and send it.',
  redoBtn: 'Have the helper do it again',
  redoDraftNoWork: 'Nothing changed in the folder, the job does not seem to be done. Please really do the job, change the files and when you are done write briefly what you changed.',
  redoDraftFailed: 'Söz’s check did not pass. Please look at why, fix it and when you are done write briefly what you changed.',
  protFullShort: 'Undo point saved',
  barKbHint: 'Ctrl+Enter to start',
  nightOn: 'On',
  nightOff: 'Off',
  expectSummaryGo: 'Show',
  pjMineTitle: 'Folders you added to Söz',
  pjMineJobs: '{n} jobs',
  pjMineJobsMore: 'at least {n} jobs',
  pjMineGive: 'Give a job',
  pjMineNone: 'You have not added a folder yet. You pick the folder when you give your first job.',
  agentNotReady: 'installed but not ready',
  testCmdPlain: 'The folder’s own tests',
} as const
