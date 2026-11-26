$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $PSScriptRoot
$appPath = Join-Path $projectDir 'VisionQC.App/bin/Release/net10.0-windows/VisionQC.App.exe'
$runtimeDir = Join-Path $projectDir 'demo-runtime'
$modelPath = Join-Path $projectDir 'demo-assets/model_cam.onnx'
if (!(Test-Path -LiteralPath $appPath)) { throw 'Release 빌드가 필요합니다: dotnet build VisionQC.slnx -c Release' }
if (!(Test-Path -LiteralPath (Join-Path $runtimeDir 'demo.db')) -or !(Test-Path -LiteralPath $modelPath)) {
    throw '로컬 시연 DB·모델이 없습니다. 모델/데이터를 준비한 뒤 tools/DemoEvidence를 실행하세요.'
}
$launchArgs = @('--db', ('"' + (Join-Path $runtimeDir 'demo.db') + '"'),
    '--settings', ('"' + (Join-Path $runtimeDir 'settings.json') + '"'),
    '--backup-dir', ('"' + (Join-Path $runtimeDir 'backup') + '"'),
    '--capture-dir', ('"' + (Join-Path $runtimeDir 'captures') + '"'))
Start-Process -FilePath $appPath -ArgumentList $launchArgs -WindowStyle Normal
Write-Host '로컬 시연 계정: portfolio / Vision!2026* (운영용 사용 금지)'
