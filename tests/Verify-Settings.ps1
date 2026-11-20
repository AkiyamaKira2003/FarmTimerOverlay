# Offline regression checks for timer and settings value validation.
# Requires only the C# compiler included with Windows .NET Framework 4.x.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64/v4.0.30319/csc.exe'
$wpf = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/WPF'
$exe = Join-Path $root 'FarmTimerOverlay.exe'

if (!(Test-Path -LiteralPath $csc)) {
    throw "Missing .NET Framework C# compiler: $csc"
}

Push-Location $root
try {
    $compileArgs = @(
        '/nologo', '/target:winexe', '/optimize+', '/win32icon:FarmTimerOverlay.ico',
        '/out:FarmTimerOverlay.exe',
        "/r:$wpf/PresentationCore.dll", "/r:$wpf/PresentationFramework.dll",
        "/r:$wpf/WindowsBase.dll", '/r:System.Xaml.dll',
        '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', 'FarmTimerOverlay.cs'
    )
    & $csc @compileArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Farm Timer Overlay build failed (exit code $LASTEXITCODE)"
    }
}
finally {
    Pop-Location
}

$assembly = [Reflection.Assembly]::LoadFrom($exe)
$settingsType = $assembly.GetType('FarmTimerOverlay.OverlaySettings', $true)
$timerType = $assembly.GetType('FarmTimerOverlay.TimerController', $true)
$soundType = $assembly.GetType('FarmTimerOverlay.LootAlertPlayer', $true)
$flags = [Reflection.BindingFlags]'NonPublic, Static'
$loadFromPath = $settingsType.GetMethod('LoadFromPath', $flags)
$clampFarm = $timerType.GetMethod('ClampFarmSeconds', $flags)
$clampVolume = $soundType.GetMethod('ClampVolume', $flags)

if (!$loadFromPath -or !$clampFarm -or !$clampVolume) {
    throw 'Missing settings parser or timer/sound clamp methods'
}

function Assert-Equal([object] $actual, [object] $expected, [string] $label) {
    if ($actual -ne $expected) {
        throw "$label : expected '$expected', got '$actual'"
    }
    Write-Output "PASS: $label"
}

function Test-Ini([string] $content) {
    $temp = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText($temp, $content, (New-Object System.Text.UTF8Encoding($false)))
        return $loadFromPath.Invoke($null, @($temp))
    }
    finally {
        [System.IO.File]::Delete($temp)
    }
}

$invalidLines = @('Left=Infinity','Top=-Infinity','Opacity=NaN','FarmSeconds=NaN','SoundAlertVolume=Infinity')
$invalid = Test-Ini ($invalidLines -join [Environment]::NewLine)
Assert-Equal ([double]::IsNaN($invalid.OverlayLeft)) $true 'Invalid left position remains unset'
Assert-Equal ([double]::IsNaN($invalid.OverlayTop)) $true 'Invalid top position remains unset'
Assert-Equal $invalid.Opacity 0.92 'Invalid opacity uses default'
Assert-Equal $invalid.FarmSeconds 105.0 'Invalid farm duration uses default'
Assert-Equal $invalid.SoundAlertVolume 1.0 'Invalid sound level uses default'

$validLines = @('Left=42.5','Top=13.25','Opacity=0.4','FarmSeconds=112','SoundAlertVolume=2','Custom=true')
$valid = Test-Ini ($validLines -join [Environment]::NewLine)
Assert-Equal $valid.OverlayLeft 42.5 'Valid left position is preserved'
Assert-Equal $valid.OverlayTop 13.25 'Valid top position is preserved'
Assert-Equal $valid.Opacity 0.55 'Opacity is clamped to safe minimum'
Assert-Equal $valid.FarmSeconds 110.0 'Farm duration is rounded to 5 seconds'
Assert-Equal $valid.SoundAlertVolume 1.0 'Sound level is clamped to safe maximum'
Assert-Equal $valid.CustomMode $true 'Existing boolean settings still load'

Assert-Equal $clampFarm.Invoke($null, @([double]::NaN)) 105.0 'Timer ignores NaN farm duration'
Assert-Equal $clampFarm.Invoke($null, @([double]::PositiveInfinity)) 105.0 'Timer ignores infinite farm duration'
Assert-Equal $clampFarm.Invoke($null, @([double]::NegativeInfinity)) 105.0 'Timer ignores negative infinite farm duration'
Assert-Equal $clampVolume.Invoke($null, @([double]::NaN)) 0.0 'Sound safely silences NaN volume'
Assert-Equal $clampVolume.Invoke($null, @([double]::PositiveInfinity)) 0.0 'Sound safely silences infinite volume'

Write-Output 'All Farm Timer Overlay settings regressions passed.'
