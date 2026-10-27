<#
  Runs after every build of Silo.csproj (see the SiloStubs target there).

  1. Makes Silo.com: a copy of the Silo.exe launcher whose PE "subsystem" is changed from Windows GUI (2) to
     Windows console (3). Both stubs start the same Silo.dll. As a console program, Silo.com gets a console
     when run from a command prompt, which therefore waits for it and can pipe its input and output.

  2. Removes the managed assemblies from Silo.deps.json. Costura has folded them into Silo.dll and deleted the
     files from the output folder, but the .NET host refuses to start if the dependency manifest lists an
     assembly that is not there. Native-library entries are kept: the host finds the native libraries in the
     `runtimes` folder through them.

  Both steps are repeatable: running them on an already processed output changes nothing.
#>
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string]$Name,
    # Debug builds do not run Costura, so the assemblies are still files and the manifest must keep listing them.
    [switch]$KeepDeps
)

$ErrorActionPreference = 'Stop'

# ---- 1. Silo.com -----------------------------------------------------------------------------------------
$exe = Join-Path $OutDir "$Name.exe"
$com = Join-Path $OutDir "$Name.com"

if (Test-Path -LiteralPath $exe) {
    $bytes = [System.IO.File]::ReadAllBytes($exe)

    $pe = [BitConverter]::ToInt32($bytes, 0x3C)                       # e_lfanew: where the PE header starts
    if ([BitConverter]::ToUInt32($bytes, $pe) -ne 0x00004550) {       # "PE\0\0"
        throw "$exe is not a PE file."
    }

    $subsystem = $pe + 24 + 68      # signature+COFF header (24), then the optional header's Subsystem field (68)
    $bytes[$subsystem]     = 3      # IMAGE_SUBSYSTEM_WINDOWS_CUI
    $bytes[$subsystem + 1] = 0

    $sha  = [System.Security.Cryptography.SHA256]::Create()
    $same = $false
    if (Test-Path -LiteralPath $com) {
        $same = [BitConverter]::ToString($sha.ComputeHash([System.IO.File]::ReadAllBytes($com))) -eq
                [BitConverter]::ToString($sha.ComputeHash($bytes))
    }
    if (-not $same) {
        [System.IO.File]::WriteAllBytes($com, $bytes)
    }
}

# ---- 2. Silo.deps.json -----------------------------------------------------------------------------------
$depsPath = Join-Path $OutDir "$Name.deps.json"

if (-not $KeepDeps -and (Test-Path -LiteralPath $depsPath)) {
    $deps    = [System.IO.File]::ReadAllText($depsPath) | ConvertFrom-Json
    $changed = $false

    foreach ($target in $deps.targets.PSObject.Properties) {
        foreach ($entry in $target.Value.PSObject.Properties) {
            $library = $deps.libraries.PSObject.Properties[$entry.Name]
            if ($null -ne $library -and $library.Value.type -eq 'project') { continue }   # Silo itself stays

            foreach ($section in 'runtime', 'resources') {
                if ($entry.Value.PSObject.Properties[$section]) {
                    $entry.Value.PSObject.Properties.Remove($section)
                    $changed = $true
                }
            }
        }
    }

    if ($changed) {
        $text = $deps | ConvertTo-Json -Depth 100
        [System.IO.File]::WriteAllText($depsPath, $text, (New-Object System.Text.UTF8Encoding($false)))
    }
}
