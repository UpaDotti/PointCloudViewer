<#
.SYNOPSIS
  Unity CLI で計測用プレイヤーをビルドし、描画モードごとに自動計測して結果を並べる。

.EXAMPLE
  # ビルドしてから Direct と Compute を計測する
  powershell -ExecutionPolicy Bypass -File Tools/Run-Benchmark.ps1 -Build

.EXAMPLE
  # ビルド済みのプレイヤーで Compute だけ計測する
  powershell -ExecutionPolicy Bypass -File Tools/Run-Benchmark.ps1 -Modes Compute
#>
param(
    # 先にプレイヤーをビルドする（コードやシェーダを変えた後は付ける）
    [switch]$Build,
    # 計測する描画モード（OpcLoader.RenderMode の名前）
    [string[]]$Modes = @("Direct", "Compute"),
    # 計測するOPCファイル（既定はプロジェクトの Assets/sample.opc）
    [string]$OpcPath = "",
    # 画面サイズ（計測条件をそろえるため固定する）
    [int]$Width = 1920,
    [int]$Height = 1080,
    # 1モードあたりの待ち時間の上限（秒）。超えたらプレイヤーを止める
    [int]$TimeoutSec = 300
)

$ErrorActionPreference = "Stop"

# パスの組み立て（このスクリプトは <プロジェクト>/Tools に置く）
$projectPath = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $projectPath "Builds/Benchmark"
$playerPath = Join-Path $outputDir "PointCloudViewer.exe"
if ($OpcPath -eq "") { $OpcPath = Join-Path $projectPath "Assets/sample.opc" }

# ProjectVersion.txt から、Unity Hub 既定の場所にあるエディタを探す
function Get-UnityEditorPath {
    $versionFile = Join-Path $projectPath "ProjectSettings/ProjectVersion.txt"
    $versionLine = Get-Content $versionFile -Encoding UTF8 | Where-Object { $_ -like "m_EditorVersion:*" } | Select-Object -First 1
    $version = ($versionLine -split ":")[1].Trim()
    $editorPath = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe"
    if (-not (Test-Path $editorPath)) { throw "Unity $version が見つかりません: $editorPath" }
    return $editorPath
}

# Unity CLI でプレイヤーをビルドする（エディタが同じプロジェクトを開いていると失敗する）
function Invoke-PlayerBuild {
    $lockFile = Join-Path $projectPath "Temp/UnityLockfile"
    if (Test-Path $lockFile) {
        try { [System.IO.File]::Open($lockFile, "Open", "ReadWrite", "None").Close() }
        catch { throw "Unity エディタがこのプロジェクトを開いています。エディタを閉じてから -Build を付けて実行してください。" }
    }

    New-Item -ItemType Directory -Force $outputDir | Out-Null
    $buildLog = Join-Path $outputDir "build.log"
    Write-Host "ビルド中... (ログ: $buildLog)"

    $unity = Get-UnityEditorPath
    $arguments = @("-batchmode", "-quit", "-projectPath", "`"$projectPath`"",
                   "-executeMethod", "BenchmarkPlayerBuilder.Build", "-logFile", "`"$buildLog`"")
    $process = Start-Process -FilePath $unity -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "ビルドに失敗しました（終了コード $($process.ExitCode)）。$buildLog を確認してください。" }
    Write-Host "ビルド完了: $playerPath"
}

# 1モードを計測し、ログから BenchmarkSession の統計表を取り出す
function Invoke-Measurement([string]$mode) {
    $runLog = Join-Path $outputDir "run-$mode.log"
    Write-Host "計測中: $mode (ログ: $runLog)"

    $arguments = @("-benchmark", "-renderMode", $mode, "-opcPath", "`"$OpcPath`"",
                   "-screen-fullscreen", "0", "-screen-width", "$Width", "-screen-height", "$Height",
                   "-logFile", "`"$runLog`"")
    $process = Start-Process -FilePath $playerPath -ArgumentList $arguments -PassThru

    # 終わらない時は止める（計測結果は出ない）
    if (-not $process.WaitForExit($TimeoutSec * 1000)) {
        $process.Kill()
        return "($mode) ${TimeoutSec}秒で終わらなかったため止めました。$runLog を確認してください。"
    }

    # 統計表は "=== BenchmarkSession" の行から空行の手前まで
    $lines = Get-Content $runLog -Encoding UTF8
    $start = [Array]::FindIndex([string[]]$lines, [Predicate[string]]{ param($line) $line -like "=== BenchmarkSession*" })
    if ($start -lt 0) { return "($mode) 計測結果がログにありません。$runLog を確認してください。" }

    $table = @("--- $mode ---")
    for ($i = $start; $i -lt $lines.Count -and $lines[$i].Trim() -ne ""; $i++) { $table += $lines[$i] }
    return ($table -join "`n")
}

if ($Build) { Invoke-PlayerBuild }
if (-not (Test-Path $playerPath)) { throw "プレイヤーがありません。-Build を付けて実行してください: $playerPath" }
if (-not (Test-Path $OpcPath)) { throw "OPCファイルがありません: $OpcPath" }

# 各モードを順に計測し、まとめてファイルにも残す
$results = foreach ($mode in $Modes) { Invoke-Measurement $mode }
$summary = ($results -join "`n`n")
$summaryPath = Join-Path $outputDir "summary.txt"
Set-Content -Path $summaryPath -Value $summary -Encoding UTF8
Write-Host ""
Write-Host $summary
Write-Host ""
Write-Host "結果: $summaryPath"
