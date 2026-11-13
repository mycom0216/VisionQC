<#
  VisionQC 배포 폴더 만들기.
    .\tools\publish.ps1                    # 프레임워크 종속(대상 PC에 .NET 10 Desktop Runtime 필요), 크기 작음
    .\tools\publish.ps1 -SelfContained     # 런타임 포함(대상 PC에 .NET 설치 불필요), 크기 큼
  결과: publish\ 폴더(빌드 산출물만 — 모델·DB·설정·데이터셋은 포함하지 않는다)
  이 스크립트는 코드 서명을 하지 않는다. 서명되지 않은 exe는 Windows SmartScreen 경고가 뜰 수 있다.
#>
param(
    [string]$Output = "publish",
    [switch]$SelfContained
)
$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

$sc = if ($SelfContained) { "true" } else { "false" }
dotnet publish VisionQC.App -c Release -r win-x64 --self-contained $sc -o $Output
if ($LASTEXITCODE -ne 0) { throw "publish 실패" }

# 필수 네이티브 라이브러리가 들어갔는지 확인(없으면 실행 시 영상 처리·추론에서 죽는다)
$required = @("VisionQC.App.exe", "OpenCvSharpExtern.dll", "onnxruntime.dll")
$missing = $required | Where-Object { -not (Get-ChildItem $Output -Recurse -Filter $_ -ErrorAction SilentlyContinue) }
if ($missing) { throw "배포 폴더에 필수 파일이 없습니다: $($missing -join ', ')" }

$size = (Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
"배포 완료: $((Resolve-Path $Output).Path)  ({0:N0} MB, 파일 {1}개, 자체 포함: {2})" -f $size, (Get-ChildItem $Output -Recurse -File).Count, $SelfContained.IsPresent
"모델(.onnx)은 포함되지 않았습니다. 설정 탭에서 지정하세요."
