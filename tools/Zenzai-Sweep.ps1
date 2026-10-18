# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Serugu
#
# zenz のヘルパーの --bench を、設定 (ヘルパーに渡す引数) と評価データ (tools/zenzai-eval.py で作ったもの) を変えて走らせ、
# 正解数を表にする。括弧の中は Mozc だけのときの正解数。
#
#   .\tools\Zenzai-Sweep.ps1 -Configs '', '--beam 4', '--rank-weight 1'   (PowerShell の中で実行する。-File で渡すと一覧が 1 つの文字列になる)
param(
    [string]$Data = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\zenzai-eval'),
    [string[]]$Configs = @(''),
    [string[]]$Sets = @('ajimee-tune', 'ajimee-test', 'zenzai-cases', 'zenzai-holdout'),
    [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Meltype.Zenz\bin\Release\net10.0\Meltype.Zenz.exe')
)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$Sets = $Sets | Where-Object { Test-Path -LiteralPath (Join-Path $Data "$_.tsv") }
$header = '{0,-40}' -f 'config'
foreach ($s in $Sets) { $header += ' {0,16}' -f $s }
$header
foreach ($c in $Configs) {
    $line = '{0,-40}' -f $(if ($c) { $c } else { '(既定)' })
    foreach ($s in $Sets) {
        $arguments = @('--bench', (Join-Path $Data "$s.tsv")) + ($c -split ' ' | Where-Object { $_ })
        $out = & $Exe @arguments 2>$null
        $m = ($out | Select-String '正解: zenz (\d+)/(\d+)\s+Mozc (\d+)/').Matches
        $line += ' {0,16}' -f $(if ($m) { "$($m[0].Groups[1].Value)/$($m[0].Groups[2].Value) ($($m[0].Groups[3].Value))" } else { '?' })
    }
    $line
}
