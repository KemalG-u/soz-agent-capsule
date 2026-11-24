# Söz yürütücüsü uçtan uca (FABLE-VIZYON-3 bölüm A, B): gerçek `soz serve` + sahte claude (test\sahte-claude\sahte-claude.mjs).
# Sahte claude, --settings ile verilen kancaları gerçekten çalıştırır (SAHTE_KANCALI=1); yani Stop kancası, kapı, bekçi ve Hakem gerçektir.
# Senaryolar:
#   1. başlat → ilerleme satırları → Stop kancası geri yollar → ajan düzeltir → iş "gecti"
#   2. bozuk: kapı geri yollar, ajan düzeltmez → 3 turda "kaldi" (asla "geçti" denmez)
#   3. onay akışı: bekçi "sor" der → waiting_approval → onayla → devam → bitti
#   4. durdur: çalışan iş + torun süreç → durdur → ikisi de ölür, yetim kalmaz
#   6-12. Denetim 2: testi boşaltan (paket), testi atlayan (atla), geçtikten sonra bozan (sonrabozdu), soru soran (soru),
#      onay beklerken Durdur, durdurulanı sürdür, geçtikten sonra süren (suren), alt ajan olayı (altajan)
#   13. durum sayıları, ayar ve proje yolu korumaları
# Veri klasörü, proje ve durum dosyaları _gecici altında kurulur; çıkışta sunucu ve geçici klasör kaldırılır.
# Çalıştırma: powershell -NoProfile -File test\e2e-yurutucu.ps1 [-Exe "<soz.exe>"] [-Port 49390]
param(
    [string]$Exe = "",
    [int]$Port = 49390,
    [string]$DotnetRoot = "D:\tools\dev\dotnet10"
)
$ErrorActionPreference = "Stop"
$urun = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $urun "src\Soz.Cli\bin\Debug\net10.0\soz.exe" }
if (-not (Test-Path $Exe)) { Write-Host "soz.exe bulunamadı: $Exe"; exit 1 }
if (-not (Test-Path (Join-Path (Split-Path $Exe) "coreclr.dll")) -and -not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = $DotnetRoot }
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)

$root = Join-Path $urun ("_gecici\e2e-yur-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $root "veri"
$proj = Join-Path $root "proje klasoru"   # boşluklu yol: ArgumentList/tırnak sınaması
$state = Join-Path $root "durum"
$torunFile = Join-Path $root "torun.pid"
$fake = Join-Path $urun "test\sahte-claude\sahte-claude.mjs"
$demo = Join-Path $urun "deneme-proje"
$fail = 0
$serverProc = $null
$base = "http://127.0.0.1:$Port"
$token = ""
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "GEÇTİ  $name $detail" } else { Write-Host "KALDI  $name $detail"; $script:fail++ }
}
function Api([string]$method, [string]$path, $body = $null) {
    $h = @{ "X-Soz-Token" = $token }
    if ($null -ne $body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress -Depth 6))
        return Invoke-RestMethod -Method $method -Uri "$base$path" -Headers $h -Body $bytes -ContentType "application/json; charset=utf-8"
    }
    return Invoke-RestMethod -Method $method -Uri "$base$path" -Headers $h
}
# Windows PowerShell 5.1: Invoke-RestMethod JSON dizisini TEK nesne (Object[]) olarak verir. @(Api ...) bunu bir kez daha sarar, Count hep 1 olur.
# Liste uçları (kanıt kayıtları gibi) bu yardımcıyla açılır: boş liste 0, iki kayıt 2 eleman verir.
function ApiList([string]$path) {
    $r = Api GET $path
    if ($null -eq $r) { return @() }
    return @($r | ForEach-Object { $_ })
}
function WaitRun([long]$jobId, [string[]]$statuses, [int]$timeoutSec = 90) {
    $end = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $end) {
        $tl = Api GET "/api/jobs/$jobId/timeline"
        if ($tl.run -and ($statuses -contains $tl.run.status)) { return $tl }
        Start-Sleep -Milliseconds 400
    }
    return Api GET "/api/jobs/$jobId/timeline"
}
function ApiCode([string]$method, [string]$path, $body = $null) {
    try { Api $method $path $body | Out-Null; return 200 } catch { return [int]$_.Exception.Response.StatusCode }
}
function NewJob([string]$text) { Api POST "/api/jobs" @{ projectId = $script:pid_; rawText = $text } }
function ResetProject {
    Copy-Item (Join-Path $demo "hesap.js") (Join-Path $proj "hesap.js") -Force
    Copy-Item (Join-Path $demo "package.json") (Join-Path $proj "package.json") -Force
    Copy-Item (Join-Path $demo "test\hesap.test.js") (Join-Path $proj "test\hesap.test.js") -Force
}
function Texts($tl) { @($tl.items | ForEach-Object { $_.text }) }

try {
    New-Item -ItemType Directory -Force -Path $proj, (Join-Path $proj "test"), $data, $state | Out-Null
    Copy-Item (Join-Path $demo "package.json") $proj
    Copy-Item (Join-Path $demo "test\hesap.test.js") (Join-Path $proj "test")
    ResetProject
    $broken = Get-Content -Raw -Encoding UTF8 (Join-Path $proj "hesap.js")
    Check "deneme projesi bozuk (a - b)" ($broken.Contains("a - b"))

    # sunucu: sahte claude ortamı sunucudan çocuğa geçer
    $env:SAHTE_KANCALI = "1"
    $env:SAHTE_STATE_DIR = $state
    $env:SAHTE_TORUN = $torunFile
    $env:SAHTE_LOG = Join-Path $root "sahte.log"
    $log = Join-Path $root "serve.log"
    $serverProc = Start-Process -FilePath $Exe -ArgumentList @("serve", "--port", "$Port", "--data", "`"$data`"", "--exit-after", "900", "--exit-with-parent", "$PID") `
        -RedirectStandardOutput $log -RedirectStandardError (Join-Path $root "serve.err") -PassThru -WindowStyle Hidden
    Write-Host "      sunucu PID $($serverProc.Id) port $Port"
    $ready = $false
    for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
        try { $s = Invoke-RestMethod "$base/api/session"; $token = $s.token; $ready = $true } catch { Start-Sleep -Milliseconds 500 }
    }
    Check "sunucu açıldı" $ready
    if (-not $ready) { throw "sunucu açılmadı" }

    Api PUT "/api/settings" @{ claudePath = $fake; runnerModel = "" } | Out-Null
    $st = Api GET "/api/settings"
    Check "ayar: claudePath sahte claude" ($st.claudePath -eq $fake) "(judgeClaudeEnabled=$($st.judgeClaudeEnabled))"

    # proje ekle + tek onay (ölçütler + kapı + Temkinli bekçi)
    $add = Api POST "/api/projects" @{ path = $proj }
    $script:pid_ = [long]$add.id
    Check "proje eklendi, ölçüt önerisi npm test (kapalı)" ($add.suggested[0].spec.cmd -eq "npm test" -and -not $add.suggested[0].enabled)
    $ap = Api POST "/api/projects/$($script:pid_)/approve-setup" @{ guardTemplate = $true }
    Check "tek onay: kapı açık + ölçüt açık + temkinli" ($ap.gate.gateEnabled -and $ap.checks[0].enabled -and $ap.guardTemplate -eq "temkinli")
    Check "proje listede" (@((Api GET "/api/projects") | Where-Object { $_.id -eq $script:pid_ }).Count -eq 1)

    # 1. düzelt: başlat → ilerleme → kapı geri yollar → düzeltir → gecti
    $j1 = NewJob "Toplama fonksiyonundaki hatayı düzelt, testler geçsin. [[sahte:duzelt]]"
    $s1 = Api POST "/api/jobs/$($j1.id)/start" @{}
    Check "başlat: çalışıyor, pencere yok" ($s1.run.status -eq "running")
    $tl1 = WaitRun $j1.id @("done", "failed", "stopped")
    $t1 = Texts $tl1
    Check "1: run bitti (done)" ($tl1.run.status -eq "done") "($($tl1.run.status))"
    Check "1: ilerleme satırları sade Türkçe" (($t1 -contains "hesap.js dosyasını okuyor") -and ($t1 -contains "bir komut çalıştırıyor: testler"))
    Check "1: araç adı gösterilmiyor" (@($t1 | Where-Object { $_ -match "\b(Read|Edit|Bash|Write)\b" }).Count -eq 0)
    Check "1: kapı geri yolladı (kaldı hükmü zaman çizgisinde)" (@($tl1.items | Where-Object { $_.kind -eq "proof" -and $_.verdict -eq "failed" }).Count -ge 1)
    Check "1: ajan düzeltti, kapı geçti hükmü" (@($tl1.items | Where-Object { $_.kind -eq "proof" -and $_.verdict -eq "passed" }).Count -ge 1)
    $fixed = Get-Content -Raw -Encoding UTF8 (Join-Path $proj "hesap.js")
    Check "1: hesap.js gerçekten düzeldi" ($fixed.Contains("a + b"))
    $jj1 = Api GET "/api/jobs/$($j1.id)"
    Check "1: iş gecti (Söz kendi testiyle doğruladı)" ($jj1.state -eq "gecti") "(durum $($jj1.state))"

    # 2. bozuk: ajan düzeltmez → kaldi, asla gecti değil
    ResetProject
    $j2 = NewJob "Toplama hatasını düzelt. [[sahte:bozuk]]"
    Api POST "/api/jobs/$($j2.id)/start" @{} | Out-Null
    $tl2 = WaitRun $j2.id @("done", "failed", "stopped")
    $jj2 = Api GET "/api/jobs/$($j2.id)"
    Check "2: run bitti" ($tl2.run.status -eq "done") "($($tl2.run.status))"
    Check "2: iş kaldi (geçti denmedi)" ($jj2.state -eq "kaldi") "(durum $($jj2.state), tur $($jj2.attempts))"
    Check "2: 3 turda kaldı" ($jj2.attempts -eq 3)
    Check "2: hesap.js hâlâ bozuk" ((Get-Content -Raw -Encoding UTF8 (Join-Path $proj "hesap.js")).Contains("a - b"))

    # 3. onay akışı
    ResetProject
    $j3 = NewJob "Eski klasörü temizle. [[sahte:sor]]"
    Api POST "/api/jobs/$($j3.id)/start" @{} | Out-Null
    $tl3 = WaitRun $j3.id @("waiting_approval", "failed", "stopped")
    Check "3: bekçi sordu → onay bekliyor" ($tl3.run.status -eq "waiting_approval") "($($tl3.run.status))"
    $pend = @($tl3.items | Where-Object { $_.kind -eq "approval" -and $_.status -eq "pending" })
    Check "3: bekleyen onay satırı" ($pend.Count -eq 1) "($($pend[0].text))"
    if ($pend.Count -eq 1) {
        $ok3 = Api POST "/api/jobs/$($j3.id)/approve" @{ eventId = [long]$pend[0].eventId; allow = $true }
        Check "3: onay 202, yeni run" ($ok3.run.status -eq "running")
        $tl3b = WaitRun $j3.id @("done", "failed", "stopped")
        Check "3: onaydan sonra devam edip bitti" ($tl3b.run.status -eq "done" -and ((Texts $tl3b) -contains "Klasör silindi. Tamamlandı.")) "($($tl3b.run.status))"
        # Süreç bitince hüküm yoksa iş "calisiyor" kalmaz: Söz kapıyı kendisi çalıştırır (burada ölçüt açık, hesap.js bozuk → kaldi)
        $st3 = ""
        for ($i = 0; $i -lt 60; $i++) { $st3 = (Api GET "/api/jobs/$($j3.id)/timeline?detail=1").state; if ($st3 -ne "calisiyor") { break }; Start-Sleep -Milliseconds 500 }
        Check "3: süreç bitince iş calisiyor kalmadı, hüküm verildi" ($st3 -in @("kaldi", "gecti", "verildi")) "(durum $st3)"
        $tl3c = Api GET "/api/jobs/$($j3.id)/timeline?detail=1"
        Check "3: aynı onay tek satır, satırlarda kimlik, kanıt sonucu gömülü" ((@($tl3c.items | Where-Object { $_.kind -eq "approval" }).Count -eq 1) -and (@($tl3c.items | Where-Object { -not $_.id }).Count -eq 0) -and (@($tl3c.items | Where-Object { $_.kind -eq "proof" -and $_.results.Count -ge 1 }).Count -ge 1))
        $log3 = Get-Content $env:SAHTE_LOG | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.scenario -eq "sor" }
        Check "3: ikinci çalıştırma --resume ile" (@($log3 | Where-Object { $_.argv -contains "--resume" }).Count -ge 1)
    }

    # 4. durdur: torun süreç dahil hepsi ölür
    $j4 = NewJob "Uzun iş. [[sahte:uzun]]"
    $s4 = Api POST "/api/jobs/$($j4.id)/start" @{}
    $torunPid = 0
    for ($i = 0; $i -lt 40 -and $torunPid -eq 0; $i++) {
        if (Test-Path $torunFile) { $txt = (Get-Content $torunFile -Raw).Trim(); if ($txt) { $torunPid = [int]$txt } }
        if ($torunPid -eq 0) { Start-Sleep -Milliseconds 250 }
    }
    Check "4: torun süreç başladı" ($torunPid -gt 0) "(PID $torunPid)"
    $fakePid = 0
    $log4 = Get-Content $env:SAHTE_LOG | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.scenario -eq "uzun" } | Select-Object -Last 1
    if ($log4) { $fakePid = [int]$log4.pid }
    $stop = Api POST "/api/jobs/$($j4.id)/stop" @{}
    Check "4: durdur cevabı stopped=true" ($stop.stopped -eq $true)
    $tl4 = WaitRun $j4.id @("stopped", "done", "failed") 20
    Check "4: run stopped" ($tl4.run.status -eq "stopped") "($($tl4.run.status))"
    Start-Sleep -Milliseconds 800
    Check "4: torun süreç öldü" ($torunPid -gt 0 -and -not (Get-Process -Id $torunPid -ErrorAction SilentlyContinue))
    Check "4: sahte claude öldü" ($fakePid -gt 0 -and -not (Get-Process -Id $fakePid -ErrorAction SilentlyContinue))
    $jj4 = Api GET "/api/jobs/$($j4.id)"
    Check "4: iş geçti sayılmadı" ($jj4.state -ne "gecti") "(durum $($jj4.state))"

    # ---- Denetim 2 senaryoları ----
    function LastProof([long]$jobId) { @(ApiList "/api/proof/runs?jobId=$jobId") | Select-Object -First 1 } # sunucu en yeniyi başa koyar
    function WaitState([long]$jobId, [string[]]$states, [int]$timeoutSec = 90) {
        $end = (Get-Date).AddSeconds($timeoutSec)
        $j = $null
        while ((Get-Date) -lt $end) {
            $j = Api GET "/api/jobs/$jobId"
            if ($states -contains $j.state -and ($null -eq $j.run -or $j.run.status -ne "running")) { return $j }
            Start-Sleep -Milliseconds 400
        }
        return $j
    }

    # 6. K-1: ajan test betiğini boşaltır → "geçti" DEĞİL, definition_changed; kapı bir kez geri yollar, ısrarda karar kullanıcıda
    ResetProject
    $j6 = NewJob "Toplamayı düzelt. [[sahte:paket]]"
    Api POST "/api/jobs/$($j6.id)/start" @{} | Out-Null
    $jj6 = WaitState $j6.id @("karar_bekliyor", "gecti", "kaldi", "durduruldu", "baslatilamadi")
    $p6 = LastProof $j6.id
    Check "6: test betiğini boşaltan ajan geçti ALMADI" ($jj6.state -ne "gecti") "(durum $($jj6.state))"
    Check "6: iş karar_bekliyor" ($jj6.state -eq "karar_bekliyor")
    Check "6: kanıt nedeni definition_changed, hüküm unchecked" ($p6.reasonCode -eq "definition_changed" -and $p6.verdict -eq "unchecked") "($($p6.reasonCode))"
    Check "6: değişen dosya package.json" (@($p6.changedFiles) -contains "package.json")
    $rej = Api POST "/api/jobs/$($j6.id)/accept" @{ accept = $false }
    Check "6: kullanıcı reddetti → kaldi" ($rej.job.state -eq "kaldi") "($($rej.job.state))"

    # 7. K-1: kalan testi atlayan ajan
    ResetProject
    # hesap.js düzgün olmalı: sahte ajan ilk testi test.skip yapar; hesap bozuksa kalan test kalır ve hüküm "kaldı" olur (atlama sınaması olmaz)
    $fx = Get-Content -Raw -Encoding UTF8 (Join-Path $proj "hesap.js")
    Set-Content -Path (Join-Path $proj "hesap.js") -Encoding UTF8 -Value ($fx.Replace("return a - b;", "return a + b;"))
    $j7 = NewJob "Testleri geçir. [[sahte:atla]]"
    Api POST "/api/jobs/$($j7.id)/start" @{} | Out-Null
    $jj7 = WaitState $j7.id @("karar_bekliyor", "gecti", "kaldi", "durduruldu", "baslatilamadi")
    $p7 = LastProof $j7.id
    Check "7: testi atlayan ajan geçti ALMADI" ($jj7.state -ne "gecti") "(durum $($jj7.state))"
    Check "7: neden tests_changed ya da count_dropped" ($p7.reasonCode -in @("tests_changed", "count_dropped")) "($($p7.reasonCode))"
    $null = Api POST "/api/jobs/$($j7.id)/accept" @{ accept = $false }

    # 8. K-2: geçtikten sonra aynı oturumda kodu bozan ajan → nihai hüküm kaldi
    ResetProject
    $j8 = NewJob "Düzelt. [[sahte:sonrabozdu]]"
    Api POST "/api/jobs/$($j8.id)/start" @{} | Out-Null
    $jj8 = WaitState $j8.id @("gecti", "kaldi", "karar_bekliyor", "durduruldu", "baslatilamadi")
    Check "8: geçtikten sonra bozan ajan: nihai hüküm kaldi" ($jj8.state -eq "kaldi") "(durum $($jj8.state))"
    $runs8 = @(ApiList "/api/proof/runs?jobId=$($j8.id)")
    Check "8: önce geçti sonra kaldı hükümleri kayıtlı" ((@($runs8 | Where-Object { $_.verdict -eq "passed" }).Count -ge 1) -and (@($runs8 | Where-Object { $_.verdict -eq "failed" }).Count -ge 1))
    Check "8: kartta 'önceki duruşta geçmişti' notu" (@($runs8 | Where-Object { $_.summary -like "*Önceki duruşta geçmişti*" }).Count -ge 1)

    # 9. Ö-3: soru soran ajan → cevap_bekliyor, kapı koşmadı
    ResetProject
    $j9 = NewJob "Bir şey sor. [[sahte:soru]]"
    Api POST "/api/jobs/$($j9.id)/start" @{} | Out-Null
    $jj9 = WaitState $j9.id @("cevap_bekliyor", "gecti", "kaldi", "durduruldu", "baslatilamadi")
    Check "9: soru soran ajan: iş cevap_bekliyor" ($jj9.state -eq "cevap_bekliyor") "(durum $($jj9.state))"
    Check "9: soru metni karta geldi" ("$($jj9.question)" -like "*Hangisini*")
    Check "9: kapı koşmadı (kanıt kaydı yok)" (@(ApiList "/api/proof/runs?jobId=$($j9.id)").Count -eq 0)

    # 10. Ö-8: onay beklerken Durdur → durduruldu, bekleyen onay reddedilir
    ResetProject
    $j10 = NewJob "Eski klasörü temizle. [[sahte:sor]]"
    Api POST "/api/jobs/$($j10.id)/start" @{} | Out-Null
    $jj10 = WaitState $j10.id @("onay_bekliyor", "gecti", "kaldi", "durduruldu", "baslatilamadi")
    Check "10: onay bekliyor durumu" ($jj10.state -eq "onay_bekliyor") "(durum $($jj10.state))"
    Check "10: bekleyen onay listesi dolu" (@($jj10.pendingApprovals).Count -eq 1)
    $stop10 = Api POST "/api/jobs/$($j10.id)/stop" @{}
    Check "10: onay beklerken Durdur çalıştı" ($stop10.stopped -eq $true -and $stop10.job.state -eq "durduruldu") "($($stop10.job.state))"
    Check "10: bekleyen onay kalmadı" (@((Api GET "/api/jobs/$($j10.id)").pendingApprovals).Count -eq 0)

    # 4b. K-6: durdurulan iş "Bu işe devam et" ile aynı oturumda sürer
    $cont = Api POST "/api/jobs/$($j4.id)/continue" @{}
    Check "4b: devam et: yeni süreç başladı" ($cont.run.status -eq "running") "($($cont.run.status))"
    # yeni süreç günlüğe kendi satırını biraz sonra yazar: sabit okumak yarış olur, en çok 15 sn yoklanır
    $log4b = @()
    $endLog = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $endLog) {
        $log4b = @(Get-Content $env:SAHTE_LOG | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.scenario -eq "uzun" -and $_.argv -contains "--resume" })
        if ($log4b.Count -ge 1) { break }
        Start-Sleep -Milliseconds 300
    }
    Check "4b: --resume ile aynı oturum" ($log4b.Count -ge 1)
    Start-Sleep -Milliseconds 1500
    $null = Api POST "/api/jobs/$($j4.id)/stop" @{}
    $null = WaitRun $j4.id @("stopped", "done", "failed") 20

    # 11. K-2: geçtikten sonra süren ajan (bozmuyor) → kapı yine koşar, iş geçti kalır
    ResetProject
    $j11 = NewJob "Düzelt ve devam et. [[sahte:suren]]"
    Api POST "/api/jobs/$($j11.id)/start" @{} | Out-Null
    $jj11 = WaitState $j11.id @("gecti", "kaldi", "karar_bekliyor", "durduruldu", "baslatilamadi")
    $runs11 = @(ApiList "/api/proof/runs?jobId=$($j11.id)")
    Check "11: geçtikten sonra süren ajan: iş gecti kaldı" ($jj11.state -eq "gecti") "(durum $($jj11.state))"
    Check "11: kapı sonraki duruşta da koştu (en az 2 kanıt)" ($runs11.Count -ge 2) "($($runs11.Count))"

    # 12. K-8: alt ajan olayı ana işi etkilemez
    ResetProject
    $j12 = NewJob "Yardımcı ajan kullan. [[sahte:altajan]]"
    Api POST "/api/jobs/$($j12.id)/start" @{} | Out-Null
    $jj12 = WaitState $j12.id @("gecti", "kaldi", "karar_bekliyor", "cevap_bekliyor", "durduruldu", "baslatilamadi")
    $tl12 = Api GET "/api/jobs/$($j12.id)/timeline"
    $t12 = Texts $tl12
    Check "12: alt ajanın sorusu ana işi cevap_bekliyor yapmadı" ($jj12.state -ne "cevap_bekliyor") "(durum $($jj12.state))"
    Check "12: alt ajanın sözü satırlara girmedi" (@($t12 | Where-Object { $_ -like "*Alt ajan*" }).Count -eq 0)
    Check "12: yardımcı ajan tek satıra toplandı" (@($t12 | Where-Object { $_ -eq "bir alt yardımcı çalıştırıyor" }).Count -eq 1) "($(@($t12 | Where-Object { $_ -like '*yardımcı*' }).Count) satır)"

    # 6c. Denetim 3 (ilke 2): kabul yalnız insandan. Kabul edilen iş "sen onayladın" diye ayrı sayılır, kanal kayıtlıdır.
    ResetProject
    $j6c = NewJob "Toplamayı düzelt. [[sahte:paket]]"
    Api POST "/api/jobs/$($j6c.id)/start" @{} | Out-Null
    $jj6c = WaitState $j6c.id @("karar_bekliyor", "gecti", "kaldi", "durduruldu", "baslatilamadi")
    Check "6c: kabul öncesi iş karar_bekliyor (kendiliğinden geçmedi)" ($jj6c.state -eq "karar_bekliyor") "(durum $($jj6c.state))"
    # kabul, kartta GÖSTERİLEN anlık görüntüyü onaylar (denetim 4, E): kimlik zaman çizgisindeki son kanıt kaydından gelir
    $tl6c = Api GET "/api/jobs/$($j6c.id)/timeline?detail=1"
    $px6c = @($tl6c.items | Where-Object { $_.kind -eq "proof" -and $_.verdict }) | Select-Object -Last 1
    Check "6c: karar kartı görüntü kimliği taşıyor" ("$($px6c.snapshotId)" -match "^[0-9a-f]{16}$") "($($px6c.snapshotId))"
    $acc = Api POST "/api/jobs/$($j6c.id)/accept" @{ accept = $true; snapshotId = "$($px6c.snapshotId)" }
    Check "6c: insan kabulü: iş gecti ve acceptedByUser" ($acc.job.state -eq "gecti" -and $acc.job.acceptedByUser -eq $true) "($($acc.job.state))"
    # Söz kabulün kanalını çağıran sürecin kimliğinden çıkarır: PowerShell "komut satırı" sayılır, "arayüz" yalnız panelden (kabuk penceresi ya da tarayıcı) gelir (arayüz yolu uiscriptskabul.mjs ile denenir)
    Check "6c: kabul kanalı kayıtlı (komut satırı), kanıtsız kabul değil" ($acc.job.acceptedVia -eq "komut satırı" -and $acc.job.acceptedUnverified -eq $false) "($($acc.job.acceptedVia))"
    $now6 = Api GET "/api/now"
    Check "6c: /api/now sen onayladın ayrı sayılır" ($now6.counts.jobsAccepted -ge 1) "($($now6.counts.jobsAccepted))"

    # 13. Ö-5 ve Ö-8 ayarları
    $now = Api GET "/api/now"
    Check "13: /api/now canlı süreç sayısı 0 (hiç süreç çalışmıyor)" ($now.counts.jobsRunning -eq 0) "($($now.counts.jobsRunning))"
    Check "13: /api/now sırada ve bekleyen sayıları var" (($null -ne $now.counts.queued) -and ($now.counts.waiting -ge 1)) "(bekleyen $($now.counts.waiting))"
    Check "13: ayar: maxParallelRuns 7 reddedilir" ((ApiCode PUT "/api/settings" @{ maxParallelRuns = 7 }) -eq 400)
    Check "13: ayar: olmayan claudePath reddedilir" ((ApiCode PUT "/api/settings" @{ claudePath = (Join-Path $root "yok\claude.exe") }) -eq 400)
    Check "13: ayar: kabuk karakterli model adı reddedilir" ((ApiCode PUT "/api/settings" @{ runnerModel = "x & calc" }) -eq 400)
    Check "13: proje: Windows klasörü reddedilir (200 + ok:false)" ((Api POST "/api/projects" @{ path = $env:SystemRoot }).ok -eq $false)
    Check "13: proje: göreli yol reddedilir (200 + ok:false)" ((Api POST "/api/projects" @{ path = "goreli\klasor" }).code -eq "yol_reddedildi")
    Check "13: proje: sürücü kökü reddedilir (200 + ok:false)" ((Api POST "/api/projects" @{ path = ([IO.Path]::GetPathRoot($proj)) }).ok -eq $false)
    Check "13: proje: olmayan klasör klasor_yok" ((Api POST "/api/projects" @{ path = (Join-Path $root "yok-klasor") }).code -eq "klasor_yok")

    # 5. güvenlik: belirteçsiz değişiklik reddedilir
    $code = 0
    try { Invoke-RestMethod -Method Post -Uri "$base/api/jobs/$($j1.id)/start" -Body "{}" -ContentType "application/json" | Out-Null } catch { $code = [int]$_.Exception.Response.StatusCode }
    Check "5: belirteçsiz start 401" ($code -eq 401) "($code)"
}
catch {
    Write-Host "HATA: $($_.Exception.Message)"
    $fail++
}
finally {
    if ($serverProc -and -not $serverProc.HasExited) { Stop-Process -Id $serverProc.Id -Force -ErrorAction SilentlyContinue; Write-Host "      sunucu PID $($serverProc.Id) kapatıldı" }
    Start-Sleep -Milliseconds 500
    if (Test-Path $root) { Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue }
}
if ($fail -eq 0) { Write-Host "E2E-YÜRÜTÜCÜ GEÇTİ"; exit 0 } else { Write-Host "E2E-YÜRÜTÜCÜ KALDI ($fail)"; exit 1 }
