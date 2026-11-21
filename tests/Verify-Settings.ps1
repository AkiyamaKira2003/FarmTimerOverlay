# Offline regression checks for timer and settings value validation.
# Requires only the C# compiler included with Windows .NET Framework 4.x.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64/v4.0.30319/csc.exe'
$wpf = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/WPF'
$testFolder = Join-Path ([System.IO.Path]::GetTempPath()) ('FarmTimerOverlay-QA-' + [Guid]::NewGuid().ToString('N'))
$exe = Join-Path $testFolder 'FarmTimerOverlay-Tests.exe'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$verifiedFolder = [IO.Path]::GetFullPath($testFolder)
if ([IO.Path]::GetDirectoryName($verifiedFolder).TrimEnd('\') -ne $tempRoot -or
    [IO.Path]::GetFileName($verifiedFolder) -notmatch '^FarmTimerOverlay-QA-[a-f0-9]{32}$') {
    throw 'Unsafe regression build output directory'
}

if (!(Test-Path -LiteralPath $csc)) {
    throw "Missing .NET Framework C# compiler: $csc"
}

New-Item -ItemType Directory -Path $testFolder -ErrorAction Stop | Out-Null
try {
    Push-Location $root
    try {
        $compileArgs = @(
            '/nologo', '/target:winexe', '/optimize+', '/win32icon:FarmTimerOverlay.ico',
            "/out:$exe",
            "/r:$wpf/PresentationCore.dll", "/r:$wpf/PresentationFramework.dll",
            "/r:$wpf/WindowsBase.dll", '/r:System.Xaml.dll',
            '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', 'FarmTimerOverlay.cs'
        )
        & $csc @compileArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Farm Timer Overlay build failed (exit code $LASTEXITCODE)"
        }
        # Load a byte array, not the executable path, so it can be removed
        # without replacing or locking the user's existing portable EXE.
        $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($exe))
    }
    finally {
        Pop-Location
    }
}
finally {
    Remove-Item -LiteralPath $testFolder -Recurse -Force -ErrorAction Stop
}
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

# A virtual desktop's bounding rectangle can contain unoccupied space
# between vertically offset monitors. A saved overlay in that gap must be
# recovered to a real screen without constructing any WPF window.
$visibilityType = $assembly.GetType('FarmTimerOverlay.OverlayVisibility', $false)
if (!$visibilityType) {
    throw 'RED: missing per-monitor visibility check (virtual-screen gaps are accepted)'
}
$visibilityMethod = $visibilityType.GetMethod('HasReachableArea',
    [Reflection.BindingFlags]'NonPublic, Static')
if (!$visibilityMethod) {
    throw 'RED: missing per-monitor reachable-area method'
}
function Test-Visibility([System.Drawing.Rectangle] $window, [System.Drawing.Rectangle[]] $monitors) {
    $argsForReflection = New-Object 'object[]' 3
    $argsForReflection[0] = $window
    $argsForReflection[1] = $monitors
    $argsForReflection[2] = [int] 40
    return [bool] $visibilityMethod.Invoke($null, $argsForReflection)
}
$monitors = [System.Drawing.Rectangle[]] @(
    ([System.Drawing.Rectangle]::new(0, 0, 1920, 1080)),
    ([System.Drawing.Rectangle]::new(1920, 400, 1920, 1080))
)
$gap = [System.Drawing.Rectangle]::new(2100, 100, 286, 148)
$virtual = [System.Drawing.Rectangle]::Union($monitors[0], $monitors[1])
Assert-Equal $virtual.Contains($gap) $true 'Legacy virtual bounding box incorrectly includes the monitor gap'
Assert-Equal (Test-Visibility $gap $monitors) $false 'An empty monitor gap is not visible'
Assert-Equal (Test-Visibility ([System.Drawing.Rectangle]::new(2100, 500, 286, 148)) $monitors) $true 'Valid secondary-monitor overlay is retained'
Assert-Equal (Test-Visibility ([System.Drawing.Rectangle]::new(1870, 500, 286, 148)) $monitors) $true '40+ visible pixels on one monitor are retained'
Assert-Equal (Test-Visibility ([System.Drawing.Rectangle]::new(1885, 100, 286, 148)) $monitors) $false 'Less than 40 visible pixels cannot count as reachable'
$negativeOrigin = [System.Drawing.Rectangle[]] @([System.Drawing.Rectangle]::new(-1600, -300, 1600, 900))
Assert-Equal (Test-Visibility ([System.Drawing.Rectangle]::new(-1500, -100, 286, 148)) $negativeOrigin) $true 'Negative-origin monitor is supported'
Assert-Equal (Test-Visibility ([System.Drawing.Rectangle]::new(30, 30, 286, 148)) ([System.Drawing.Rectangle[]] @())) $false 'No monitors reported is not visible'

# TimerController uses a monotonic Stopwatch for elapsed time. Its UI
# DispatcherTimer should tick only while running; explicit Changed events
# still refresh the paused overlay on reset or settings changes.
$instanceFlag = [Reflection.BindingFlags]'NonPublic, Instance'
$uiTimerField = $timerType.GetField('uiTimer', $instanceFlag)
if (!$uiTimerField) {
    throw 'Missing TimerController UI dispatcher timer'
}
$controller = [Activator]::CreateInstance($timerType, @([double]105.0))
$uiTimer = $uiTimerField.GetValue($controller)
Assert-Equal $uiTimer.IsEnabled $false 'Initial paused timer does not refresh UI periodically'
$controller.Start()
Assert-Equal $uiTimer.IsEnabled $true 'Starting timer enables periodic UI refresh'
$controller.Stop()
Assert-Equal $uiTimer.IsEnabled $false 'Stopping timer disables periodic UI refresh'
$controller.SetFarmSeconds(90.0)
Assert-Equal $controller.FarmSeconds 90.0 'Paused timer still accepts changed phase duration'
$controller.Start()
$controller.Reset()
Assert-Equal $uiTimer.IsEnabled $false 'Reset also stops idle UI refresh'
Assert-Equal $controller.IsRunning $false 'Reset preserves existing paused state'

# Exercise the real pre-LOOT alert state machine without constructing or
# showing a WPF window or writing to the user's LocalApplicationData.
$overlayType = $assembly.GetType('FarmTimerOverlay.FarmOverlayWindow', $true)
$handleAlerts = $overlayType.GetMethod('HandlePhaseAlerts', $instanceFlag)
$pulseActive = $overlayType.GetField('preLootSoundActive', $instanceFlag)
$preLootTimer = $overlayType.GetField('preLootSoundTimer', $instanceFlag)
$settingsField = $overlayType.GetField('settings', $instanceFlag)
$timerField = $overlayType.GetField('timer', $instanceFlag)
$canPulse = $settingsType.GetMethod('CanPlayPulseSound', $instanceFlag)
$canTing = $settingsType.GetMethod('CanPlayLootTingSound', $instanceFlag)
if (!$handleAlerts -or !$pulseActive -or !$preLootTimer -or !$settingsField -or !$timerField -or
    !$canPulse -or !$canTing) {
    throw 'Missing pre-LOOT sound state machine reflection targets'
}
$settingsFile = [string]([IO.Path]::Combine([IO.Path]::GetTempPath(), ('FarmTimerOverlay-Unsaved-' + [Guid]::NewGuid().ToString('N') + '.ini')))
$quietSettings = $loadFromPath.Invoke($null, [object[]]@($settingsFile))
$settingsType.GetField('FlashLootAlert').SetValue($quietSettings, $false)
$settingsType.GetField('SoundLootAlert').SetValue($quietSettings, $false)
Assert-Equal $canPulse.Invoke($quietSettings, @()) $false 'Master Sound Alert OFF blocks pre-LOOT pip'
Assert-Equal $canTing.Invoke($quietSettings, @()) $false 'Master Sound Alert OFF blocks LOOT chime'
$settingsType.GetField('SoundLootAlert').SetValue($quietSettings, $true)
Assert-Equal $canPulse.Invoke($quietSettings, @()) $true 'Master ON permits enabled pip'
Assert-Equal $canTing.Invoke($quietSettings, @()) $true 'Master ON permits enabled chime'
$settingsType.GetField('PulseSoundAlert').SetValue($quietSettings, $false)
Assert-Equal $canPulse.Invoke($quietSettings, @()) $false 'Independent pip toggle still mutes pip'
Assert-Equal $canTing.Invoke($quietSettings, @()) $true 'Independent pip toggle does not mute chime'
$settingsType.GetField('PulseSoundAlert').SetValue($quietSettings, $true)
$settingsType.GetField('LootTingSoundAlert').SetValue($quietSettings, $false)
Assert-Equal $canPulse.Invoke($quietSettings, @()) $true 'Independent chime toggle does not mute pip'
Assert-Equal $canTing.Invoke($quietSettings, @()) $false 'Independent chime toggle still mutes chime'
$settingsType.GetField('LootTingSoundAlert').SetValue($quietSettings, $true)
$settingsType.GetField('SoundAlertVolume').SetValue($quietSettings, [double] 0.0)
Assert-Equal $canPulse.Invoke($quietSettings, @()) $false 'Zero volume silences pip'
Assert-Equal $canTing.Invoke($quietSettings, @()) $false 'Zero volume silences chime'
$settingsType.GetField('SoundAlertVolume').SetValue($quietSettings, [double] 1.0)
$settingsType.GetField('SoundLootAlert').SetValue($quietSettings, $false)
$probeTimer = [Activator]::CreateInstance($timerType, @([double] 105.0))
$probeOverlay = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($overlayType)
$settingsField.SetValue($probeOverlay, $quietSettings)
$timerField.SetValue($probeOverlay, $probeTimer)
$probeTimer.Start()
try {
    $handleAlerts.Invoke($probeOverlay, [object[]]@($true, [double] 4.0)) | Out-Null
    Assert-Equal $pulseActive.GetValue($probeOverlay) $false 'Master Sound Alert OFF prevents automatic pre-LOOT pulse'
    $settingsType.GetField('SoundLootAlert').SetValue($quietSettings, $true)
    $handleAlerts.Invoke($probeOverlay, [object[]]@($true, [double] 4.0)) | Out-Null
    Assert-Equal $pulseActive.GetValue($probeOverlay) $true 'Master ON resumes automatic pre-LOOT pulse'
    $settingsType.GetField('SoundLootAlert').SetValue($quietSettings, $false)
    $handleAlerts.Invoke($probeOverlay, [object[]]@($true, [double] 4.0)) | Out-Null
    Assert-Equal $pulseActive.GetValue($probeOverlay) $false 'Muting during pre-LOOT stops active pulse timer'
}
finally {
    $probeTimer.Stop()
    $activeDispatcher = $preLootTimer.GetValue($probeOverlay)
    if ($activeDispatcher) { $activeDispatcher.Stop() }
}
Assert-Equal (Test-Path -LiteralPath $settingsFile) $false 'Sound test never writes user settings'

Write-Output 'All Farm Timer Overlay settings regressions passed.'
