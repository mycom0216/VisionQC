# Word가 설치된 Windows에서 .docx/.pptx 를 PDF로 변환합니다.
# 사용법: powershell -File tools\docx2pdf.ps1 docs\StudyMate_기획서.docx [docs\other.docx ...]
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Files)
$word = New-Object -ComObject Word.Application
$word.Visible = $false
try {
  foreach ($f in $Files) {
    $src = (Resolve-Path $f).Path
    $dst = [IO.Path]::ChangeExtension($src, '.pdf')
    $doc = $word.Documents.Open($src, $false, $true)
    $doc.ExportAsFixedFormat($dst, 17)   # 17 = wdExportFormatPDF
    $doc.Close($false)
    Write-Host "saved $dst"
  }
} finally { $word.Quit() }
