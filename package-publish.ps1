param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$Archive
)

$ErrorActionPreference = 'Stop'
$exePath = [System.IO.Path]::GetFullPath($Executable)
$archivePath = [System.IO.Path]::GetFullPath($Archive)
if (-not [System.IO.File]::Exists($exePath)) {
    throw "Published executable was not found: $exePath"
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archiveDir = [System.IO.Path]::GetDirectoryName($archivePath)
[System.IO.Directory]::CreateDirectory($archiveDir) | Out-Null
$temporaryPath = [System.IO.Path]::Combine($archiveDir, ".midi2key-$([guid]::NewGuid().ToString('N')).zip")
try {
    $zip = [System.IO.Compression.ZipFile]::Open($temporaryPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $exePath, 'midi2key.exe', [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    finally {
        $zip.Dispose()
    }
    # The pre-publish target removes a stale archive before compilation starts.
    [System.IO.File]::Move($temporaryPath, $archivePath)
}
finally {
    if ([System.IO.File]::Exists($temporaryPath)) {
        [System.IO.File]::Delete($temporaryPath)
    }
}
