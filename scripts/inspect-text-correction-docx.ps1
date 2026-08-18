param([Parameter(Mandatory = $true)][string]$Path)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path))
try {
  $entry = $archive.GetEntry('word/document.xml')
  if ($null -eq $entry) { throw 'document part missing' }
  $reader = [System.IO.StreamReader]::new($entry.Open())
  try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
  $manager = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
  $manager.AddNamespace('w', 'http://schemas.openxmlformats.org/wordprocessingml/2006/main')
  $paragraphs = @($xml.SelectNodes('//w:body//w:p', $manager))
  $paragraphTexts = foreach ($paragraph in $paragraphs) {
    (($paragraph.SelectNodes('.//w:t', $manager) | ForEach-Object { $_.InnerText }) -join '')
  }
  $allText = $paragraphTexts -join "`n"
  function Count-Exact([string]$Value, [string]$Needle) {
    $count = 0; $offset = 0
    while (($found = $Value.IndexOf($Needle, $offset, [StringComparison]::Ordinal)) -ge 0) {
      $count += 1; $offset = $found + $Needle.Length
    }
    return $count
  }
  $hyperlinkTexts = @($xml.SelectNodes('//w:hyperlink', $manager) | ForEach-Object {
    (($_.SelectNodes('.//w:t', $manager) | ForEach-Object { $_.InnerText }) -join '')
  })
  [ordered]@{
    packageValid = $true
    paragraphCount = $paragraphs.Count
    runCount = @($xml.SelectNodes('//w:body//w:r', $manager)).Count
    pageBreakCount = @($xml.SelectNodes('//w:br[@w:type="page"]', $manager)).Count
    remainingSourceCount = Count-Exact $allText 'di analisa'
    suggestionCount = Count-Exact $allText 'dianalisis'
    manualCount = Count-Exact $allText 'dianalisis secara manual'
    hyperlinkSourceCount = @($hyperlinkTexts | Where-Object { $_ -eq 'di analisa' }).Count
  } | ConvertTo-Json -Compress
} finally {
  $archive.Dispose()
}
