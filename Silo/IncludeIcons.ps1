[CmdletBinding()]
param (
    [string]$SourceDir = ".\Resources",
    [string]$OutputFile = ".\Paths.xaml",
    # Replace icons that already exist in $OutputFile (needed once to re-generate icons that were
    # converted before the 'first moveto is absolute' fix below).
    [switch]$Force
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $SourceDir)) {
    throw "Source directory '$SourceDir' does not exist."
}

$svgFiles = Get-ChildItem -Path $SourceDir -Filter "*.svg" -File
if ($svgFiles.Count -eq 0) {
    Write-Host "No SVG files found in $SourceDir."
    return
}

$nsPresentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
$nsX = "http://schemas.microsoft.com/winfx/2006/xaml"

if (Test-Path $OutputFile) {
    [xml]$xamlDoc = Get-Content -Path $OutputFile -Raw
    $root = $xamlDoc.ResourceDictionary
    if (-not $root) {
        throw "$OutputFile exists but is not a valid ResourceDictionary."
    }
} else {
    [xml]$xamlDoc = @"
<ResourceDictionary xmlns="$nsPresentation"
                    xmlns:x="$nsX">
</ResourceDictionary>
"@
    $root = $xamlDoc.ResourceDictionary
}

$nsManager = New-Object System.Xml.XmlNamespaceManager($xamlDoc.NameTable)
$nsManager.AddNamespace("res", $nsPresentation)
$nsManager.AddNamespace("x", $nsX)

$textInfo = (Get-Culture).TextInfo

# Every SVG <path> starts at the origin: its first "m" is ABSOLUTE even though it is lower case.
# When several paths are joined into one geometry that is no longer true - a lower-case "m" would
# be read relative to the end of the previous path, shifting the whole second piece. Rewrite the
# first moveto as an absolute "M" (the extra coordinate pairs after it stay relative: "l").
$num = '[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?'
function ConvertTo-AbsoluteStart([string]$d) {
    $m = [regex]::Match($d.Trim(), "^[mM]\s*($num)[\s,]*($num)(.*)$", 'Singleline')
    if (-not $m.Success) { return $d.Trim() }
    $rest = $m.Groups[3].Value
    if ($rest -match "^[\s,]*[-+.0-9]") {
        $rest = " " + $(if ($d.Trim()[0] -ceq 'm') { 'l' } else { 'L' }) + $rest.TrimStart(' ', ',')
    }
    return "M$($m.Groups[1].Value) $($m.Groups[2].Value)$rest"
}

foreach ($file in $svgFiles) {
    $cleanName = $file.BaseName -replace '[^a-zA-Z0-9_]', ' '
    $titleCased = $textInfo.ToTitleCase($cleanName)
    $keyName = "Icon" + ($titleCased -replace '\s+', '')

    $existing = $root.SelectSingleNode("*[@x:Key='$keyName']", $nsManager)
    if ($existing) {
        if (-not $Force) {
            Write-Host "Skipping duplicate key: $keyName"
            continue
        }
        $root.RemoveChild($existing) | Out-Null
    }

    $content = Get-Content -Path $file.FullName -Raw
    $pathMatches = [regex]::Matches($content, '(?i)<path[^>]*?\sd=["'']([^"'']+)["'']')

    if ($pathMatches.Count -eq 0) {
        Write-Warning "No <path> with 'd' attribute found in $($file.Name)"
        continue
    }

    if ($content -match '(?i)\stransform\s*=') {
        Write-Warning "$($file.Name) uses a transform attribute, which is not carried over - the icon may be mis-positioned."
    }
    if ($content -match '(?i)<(circle|rect|ellipse|line|polygon|polyline)\b') {
        Write-Warning "$($file.Name) contains basic shapes (circle/rect/...) that are not converted - parts of the icon will be missing."
    }

    $combinedData = ($pathMatches | ForEach-Object { ConvertTo-AbsoluteStart $_.Groups[1].Value }) -join ' '

    # SVG's default fill rule is nonzero; WPF's is even-odd. Say so explicitly (F1 = nonzero).
    if ($content -notmatch '(?i)(fill|clip)-rule\s*=\s*["'']evenodd') { $combinedData = "F1 $combinedData" }

    $elem = $xamlDoc.CreateElement("StreamGeometry", $nsPresentation)
    $keyAttr = $xamlDoc.CreateAttribute("x", "Key", $nsX)
    $keyAttr.Value = $keyName
    $elem.Attributes.Append($keyAttr) | Out-Null
    $elem.InnerText = $combinedData

    $root.AppendChild($elem) | Out-Null
    Write-Host "Added: $keyName ($($file.Name))"
}

$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputFile)

$settings = New-Object System.Xml.XmlWriterSettings
$settings.Indent = $true
$settings.IndentChars = "    "
$settings.OmitXmlDeclaration = $true

$writer = [System.Xml.XmlWriter]::Create($resolvedOutput, $settings)
$xamlDoc.Save($writer)
$writer.Dispose()

Write-Host "Successfully updated $OutputFile"