param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '..\BetterJoyForCemu\bin\x64\Release\BetterJoy2.exe')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Configuration
Add-Type -AssemblyName System.Windows.Forms

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}

function Assert-Near([double]$Actual, [double]$Expected, [double]$Tolerance,
        [string]$Message) {
    if ([Math]::Abs($Actual - $Expected) -gt $Tolerance) {
        throw "$Message (actual=$Actual expected=$Expected)"
    }
    $script:checks++
}

$resolvedAssembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
Push-Location (Split-Path -Parent $resolvedAssembly)
try {
    # Controller construction reads the application's settings through ConfigurationManager;
    # PowerShell otherwise supplies pwsh.exe.config rather than BetterJoy2.exe.config.
    $settings = [Configuration.ConfigurationManager]::AppSettings
    $readOnlyProperty = [Collections.Specialized.NameObjectCollectionBase].GetProperty(
        'IsReadOnly', [Reflection.BindingFlags]'Instance,NonPublic')
    $readOnlyProperty.SetValue($settings, $false)
    [xml]$applicationConfig = Get-Content -LiteralPath ($resolvedAssembly + '.config')
    foreach ($entry in $applicationConfig.configuration.appSettings.add) {
        $settings.Set([string]$entry.key, [string]$entry.value)
    }

    $assembly = [Reflection.Assembly]::LoadFrom($resolvedAssembly)
    $managerType = $assembly.GetType('BetterJoyForCemu.JoyconManager', $true)
    $xboxType = $assembly.GetType('BetterJoyForCemu.XboxController', $true)
    $controllerType = $assembly.GetType('BetterJoyForCemu.Controller', $true)
    $buttonType = $controllerType.GetNestedType('Button')
    $reassignType = $assembly.GetType('BetterJoyForCemu.Reassign', $true)
    $mappingsType = $assembly.GetType('BetterJoyForCemu.ControllerMappings', $true)
    $appPathsType = $assembly.GetType('BetterJoyForCemu.AppPaths', $true)
    $appPathsType.GetField('dataDir',
        [Reflection.BindingFlags]'Static,NonPublic').SetValue(
            $null, (Split-Path -Parent $resolvedAssembly))
    # Keep this regression isolated from the user's real controller_mappings.xml. The static
    # dictionary starts empty; marking it loaded makes all lookups use App.config defaults.
    $mappingsType.GetField('loaded',
        [Reflection.BindingFlags]'Static,NonPublic').SetValue($null, $true)

    # User contract: every physical XInput-compatible controller is Xbox input, including licensed
    # third-party pads such as the SCUF Valor Pro (1B1C:3A15), which previously fell through to
    # the third-party list and was decoded as a Pro Controller. BetterJoy's own virtual output
    # and non-XInput devices are never claimed.
    $knownDevice = $managerType.GetMethod(
        'IsXboxController', [Reflection.BindingFlags]'Static,NonPublic')
    Assert-True $knownDevice.Invoke($null, @(
        [UInt16]0x045E, [UInt16]0x02FF, '\\?\HID#VID_045E&PID_02FF&IG_00#7&1a2b3c4d&0&0000')) `
        'The wireless-adapter Xbox controller 045E:02FF was not recognized as Xbox input.'
    Assert-True $knownDevice.Invoke($null, @(
        [UInt16]0x1B1C, [UInt16]0x3A15, '\\?\HID#VID_1B1C&PID_3A15&IG_06#unknown_physical')) `
        'The SCUF Valor Pro XInput controller 1B1C:3A15 was not recognized as Xbox input.'
    Assert-True (-not $knownDevice.Invoke($null, @(
        [UInt16]0x045E, [UInt16]0x028E, '\\?\HID#VID_045E&PID_028E&IG_00'))) `
        'BetterJoy''s ViGEm Xbox 360 output must never be adopted as physical Xbox input.'
    Assert-True (-not $knownDevice.Invoke($null, @(
        [UInt16]0x045E, [UInt16]0x0B13,
        '\\?\HID#{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02045e_PID&0b13_REV&0501'))) `
        'Bluetooth LE Xbox input (no IG_ collection) was claimed before that transport is implemented.'
    Assert-True (-not $knownDevice.Invoke($null, @(
        [UInt16]0x054C, [UInt16]0x0CE6, '\\?\HID#VID_054C&PID_0CE6&MI_03#8&2f3a&0&0000'))) `
        'A non-XInput controller (DualSense) was classified as Xbox input.'

    # A single transient XInput miss must not release the selected slot and force BetterJoy into
    # a disconnect/reconnect loop. Only the same sustained failure window used by Poll may release.
    $shouldRelease = $xboxType.GetMethod(
        'ShouldReleaseInputSlot', [Reflection.BindingFlags]'Static,NonPublic')
    Assert-True (-not $shouldRelease.Invoke($null, @(1))) `
        'One transient XInput failure incorrectly releases the controller slot.'
    Assert-True (-not $shouldRelease.Invoke($null, @(239))) `
        'The XInput slot releases before Poll''s sustained-failure window.'
    Assert-True $shouldRelease.Invoke($null, @(240)) `
        'A sustained XInput failure does not release the controller slot for reconnection.'

    # Creating a ViGEm target can change XInput user-index assignment. The receiver must reject
    # BetterJoy's own 045E:028E output instead of accepting it as the physical controller.
    $virtualIdentity = $managerType.GetMethod(
        'IsVigemVirtualController', [Reflection.BindingFlags]'Static,NonPublic')
    Assert-True $virtualIdentity.Invoke($null, @([UInt16]0x045E, [UInt16]0x028E)) `
        'BetterJoy''s existing ViGEm Xbox identity was not recognized.'
    Assert-True (-not $virtualIdentity.Invoke($null, @([UInt16]0x045E, [UInt16]0x02FF))) `
        'The physical Xbox identity was incorrectly classified as BetterJoy''s virtual output.'

    $mapState = $xboxType.GetMethod(
        'MapXInputState', [Reflection.BindingFlags]'Static,NonPublic')
    $stateType = $xboxType.GetNestedType(
        'XInputState', [Reflection.BindingFlags]'NonPublic')
    $gamepadType = $xboxType.GetNestedType(
        'XInputGamepad', [Reflection.BindingFlags]'NonPublic')
    $parsedType = $xboxType.GetNestedType(
        'ParsedReport', [Reflection.BindingFlags]'NonPublic')
    $fieldFlags = [Reflection.BindingFlags]'Instance,NonPublic'
    $applyParsed = $xboxType.GetMethod(
        'ApplyParsedReport', [Reflection.BindingFlags]'Instance,NonPublic')
    $mapToXbox = $controllerType.GetMethod(
        'MapToXbox360Input', [Reflection.BindingFlags]'Static,NonPublic')
    $testController = [Activator]::CreateInstance($xboxType, [object[]]@(
        [IntPtr]::Zero, 'test-xbox-input', 'test-xbox-input', $true,
        [UInt16]0x045E, [UInt16]0x02FF, 0))
    $testController.PadMacAddress =
        [Net.NetworkInformation.PhysicalAddress]::Parse('A1B2C3D4E5F6')
    $testController.InvalidateMappingProfileCache()

    function Map-XboxState([UInt16]$Buttons = 0, [byte]$LeftTrigger = 0,
            [byte]$RightTrigger = 0, [Int16]$LeftX = 0, [Int16]$LeftY = 0,
            [Int16]$RightX = 0, [Int16]$RightY = 0) {
        $gamepad = [Activator]::CreateInstance($gamepadType)
        $gamepadType.GetField('Buttons', $fieldFlags).SetValue($gamepad, $Buttons)
        $gamepadType.GetField('LeftTrigger', $fieldFlags).SetValue($gamepad, $LeftTrigger)
        $gamepadType.GetField('RightTrigger', $fieldFlags).SetValue($gamepad, $RightTrigger)
        $gamepadType.GetField('LeftThumbX', $fieldFlags).SetValue($gamepad, $LeftX)
        $gamepadType.GetField('LeftThumbY', $fieldFlags).SetValue($gamepad, $LeftY)
        $gamepadType.GetField('RightThumbX', $fieldFlags).SetValue($gamepad, $RightX)
        $gamepadType.GetField('RightThumbY', $fieldFlags).SetValue($gamepad, $RightY)
        $state = [Activator]::CreateInstance($stateType)
        $stateType.GetField('Gamepad', $fieldFlags).SetValue($state, $gamepad)
        return $mapState.Invoke($null, @($state))
    }

    function Get-ParsedField($Parsed, [string]$Name) {
        return $parsedType.GetField($Name, $fieldFlags).GetValue($Parsed)
    }

    function Button-Code([string]$Name) {
        return [int][Enum]::Parse($buttonType, $Name)
    }

    # User contract: physical Xbox face buttons map directly to the same virtual Xbox positions.
    # BetterJoy's canonical enum names are Nintendo-derived historical storage names, so these
    # expected numeric positions intentionally look crossed: A=13(B), B=14(A), X=15(Y), Y=16(X).
    $faceCases = @(
        @{ Name = 'A'; Mask = 0x1000; Code = 13 },
        @{ Name = 'B'; Mask = 0x2000; Code = 14 },
        @{ Name = 'X'; Mask = 0x4000; Code = 15 },
        @{ Name = 'Y'; Mask = 0x8000; Code = 16 }
    )
    foreach ($case in $faceCases) {
        $parsed = Map-XboxState -Buttons $case.Mask
        [bool[]]$buttons = Get-ParsedField $parsed 'Buttons'
        Assert-True $buttons[$case.Code] `
            "Physical Xbox $($case.Name) did not land on its matching virtual position."
        Assert-True (($buttons | Where-Object { $_ }).Count -eq 1) `
            "Physical Xbox $($case.Name) activated an additional canonical button."
        $applyParsed.Invoke($testController, @($parsed)) | Out-Null
        $virtual = $mapToXbox.Invoke($null, @($testController))
        $virtualFace = $virtual.GetType().GetField($case.Name.ToLowerInvariant()).GetValue($virtual)
        Assert-True $virtualFace `
            "Physical Xbox $($case.Name) did not emerge as virtual Xbox $($case.Name)."
    }

    # One native state covers all remaining digital fields plus actual trigger and stick domains.
    $parsed = Map-XboxState -Buttons 0x07FF -LeftTrigger 255 -RightTrigger 128 `
        -LeftX 32767 -LeftY -32768 -RightX -32768 -RightY 32767
    [bool[]]$buttons = Get-ParsedField $parsed 'Buttons'
    foreach ($name in @('MINUS','PLUS','DPAD_UP','DPAD_DOWN','DPAD_LEFT','DPAD_RIGHT',
            'SHOULDER_1','SHOULDER_2','SHOULDER2_1','SHOULDER2_2','STICK','STICK2','HOME')) {
        Assert-True $buttons[(Button-Code $name)] "Xbox $name was not decoded."
    }
    Assert-True ((Get-ParsedField $parsed 'LeftTrigger') -eq 255) `
        'A full LT press did not remain 255.'
    Assert-True ((Get-ParsedField $parsed 'RightTrigger') -eq 128) `
        'A half RT press did not remain 128.'
    Assert-Near (Get-ParsedField $parsed 'LeftX') 1.0 0.00001 'Left X maximum was wrong.'
    Assert-Near (Get-ParsedField $parsed 'LeftY') -1.0 0.00001 'Left Y minimum was wrong.'
    Assert-Near (Get-ParsedField $parsed 'RightX') -1.0 0.00001 'Right X minimum was wrong.'
    Assert-Near (Get-ParsedField $parsed 'RightY') 1.0 0.00001 'Right Y maximum was wrong.'

    # Model-aware presentation must expose Xbox labels and reuse the same glyph table as Xbox
    # virtual output. This prevents the old Nintendo labels from reappearing in any bind pane.
    $kindType = $assembly.GetType('BetterJoyForCemu.ControllerKind', $true)
    $xboxKind = [Enum]::Parse($kindType, 'Xbox')
    $displayMethod = $reassignType.GetMethods(
        [Reflection.BindingFlags]'Static,NonPublic') | Where-Object {
            $_.Name -eq 'ControllerButtonDisplayName' -and $_.GetParameters().Count -eq 2
        }
    Assert-True (($displayMethod.Invoke($null, @([int]13, $xboxKind))) -eq 'A') `
        'Physical Xbox A still displayed a Nintendo-derived label.'
    $glyphMethod = $reassignType.GetMethod(
        'ControllerButtonGlyphName', [Reflection.BindingFlags]'Static,NonPublic')
    Assert-True (($glyphMethod.Invoke($null, @([int]13, $xboxKind))) -eq 'xbox_a_color_light') `
        'Physical Xbox A did not reuse the Xbox output glyph.'

    $profileMethod = $mappingsType.GetMethod('ProfileIdFor')
    $controller = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($xboxType)
    $controller.PadMacAddress = [Net.NetworkInformation.PhysicalAddress]::Parse('112233445566')
    $controller.serial_number = '000001016FC692D4'
    $controller.path = 'test-xbox'
    $profileId = $profileMethod.Invoke($null, [object[]]@($controller))
    Assert-True ($profileId -eq 'xbox:112233445566') `
        "Xbox controller did not receive an Xbox profile identity: $profileId"

    # User contract: a dropped Xbox controller must be able to latch on again. Detaching from any
    # state - including DROPPED after a stale or failed connection - releases its XInput slot.
    $claimedSlots = $xboxType.GetField(
        'ClaimedXInputSlots', [Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
    $slotField = $xboxType.GetField('xInputSlot', $fieldFlags)
    $claimedSlots[2] = $true
    $slotField.SetValue($testController, 2)
    $testController.state = [Enum]::Parse($controllerType.GetNestedType('state_'), 'DROPPED')
    $testController.Detach($true)
    Assert-True (-not $claimedSlots[2]) `
        'Detaching a DROPPED Xbox controller leaked its XInput slot.'
    Assert-True ($slotField.GetValue($testController) -eq -1) `
        'A detached Xbox controller still reports an XInput slot.'

    # User contract: removal is detected like arrival, from the device scan, never from HID
    # report traffic - controllers without a gyro may send no report while idle. A controller is
    # dropped only after its path is absent from consecutive scans, and reappearing resets that.
    $nextAbsent = $managerType.GetMethod(
        'NextAbsentScanCount', [Reflection.BindingFlags]'Static,NonPublic')
    $dropAfter = [int]$managerType.GetField(
        'AbsentScansBeforeDrop', [Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
    Assert-True ($dropAfter -ge 2) 'A single missed scan must never be enough to drop a controller.'
    $absent = [int]$nextAbsent.Invoke($null, @($false, 0))
    Assert-True ($absent -lt $dropAfter) 'One absent scan dropped a live controller.'
    $absent = [int]$nextAbsent.Invoke($null, @($false, $absent))
    Assert-True ($absent -ge $dropAfter) 'A controller absent from consecutive scans was not dropped.'
    Assert-True (([int]$nextAbsent.Invoke($null, @($true, $absent))) -eq 0) `
        'A controller whose path reappeared was not reset to present.'

    # User contract: one physical Xbox controller keeps one profile across reconnects - never keyed
    # on the regenerated HID path. In Xbox mode the controller's own 64-bit ID is used, identical
    # wired and through the dongle; in PC mode (no serial anywhere) the port-stable root device.
    # Chains are the real ones Windows reported for a SCUF Valor Pro, listed HID node upward.
    $stableIdentity = $managerType.GetMethod(
        'XboxStableIdentity', [Reflection.BindingFlags]'Static,NonPublic')
    function Resolve-XboxIdentity([string[]]$Chain) {
        [byte[]]$mac = [byte[]]::new(6)
        $identityArguments = New-Object object[] 2
        $identityArguments[0] = [string[]]$Chain
        $identityArguments[1] = $mac
        $source = $stableIdentity.Invoke($null, $identityArguments)
        return @{ Source = $source; Id = (($mac | ForEach-Object { $_.ToString('X2') }) -join '') }
    }
    $dongleXbox = Resolve-XboxIdentity @(
        'HID\VID_045E&PID_02FF&IG_00\C&777E48C&1&0000',
        'USB\VID_045E&PID_02FF&IG_00\00&00&000019036804CD63',
        'USB\VID_1B1C&PID_3A14\000001016FC692D4',
        'USB\ROOT_HUB30\9&265260E5&0&0')
    $wiredXbox = Resolve-XboxIdentity @(
        'HID\VID_045E&PID_02FF&IG_00\C&777E48C&1&0000',
        'USB\VID_045E&PID_02FF&IG_00\00&00&000019036804CD63',
        'USB\VID_1B1C&PID_3A07\000001016FC692D4',
        'USB\ROOT_HUB30\9&265260E5&0&0')
    Assert-True ($dongleXbox.Source -eq 'controller-serial' -and
        $dongleXbox.Id -eq '19036804CD63') `
        "Xbox-mode dongle connection did not use the controller serial: $($dongleXbox.Source) $($dongleXbox.Id)"
    Assert-True ($wiredXbox.Id -eq $dongleXbox.Id) `
        'The same controller got different identities wired and through its dongle.'
    $pcMode = Resolve-XboxIdentity @(
        'HID\VID_1B1C&PID_3A15&IG_04\D&FFA48E9&0&0000',
        'USB\VID_1B1C&PID_3A15&IG_04\C&12180EDB&0&04',
        'USB\VID_1B1C&PID_3A15&MI_00\B&9D9BC04&1&0000',
        'USB\VID_1B1C&PID_3A15\A&308493AA&0&10',
        'USB\ROOT_HUB30\9&265260E5&0&0')
    Assert-True ($pcMode.Source -eq 'root-device') `
        "PC mode (no serial) did not fall back to the root device: $($pcMode.Source)"
    $childOnly = Resolve-XboxIdentity @(
        'HID\VID_1B1C&PID_3A16&IG_04\D&F9371F&0&0000',
        'USB\VID_1B1C&PID_3A16&IG_04\C&38937C2D&0&04')
    Assert-True ($null -eq $childOnly.Source) `
        'Only regenerated IG_ child nodes must never be accepted as a stable identity.'

    # User contract: deleting a profile in Controller Profiles removes it for good, while profiles
    # the service created behind the UI's back are still never erased by that Save.
    $deleteTestDir = Join-Path ([IO.Path]::GetTempPath()) ('betterjoy-delete-' + [guid]::NewGuid())
    New-Item -ItemType Directory -Path $deleteTestDir | Out-Null
    try {
        $appPathsType.GetField('dataDir',
            [Reflection.BindingFlags]'Static,NonPublic').SetValue($null, $deleteTestDir)
        $mappingsFile = Join-Path $deleteTestDir 'controller_mappings.xml'
        Set-Content -LiteralPath $mappingsFile -Encoding UTF8 -Value (
            '<controllerMappings version="5"><profile id="xbox:aaaaaaaaaaaa" />' +
            '<profile id="pro:bbbbbbbbbbbb" /></controllerMappings>')
        $mappingsType.GetMethod('DeleteProfile').Invoke($null, @('xbox:aaaaaaaaaaaa')) | Out-Null
        [xml]$savedMappings = Get-Content -LiteralPath $mappingsFile -Raw
        $savedIds = @($savedMappings.controllerMappings.profile | ForEach-Object { $_.id })
        Assert-True (-not ($savedIds -contains 'xbox:aaaaaaaaaaaa')) `
            'A deleted profile was written back to disk by Save.'
        Assert-True ($savedIds -contains 'pro:bbbbbbbbbbbb') `
            'Deleting one profile erased a profile created by another process.'
    } finally {
        Remove-Item -LiteralPath $deleteTestDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Output "Passed $script:checks Xbox controller checks."
} finally {
    Pop-Location
}
