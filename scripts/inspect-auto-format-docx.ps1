param(
    [Parameter(Mandatory = $true)]
    [string] $Path
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Xml.Linq

function Read-EntryBytes([System.IO.Compression.ZipArchive] $Archive, [string] $Name) {
    $entry = $Archive.GetEntry($Name)
    if ($null -eq $entry) { throw "Required DOCX entry is missing." }
    $stream = $entry.Open()
    try {
        $memory = New-Object System.IO.MemoryStream
        try { $stream.CopyTo($memory); return $memory.ToArray() }
        finally { $memory.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Sha256([byte[]] $Bytes) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace("-", "").ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}

function Attribute-Value($Element, [System.Xml.Linq.XNamespace] $Namespace, [string] $Name) {
    if ($null -eq $Element) { return $null }
    $attribute = $Element.Attribute($Namespace + $Name)
    if ($null -eq $attribute) { return $null }
    return $attribute.Value
}

$resolved = (Resolve-Path -LiteralPath $Path).Path
$archive = [System.IO.Compression.ZipFile]::OpenRead($resolved)
try {
    $documentBytes = Read-EntryBytes $archive "word/document.xml"
    $relationshipsBytes = Read-EntryBytes $archive "word/_rels/document.xml.rels"
    $document = [System.Xml.Linq.XDocument]::Parse([System.Text.Encoding]::UTF8.GetString($documentBytes))
    [System.Xml.Linq.XNamespace] $w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"

    $paragraphs = @($document.Descendants($w + "p"))
    if ($paragraphs.Count -lt 1) { throw "DOCX has no paragraphs." }
    $textBuilder = New-Object System.Text.StringBuilder
    foreach ($paragraph in $paragraphs) {
        foreach ($text in $paragraph.Descendants($w + "t")) { [void] $textBuilder.Append($text.Value) }
        [void] $textBuilder.Append([char] 0)
    }
    $textFingerprint = Sha256 ([System.Text.Encoding]::UTF8.GetBytes($textBuilder.ToString()))

    $first = $paragraphs[0]
    $properties = $first.Element($w + "pPr")
    $spacing = if ($null -eq $properties) { $null } else { $properties.Element($w + "spacing") }
    $indent = if ($null -eq $properties) { $null } else { $properties.Element($w + "ind") }
    $justification = if ($null -eq $properties) { $null } else { $properties.Element($w + "jc") }
    $runs = @()
    foreach ($run in @($first.Descendants($w + "r"))) {
        $runProperties = $run.Element($w + "rPr")
        $fonts = if ($null -eq $runProperties) { $null } else { $runProperties.Element($w + "rFonts") }
        $size = if ($null -eq $runProperties) { $null } else { $runProperties.Element($w + "sz") }
        $underline = if ($null -eq $runProperties) { $null } else { $runProperties.Element($w + "u") }
        $runs += [ordered]@{
            parent = $run.Parent.Name.LocalName
            fontAscii = Attribute-Value $fonts $w "ascii"
            fontHighAnsi = Attribute-Value $fonts $w "hAnsi"
            size = Attribute-Value $size $w "val"
            bold = $null -ne $runProperties -and $null -ne $runProperties.Element($w + "b")
            italic = $null -ne $runProperties -and $null -ne $runProperties.Element($w + "i")
            underline = Attribute-Value $underline $w "val"
        }
    }

    [ordered]@{
        packageValid = $true
        entryCount = @($archive.Entries).Count
        entryNamesHash = Sha256 ([System.Text.Encoding]::UTF8.GetBytes((@($archive.Entries | ForEach-Object FullName | Sort-Object) -join "`n")))
        relationshipsHash = Sha256 $relationshipsBytes
        textFingerprint = $textFingerprint
        paragraphCount = $paragraphs.Count
        firstParagraph = [ordered]@{
            line = Attribute-Value $spacing $w "line"
            lineRule = Attribute-Value $spacing $w "lineRule"
            before = Attribute-Value $spacing $w "before"
            after = Attribute-Value $spacing $w "after"
            firstLine = Attribute-Value $indent $w "firstLine"
            hanging = Attribute-Value $indent $w "hanging"
            left = Attribute-Value $indent $w "left"
            right = Attribute-Value $indent $w "right"
            alignment = Attribute-Value $justification $w "val"
            runs = $runs
        }
    } | ConvertTo-Json -Depth 8 -Compress
}
finally {
    $archive.Dispose()
}
