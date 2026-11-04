param([string]$Version = '1.0.0')

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    Write-Host "== $Name"
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name that bai (exit $LASTEXITCODE)" }
}

Invoke-Step 'dotnet test' { dotnet test DoMa.sln }
Invoke-Step 'npm test (node --test)' { Push-Location extension; try { npm test } finally { Pop-Location } }

if (Test-Path artifacts/app) { Remove-Item artifacts/app -Recurse -Force }
Invoke-Step 'dotnet publish' {
    dotnet publish src/DoMa.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:Version=$Version -o artifacts/app
}

$iscc = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) {
    throw 'Khong tim thay ISCC.exe (Inno Setup 6). Test va publish da xong; cai Inno Setup roi chay lai build.ps1.'
}

$extensionId = (Get-Content installer/extension-id.txt -Raw).Trim()
Invoke-Step 'ISCC' { & $iscc "/DExtensionId=$extensionId" "/DAppVersion=$Version" installer/DoMa.iss }

$setup = "artifacts/DoMa-Setup-$Version.exe"
Write-Host "SHA-256 $setup"
(Get-FileHash $setup -Algorithm SHA256).Hash
