param([string]$Revision = 'HEAD')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$destination = Join-Path $PSScriptRoot 'obj/baseline'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$sources = @('KeySimulator.cs','InputTracker.cs','SettingsManager.cs','MidiNoteHelper.cs','MidiListener.cs','HotkeyManager.cs','HotkeyConfigDialog.cs','DiagnosticLogger.cs','MidiDeviceDiagnostics.cs','WinRtMidiWatcher.cs')
foreach ($source in $sources) {
    $content = & git -C $repo show "${Revision}:$source"
    if ($LASTEXITCODE -ne 0) { throw "git show failed: ${Revision}:$source" }
    [System.IO.File]::WriteAllLines((Join-Path $destination $source), [string[]]$content, [System.Text.UTF8Encoding]::new($false))
}
& git -C $repo rev-parse $Revision
if ($LASTEXITCODE -ne 0) { throw "Invalid revision: $Revision" }
