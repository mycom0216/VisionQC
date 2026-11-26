# PowerPoint가 설치된 Windows에서 .pptx 를 PDF 와 슬라이드별 PNG(미리보기)로 내보냅니다.
# 사용법: powershell -File tools\pptx2pdf.ps1 docs\MeetMate_발표자료.pptx [미리보기폴더]
param([string]$File, [string]$PngDir = "")
$app = New-Object -ComObject PowerPoint.Application
try {
  $src = (Resolve-Path $File).Path
  $pres = $app.Presentations.Open($src, $true, $false, $false)
  $pres.SaveAs([IO.Path]::ChangeExtension($src, '.pdf'), 32)  # 32 = ppSaveAsPDF
  Write-Host "saved $([IO.Path]::ChangeExtension($src, '.pdf'))"
  if ($PngDir) { New-Item -ItemType Directory -Force $PngDir | Out-Null; $pres.Export((Resolve-Path $PngDir).Path, 'PNG', 1280, 720) }
  $pres.Close()
} finally { $app.Quit() }
