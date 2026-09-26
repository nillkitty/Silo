[CmdletBinding()]
param (
    [string]$SourceDir = ".\Resources",
    [string]$OutputFile = ".\Paths.xaml"
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

foreach ($file in $svgFiles) {
    $cleanName = $file.BaseName -replace '[^a-zA-Z0-9_]', ' '
    $titleCased = $textInfo.ToTitleCase($cleanName)
    $keyName = "Icon" + ($titleCased -replace '\s+', '')

    $existing = $root.SelectSingleNode("*[@x:Key='$keyName']", $nsManager)
    if ($existing) {
        Write-Host "Skipping duplicate key: $keyName"
        continue
    }

    $content = Get-Content -Path $file.FullName -Raw
    $pathMatches = [regex]::Matches($content, '(?i)<path[^>]*?\sd=["'']([^"'']+)["'']')

    if ($pathMatches.Count -eq 0) {
        Write-Warning "No <path> with 'd' attribute found in $($file.Name)"
        continue
    }

    $combinedData = ($pathMatches | ForEach-Object { $_.Groups[1].Value.Trim() }) -join ' '

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