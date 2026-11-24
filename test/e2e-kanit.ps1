# Söz 2 uçtan uca: İş Kartı + Kanıt Kapısı + kuyruk + sabah raporu (FABLE-VIZYON-2 bölüm 7, madde 1/3/4/5; sahte stdin düzeyi).
# deneme-proje'nin bozuk kopyası _gecici altında kurulur (asıl deneme-proje bozuk kalır), temiz veri klasörü kullanılır.
# Akış: ölçüt öner (kapalı) → açmadan çalışmaz → iş kartı → istem yapıştırılır (user-prompt-submit) → stop: block + kalan testin adı
#       → dosya düzeltilir → stop: izin + passed + iş gecti → gece işi 3 tur → kaldi → kuyruk mesajı iletilir → bozuk girdi çıkış 0
#       → notification → now/morning.
# Çalıştırma: powershell -NoProfile -File test\e2e-kanit.ps1 [-Exe "<yayin>\soz.exe"]
param(
    [string]$Exe = "",
    [string]$DotnetRoot = "D:\tools\dev\dotnet10"
)
$ErrorActionPreference = "Stop"
$urun = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $urun "src\Soz.Cli\bin\Debug\net10.0\soz.exe" }
if (-not (Test-Path $Exe)) { Write-Host "soz.exe bulunamadı: $Exe"; exit 1 }
if (-not (Test-Path (Join-Path (Split-Path $Exe) "coreclr.dll")) -and -not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = $DotnetRoot }
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)

$root = Join-Path $urun ("_gecici\e2e-kanit-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $root "veri"
$proj = Join-Path $root "proje"
$demo = Join-Path $urun "deneme-proje"
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "GEÇTİ  $name $detail" } else { Write-Host "KALDI  $name $detail"; $script:fail++ }
}
function Soz { & $Exe @args --data $data }
function SozJson { (& $Exe @args --json --data $data | Out-String) | ConvertFrom-Json }
function Hook([string]$ev, [hashtable]$obj) {
    $stdin = $obj | ConvertTo-Json -Compress
    $out = $stdin | & $Exe hook $ev --data $data | Out-String
    $script:hookExit = $LASTEXITCODE
    return $out.Trim()
}

try {
    New-Item -ItemType Directory -Force -Path $proj, (Join-Path $proj "test"), $data | Out-Null
    Copy-Item (Join-Path $demo "package.json") $proj
    Copy-Item (Join-Path $demo "hesap.js") $proj
    Copy-Item (Join-Path $demo "test\hesap.test.js") (Join-Path $proj "test")
    $broken = Get-Content -Raw -Encoding UTF8 (Join-Path $proj "hesap.js")

    # 1. Ölçüt öner: kapalı gelir
    $sug = SozJson checks suggest --project $proj
    $check = $sug.checks | Select-Object -First 1
    Check "öneri: npm test" ($check.spec.cmd -eq "npm test") "(#$($check.id))"
    Check "öneri kapalı (enabled=false, origin=auto)" ((-not $check.enabled) -and $check.origin -eq "auto")

    # 2. Madde 4: açılmadan hiçbir komut çalışmaz
    Soz gate on --project $proj | Out-Null
    $r0 = SozJson proof run --project $proj
    Check "açık ölçüt yok → unchecked" ($r0.verdict -eq "unchecked") "($($r0.summary))"
    Check "unchecked'te sonuç satırı yok (komut çalışmadı)" (@($r0.results).Count -eq 0)
    Soz gate off --project $proj | Out-Null

    # 3. İş kartı (kapalı öneri bu iş için seçildi)
    $job = SozJson job new --project $proj --check $check.id "Toplama fonksiyonundaki hatayı düzelt, testler geçsin."
    Check "iş taslak" ($job.state -eq "taslak") "(#$($job.id))"
    Check "istem ham sözü içerir" ($job.promptText.StartsWith("Toplama fonksiyonundaki hatayı düzelt, testler geçsin."))
    Check "istem ölçütü içerir" ($job.promptText.Contains("- Testler geçsin (komut: npm test)"))
    $listChecks = SozJson checks list --project $proj
    Check "proje varsayılanı hâlâ kapalı" (-not (@($listChecks | Where-Object { $_.id -eq $check.id })[0].enabled))
    Soz job give $job.id | Out-Null

    # 4. İstem terminale yapıştırıldı → iş oturuma bağlanır
    Hook "user-prompt-submit" @{ session_id = "e2e-kanit-1"; transcript_path = ""; cwd = $proj; hook_event_name = "UserPromptSubmit"; prompt = $job.promptText } | Out-Null
    $j1 = SozJson job show $job.id
    Check "iş calisiyor ve oturuma bağlı" ($j1.state -eq "calisiyor" -and $null -ne $j1.sessionId)

    # 5. Madde 1: ajan "bitti" der → kapı çalıştırır → block + kalan testin adı
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $o1 = Hook "stop" @{ session_id = "e2e-kanit-1"; transcript_path = ""; cwd = $proj; hook_event_name = "Stop"; stop_hook_active = $false; last_assistant_message = "Bitti, testler geçti." }
    $sw.Stop()
    $b1 = $o1 | ConvertFrom-Json
    Check "stop çıkış kodu 0" ($hookExit -eq 0)
    Check "stop: decision=block" ($b1.decision -eq "block") "($($sw.ElapsedMilliseconds) ms)"
    Check "block nedeni kalan testin adını içerir" ($b1.reason.Contains("toplama iki sayiyi toplar"))
    Write-Host "      neden (ilk satır): $(($b1.reason -split "`n")[0])"

    # 6. Dosya düzeltilir → tekrar → izin + passed + iş gecti
    Set-Content -Path (Join-Path $proj "hesap.js") -Encoding UTF8 -Value ($broken.Replace("return a - b;", "return a + b;"))
    $o2 = Hook "stop" @{ session_id = "e2e-kanit-1"; cwd = $proj; hook_event_name = "Stop"; stop_hook_active = $true; last_assistant_message = "Düzelttim, bitti." }
    $b2 = $o2 | ConvertFrom-Json
    Check "ikinci stop: block yok" ($null -eq $b2.decision) "($($b2.systemMessage))"
    $j2 = SozJson job show $job.id
    Check "iş gecti" ($j2.state -eq "gecti") "(tur $($j2.attempts), son hüküm $($j2.lastVerdict))"
    Check "son çalışma passed + iddia tablosu" ($j2.runs[0].verdict -eq "passed" -and $j2.runs[0].claim.label -eq "bitti dedi / kanıt: geçti") "($($j2.runs[0].claim.label))"
    Check "ilk çalışma failed + iddia tablosu" ($j2.runs[1].verdict -eq "failed" -and $j2.runs[1].claim.label -eq "bitti dedi / kanıt: kaldı")

    # 7. Deneme sayacı: gece işi, bozuk dosya, gateMaxAttempts=3 → block, block, izin + kaldi
    Set-Content -Path (Join-Path $proj "hesap.js") -Encoding UTF8 -Value $broken
    $night = SozJson job new --project $proj --night --check $check.id "Gece işi: testleri geçir."
    Soz job give $night.id | Out-Null
    # Gece işi de istemi yapıştırılan oturuma bağlanır (başka oturuma kendiliğinden bağlanmaz).
    Hook "user-prompt-submit" @{ session_id = "e2e-kanit-gece"; cwd = $proj; hook_event_name = "UserPromptSubmit"; prompt = $night.promptText } | Out-Null
    $n1 = (Hook "stop" @{ session_id = "e2e-kanit-gece"; cwd = $proj; stop_hook_active = $false; last_assistant_message = "Bitti." }) | ConvertFrom-Json
    $n2 = (Hook "stop" @{ session_id = "e2e-kanit-gece"; cwd = $proj; stop_hook_active = $true; last_assistant_message = "Bitti." }) | ConvertFrom-Json
    $n3 = (Hook "stop" @{ session_id = "e2e-kanit-gece"; cwd = $proj; stop_hook_active = $true; last_assistant_message = "Bitti." }) | ConvertFrom-Json
    Check "gece: tur 1 block, tur 2 block" ($n1.decision -eq "block" -and $n2.decision -eq "block" -and $n1.reason.Contains("tur 1/3") -and $n2.reason.Contains("tur 2/3"))
    Check "gece: tur 3 izin (ajan kilitlenmez)" ($null -eq $n3.decision) "($($n3.systemMessage))"
    $nj = SozJson job show $night.id
    Check "gece işi kaldi, 3 tur" ($nj.state -eq "kaldi" -and $nj.attempts -eq 3)

    # 8. Madde 3: kuyruk mesajı ajanın duruşunda iletilir
    $sess = (SozJson job show $job.id).sessionId
    $q = SozJson queue add --session $sess "README dosyasına da bir satır ekle."
    Check "kuyruğa eklendi" ($null -eq $q.deliveredAt) "(#$($q.id))"
    $qo = (Hook "stop" @{ session_id = "e2e-kanit-1"; cwd = $proj; stop_hook_active = $false; last_assistant_message = "Tamam." }) | ConvertFrom-Json
    Check "kuyruk: block + Kullanıcıdan mesaj" ($qo.decision -eq "block" -and $qo.reason -eq "Kullanıcıdan mesaj: README dosyasına da bir satır ekle.")
    $ql = SozJson queue list --project $proj
    Check "kuyruk: iletildi işaretli" ($null -ne @($ql)[0].deliveredAt)

    # 9. Madde 5: kanca hataları ajanı kilitlemez
    $bad = "{bozuk" | & $Exe hook stop --data $data | Out-String
    Check "bozuk stdin: çıkış 0, boş çıktı" ($LASTEXITCODE -eq 0 -and $bad.Trim() -eq "")
    $file = Join-Path $root "dosya.txt"; Set-Content $file "x"
    $bad2 = '{"session_id":"x","cwd":"C:\\"}' | & $Exe hook stop --data (Join-Path $file "veri") | Out-String
    Check "veri klasörü açılamıyor: çıkış 0, boş çıktı" ($LASTEXITCODE -eq 0 -and $bad2.Trim() -eq "")
    $bad3 = "" | & $Exe hook notification --data $data | Out-String
    Check "boş stdin (notification): çıkış 0" ($LASTEXITCODE -eq 0 -and $bad3.Trim() -eq "")

    # 10. Notification → Bugün'de "seni bekliyor"
    Hook "notification" @{ session_id = "e2e-kanit-izin"; cwd = $proj; hook_event_name = "Notification"; message = "Claude needs your permission"; notification_type = "permission_prompt" } | Out-Null
    $now = SozJson now
    Check "now: seni bekleyen 1" (@($now.needsYou | Where-Object { $_.reason -eq "permission_prompt" }).Count -eq 1) "($($now.sentence))"

    # 11. Sabah raporu
    $m = SozJson morning
    Check "sabah: 1 geçti, 1 kaldı" (@($m.passed).Count -eq 1 -and @($m.failed).Count -eq 1) "($($m.sentence))"
    Check "sabah: kalan ölçüt adı" (@($m.failed)[0].failedChecks -contains "Testler geçsin")

    # 12. Kanca kurulumu: beş kanca, Stop zaman aşımı bütçe+30
    $settings = Join-Path $root ".claude\settings.json"
    Soz hooks install --settings $settings | Out-Null
    $s = Get-Content -Raw $settings | ConvertFrom-Json
    Check "kurulum: beş kanca" ((@($s.hooks.PSObject.Properties.Name) -join ",") -eq "SessionStart,UserPromptSubmit,Stop,Notification,PreToolUse")
    Check "kurulum: Stop zaman aşımı 330 sn" ($s.hooks.Stop[0].hooks[0].timeout -eq 330)
}
catch {
    Write-Host "HATA: $($_.Exception.Message)"
    $fail++
}
finally {
    if (Test-Path $root) { Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue }
}

if ($fail -eq 0) { Write-Host "SONUÇ: hepsi geçti"; exit 0 } else { Write-Host "SONUÇ: $fail madde kaldı"; exit 1 }
