# Verifies that every assembly in the given NuGet packages is fully strong-name signed with the CodeLogic key:
# the expected public key token, the StrongNameSigned flag, and a non-empty signature (a public-signed assembly
# has an all-zero signature). Used by release.yml before anything is pushed. Exit code 1 on any problem.
#
#   pwsh build/verify-strong-name.ps1 -Packages artifacts/*.nupkg
param(
    [Parameter(Mandatory)] [string[]] $Packages,
    [string] $ExpectedToken = '53a89305eedc5d98'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$failures = 0
$checked = 0

foreach ($package in ($Packages | ForEach-Object { Get-Item $_ })) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        foreach ($entry in $zip.Entries | Where-Object { $_.FullName -like '*.dll' }) {
            $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString() + '.dll')
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $tmp)
            try {
                $token = -join ([System.Reflection.AssemblyName]::GetAssemblyName($tmp).GetPublicKeyToken() | ForEach-Object { $_.ToString('x2') })
                $bytes = [System.IO.File]::ReadAllBytes($tmp)
                $stream = [System.IO.MemoryStream]::new($bytes)
                $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
                try {
                    $cor = $pe.PEHeaders.CorHeader
                    $flagged = ($cor.Flags -band [System.Reflection.PortableExecutable.CorFlags]::StrongNameSigned) -ne 0
                    $dir = $cor.StrongNameSignatureDirectory
                    $offset = 0
                    $real = $false
                    if ($dir.Size -gt 0 -and $pe.PEHeaders.TryGetDirectoryOffset($dir, [ref] $offset)) {
                        for ($i = $offset; $i -lt $offset + $dir.Size; $i++) { if ($bytes[$i] -ne 0) { $real = $true; break } }
                    }
                } finally { $pe.Dispose() }

                $checked++
                $name = "$($package.Name)!$($entry.FullName)"
                if ($token -ne $ExpectedToken) { Write-Host "::error::$name has public key token '$token', expected '$ExpectedToken'"; $failures++ }
                elseif (-not $flagged -or -not $real) { Write-Host "::error::$name is not fully signed (public-signed or unsigned)"; $failures++ }
                else { Write-Host "signed: $name ($token)" }
            } finally { Remove-Item $tmp -Force }
        }
    } finally { $zip.Dispose() }
}

if ($checked -eq 0) { Write-Host '::error::no assemblies found to verify'; exit 1 }
if ($failures -gt 0) { Write-Host "::error::$failures assembly(ies) not signed with the CodeLogic key - refusing to publish"; exit 1 }
Write-Host "strong-name OK: $checked assembly(ies)"
