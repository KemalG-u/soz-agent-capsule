# Söz uçtan uca sınaması (sözleşme bölüm 8).
# Temiz veri klasörü → sentetik kayıtları içe al → öneri > 0 → birini onayla → paket metni içerir
# → session-start kancası çıktısı içerir → user-prompt-submit tekrar uyarısı → dışa aktar → AGENTS.md bloğu var.
# Yalnız sentetik veri (test\ornek-veri) kullanılır; yazma yalnız urun\_gecici ve urun\deneme-proje altına.
# Çalıştırma: powershell -NoProfile -File test\e2e.ps1 [-Exe "<yayin>\soz.exe"]   (-Command ile değil: stdin kodlaması farklı olur)
# Yayın exe'siyle DOTNET_ROOT gerekmez (kendi çalışma zamanını taşır).
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

$data = Join-Path $urun ("_gecici\e2e-veri-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$demo = Join-Path $urun "deneme-proje"
$claude = Join-Path $urun "test\ornek-veri\claude\projects"
$codex = Join-Path $urun "test\ornek-veri\codex\sessions"
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "GEÇTİ  $name $detail" } else { Write-Host "KALDI  $name $detail"; $script:fail++ }
}
function Soz { & $Exe @args --data $data }

try {
    New-Item -ItemType Directory -Force -Path $data | Out-Null

    # 1. İçe al
    $imp = Soz import --claude $claude --codex $codex --json | Out-String | ConvertFrom-Json
    Check "içe alma: insan mesajı" ($imp.newUtterances -eq 12) "($($imp.newUtterances) mesaj)"
    Check "içe alma: öneri > 0" ($imp.newProposals -gt 0) "($($imp.newProposals) öneri)"

    # 2. Bir öneriyi onayla (iki projede söylenmiş genel kural)
    $props = Soz decisions --status proposed --json | Out-String | ConvertFrom-Json
    $general = $props | Where-Object { $_.projectName -eq "Genel" } | Select-Object -First 1
    Check "genel öneri var" ($null -ne $general)
    $text = $general.text
    Soz approve $general.id | Out-Null
    $after = Soz decisions --status approved --json | Out-String | ConvertFrom-Json
    Check "onaylandı" (@($after | Where-Object { $_.id -eq $general.id }).Count -eq 1) "(#$($general.id))"

    # 3. Paket metni içerir
    $packet = Soz packet --project $demo | Out-String
    Check "paket kararı içerir" ($packet.Contains($text))

    # 4. session-start kancası (sahte stdin)
    $stdin = @{ session_id = "e2e-oturum"; transcript_path = ""; cwd = $demo; hook_event_name = "SessionStart"; source = "startup" } | ConvertTo-Json -Compress
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $hookOut = $stdin | & $Exe hook session-start --data $data | Out-String
    $sw.Stop()
    $hj = $hookOut | ConvertFrom-Json
    Check "session-start JSON biçimi" ($hj.hookSpecificOutput.hookEventName -eq "SessionStart")
    Check "session-start kararı içerir" ($hj.hookSpecificOutput.additionalContext.Contains($text)) "($($sw.ElapsedMilliseconds) ms)"

    # 5. user-prompt-submit: onaylı kuralı yeniden söylemek tekrar uyarısı üretir; çıkış kodu 0
    $stdin2 = @{ session_id = "e2e-oturum"; cwd = $demo; hook_event_name = "UserPromptSubmit"; prompt = $text } | ConvertTo-Json -Compress
    $ups = $stdin2 | & $Exe hook user-prompt-submit --data $data | Out-String
    $code = $LASTEXITCODE
    Check "user-prompt-submit çıkış 0" ($code -eq 0)
    $uj = $ups | ConvertFrom-Json
    Check "user-prompt-submit tekrar uyarısı" ($uj.hookSpecificOutput.hookEventName -eq "UserPromptSubmit" -and $uj.hookSpecificOutput.additionalContext.Contains("daha önce"))

    # 6. Dışa aktar → AGENTS.md bloğu
    Soz export --project-dir $demo | Out-Null
    $agents = Get-Content -Raw -Encoding UTF8 (Join-Path $demo "AGENTS.md")
    Check "AGENTS.md bloğu" ($agents.Contains("<!-- soz:basla -->") -and $agents.Contains("<!-- soz:bitir -->") -and $agents.Contains($text))

    # 7. Gerçek proje klasörüne dışa aktarma reddedilir (izin kapalı)
    $outside = Join-Path $urun "_gecici\e2e-gercek-proje-yok"
    $ErrorActionPreference = "Continue"
    & $Exe export --project-dir $outside --data $data 2>&1 | Out-Null
    $ErrorActionPreference = "Stop"
    Check "deneme dışı dışa aktarma reddedildi" ($LASTEXITCODE -ne 0 -and -not (Test-Path $outside))
}
catch {
    Write-Host "HATA: $($_.Exception.Message)"
    $fail++
}
finally {
    if (Test-Path $data) { Remove-Item -Recurse -Force $data }
}
if ($fail -eq 0) { Write-Host "E2E GEÇTİ"; exit 0 } else { Write-Host "E2E KALDI ($fail)"; exit 1 }
