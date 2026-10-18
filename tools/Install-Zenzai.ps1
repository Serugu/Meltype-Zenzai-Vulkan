# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Serugu
#
# インストール済みの Meltype (%LOCALAPPDATA%\Programs\Meltype) を、Zenzai を足したこのリポジトリのビルドに差し替える。
#   - Meltype.dll / Meltype.Core.dll を入れ替える (前の DLL は %LOCALAPPDATA%\Meltype\zenzai-backup\ に控える)
#   - zenz のヘルパー (src\Meltype.Zenz、自己完結) を zenz\ に置く
#   - zenz の重み (zenz-v3.2-small Q5_K_M、Apache-2.0) が無ければ Hugging Face から取ってきて SHA-256 を確かめる
#   - -Enable で設定の Zenzai をオンにし、自動更新をオフにする
# 管理者権限は要らない (Meltype IME の DLL は入れ替えない)。
#
#   powershell -ExecutionPolicy Bypass -File .\tools\Install-Zenzai.ps1 -Enable
#   powershell -ExecutionPolicy Bypass -File .\tools\Install-Zenzai.ps1 -Restore   # 控えた DLL に戻し、zenz\ をごみ箱へ
param([switch]$Enable, [switch]$Restore)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $env:LOCALAPPDATA 'Programs\Meltype'
$data = Join-Path $env:LOCALAPPDATA 'Meltype'
$backup = Join-Path $data 'zenzai-backup\original'
$config = Join-Path $data 'config.json'
$modelDir = Join-Path $data 'zenz'
$model = Join-Path $modelDir 'zenz-v3.2-small-Q5_K_M.gguf'
$modelUrl = 'https://huggingface.co/Miwa-Keita/zenz-v3.2-small-gguf/resolve/main/ggml-model-Q5_K_M.gguf'
$modelSha256 = '29C223D4C23327B80FD13EBB5AB2555057A46317997D5DA391584FFBEF0DB673'
$dlls = 'Meltype.dll', 'Meltype.Core.dll'

if (-not (Test-Path -LiteralPath (Join-Path $app 'Meltype.exe'))) { throw "Meltype が入っていません ($app)。先に公式のインストーラーで入れてください。" }

function Stop-Meltype {
    Get-Process Meltype, Meltype.Zenz, meltype_mozc_helper -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

function Start-Meltype {
    Start-Process -FilePath (Join-Path $app 'Meltype.exe') -WorkingDirectory $app
}

function Send-ToRecycleBin([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    Add-Type -AssemblyName Microsoft.VisualBasic
    [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($path, 'OnlyErrorDialogs', 'SendToRecycleBin')
}

if ($Restore) {
    if (-not (Test-Path -LiteralPath $backup)) { throw "控えがありません ($backup)。" }
    Stop-Meltype
    foreach ($dll in $dlls) { Copy-Item -LiteralPath (Join-Path $backup $dll) -Destination $app -Force }
    Send-ToRecycleBin (Join-Path $app 'zenz')
    Start-Meltype
    Write-Host '公式の DLL に戻しました (zenz\ はごみ箱へ送りました。重みは残してあります)。'
    return
}

# ビルド (Meltype 本体は外部のパッケージを使わない。ヘルパーだけ nuget.org から LLamaSharp を取る)
# 置き場所は dist\zenzai\ (Git には入れない)。ヘルパーは前の発行のファイルが残らないよう、前のものをごみ箱へ送ってから発行する
$stage = Join-Path $root 'dist\zenzai'
dotnet publish (Join-Path $root 'src\Meltype\Meltype.csproj') -c Release -o (Join-Path $stage 'app') -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'Meltype をビルドできませんでした。' }
Send-ToRecycleBin (Join-Path $stage 'zenz')
dotnet publish (Join-Path $root 'src\Meltype.Zenz\Meltype.Zenz.csproj') -c Release -r win-x64 --self-contained -o (Join-Path $stage 'zenz') -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'zenz のヘルパーをビルドできませんでした。' }

# 重み
if (-not (Test-Path -LiteralPath $model) -or (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash -ne $modelSha256) {
    New-Item -ItemType Directory -Force -Path $modelDir | Out-Null
    Write-Host "zenz の重みを取ってきます: $modelUrl"
    $partial = "$model.download"
    Invoke-WebRequest -Uri $modelUrl -OutFile $partial -UseBasicParsing
    if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $modelSha256) {
        Remove-Item -LiteralPath $partial
        throw 'zenz の重みの SHA-256 が合いません。'
    }
    Move-Item -LiteralPath $partial -Destination $model -Force
}

Stop-Meltype
# 入っている Meltype.dll が公式のもの (版に zenzai が付いていない) なら、控えを取り直す
# (公式の新しい版を入れてから入れ直したときに、-Restore で古い版の DLL に戻さないように。前の控えはごみ箱へ)
$installed = (Get-Item -LiteralPath (Join-Path $app 'Meltype.dll')).VersionInfo.ProductVersion
if ($installed -notmatch 'zenzai') {
    Send-ToRecycleBin $backup
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    foreach ($dll in $dlls) { Copy-Item -LiteralPath (Join-Path $app $dll) -Destination $backup }
    Write-Host "公式の DLL ($installed) を控えました: $backup"
}
foreach ($dll in $dlls) { Copy-Item -LiteralPath (Join-Path $stage "app\$dll") -Destination $app -Force }
# 前の版のヘルパーはごみ箱へ送ってから置く (前の版にだけあったファイルを残さない)
$zenz = Join-Path $app 'zenz'
Send-ToRecycleBin $zenz
Copy-Item -LiteralPath (Join-Path $stage 'zenz') -Destination $zenz -Recurse

if ($Enable -and (Test-Path -LiteralPath $config)) {
    $settings = Get-Content -LiteralPath $config -Raw -Encoding UTF8 | ConvertFrom-Json
    $settings | Add-Member -NotePropertyName Zenzai -NotePropertyValue $true -Force
    $settings | Add-Member -NotePropertyName AutoUpdate -NotePropertyValue $false -Force
    [IO.File]::WriteAllText($config, ($settings | ConvertTo-Json -Depth 32), [Text.UTF8Encoding]::new($false))
}
Start-Meltype
Write-Host "Zenzai を入れました ($app)。$(if ($Enable) { '設定で Zenzai をオンにしました。' } else { 'トレイの「設定」で「Zenzai (ニューラル変換)」をオンにすると使えます。' })"
