param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '..\BetterJoyForCemu\bin\x64\Release\BetterJoy2.exe')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Configuration
[Configuration.ConfigurationManager]::AppSettings.Set('AHRS_beta', '0.1')

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

$resolvedAssembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
Push-Location (Split-Path -Parent $resolvedAssembly)
try {
    $assembly = [Reflection.Assembly]::LoadFrom($resolvedAssembly)
    $mappingsType = $assembly.GetType('BetterJoyForCemu.ControllerMappings', $true)
    $bindingType = $assembly.GetType('BetterJoyForCemu.ControllerMappings+CustomBinding', $true)
    $controllerType = $assembly.GetType('BetterJoyForCemu.Controller', $true)
    $reassignType = $assembly.GetType('BetterJoyForCemu.Reassign', $true)

    $validateInput = $mappingsType.GetMethod(
        'IsValidCustomBindingInput',
        [Reflection.BindingFlags]'Public,Static',
        $null,
        [Type[]]@([string], [bool]),
        $null)

    # User contract: ordinary custom binds still require a chord, while Rebind accepts one or
    # more normalized controller buttons.
    Assert-True (-not $validateInput.Invoke($null, @('joy_13', $false))) `
        'Rebind Disabled incorrectly accepted a one-button source.'
    Assert-True ($validateInput.Invoke($null, @('joy_13', $true))) `
        'Rebind Enabled did not accept a one-button source.'
    Assert-True ($validateInput.Invoke($null, @('joy_13+joy_15', $true))) `
        'Rebind Enabled did not accept a multi-button source.'

    $bindingConstructor = $bindingType.GetConstructor([Type[]]@([string], [string], [bool]))
    $typedBindings = [Array]::CreateInstance($bindingType, 1)
    $typedBindings.SetValue(
        $bindingConstructor.Invoke(@('joy_13', 'joy_15', $true)), 0)
    $serialize = $mappingsType.GetMethod(
        'SerializeCustomBindings', [Reflection.BindingFlags]'NonPublic,Static')
    $parse = $mappingsType.GetMethod(
        'ParseCustomBindings', [Reflection.BindingFlags]'NonPublic,Static')
    $serialized = [string]$serialize.Invoke($null, [object[]]@(,$typedBindings))
    Assert-True ($serialized -eq "joy_13`tjoy_15`t1") `
        'A Rebind Enabled row did not retain its mode in profile serialization.'
    $legacyBinding = $parse.Invoke($null, @("joy_13+joy_14`tjoy_15"))[0]
    Assert-True (-not $bindingType.GetProperty('Rebind').GetValue($legacyBinding)) `
        'A saved custom bind without a rebind field must remain Rebind Disabled.'

    $applyOverrides = $controllerType.GetMethod(
        'ApplyCustomButtonOverrides', [Reflection.BindingFlags]'NonPublic,Static')
    $buttonCount = [Enum]::GetValues($controllerType.GetNestedType('Button')).Count
    $isRebindHeld = $controllerType.GetMethod(
        'AreCustomRebindButtonsHeld',
        [Reflection.BindingFlags]'NonPublic,Static',
        $null,
        [Type[]]@([string], [bool[]], [bool[]]),
        $null)

    # A direct B rebind remains active when another button is also held, while every member of a
    # multi-button source must be physically present.
    [bool[]]$physical = New-Object bool[] $buttonCount
    $physical[13] = $true
    $heldArguments = New-Object object[] 3
    $heldArguments[0] = 'joy_13'
    $heldArguments[1] = $physical
    Assert-True ($isRebindHeld.Invoke($null, $heldArguments)) `
        'A physical B press did not activate its direct rebind.'
    $physical[14] = $true
    Assert-True ($isRebindHeld.Invoke($null, $heldArguments)) `
        'An additional held button incorrectly cancelled the B rebind.'
    $heldArguments[0] = 'joy_13+joy_15'
    Assert-True (-not $isRebindHeld.Invoke($null, $heldArguments)) `
        'A multi-button rebind activated before every source button was held.'

    # User contract: B -> Y is replacement output. Physical B is consumed and only Y is emitted.
    [bool[]]$output = New-Object bool[] $buttonCount
    [bool[]]$consumed = New-Object bool[] $buttonCount
    [bool[]]$remapped = New-Object bool[] $buttonCount
    $output[13] = $true
    $consumed[13] = $true
    $remapped[15] = $true
    $applyOverrides.Invoke($null, @($output, $consumed, $remapped))
    Assert-True (-not $output[13] -and $output[15]) `
        'B -> Y must consume B and emit only Y.'

    # Rebinding a button to itself must still emit the explicitly selected output.
    [bool[]]$output = New-Object bool[] $buttonCount
    [bool[]]$consumed = New-Object bool[] $buttonCount
    [bool[]]$remapped = New-Object bool[] $buttonCount
    $output[13] = $true
    $consumed[13] = $true
    $remapped[13] = $true
    $applyOverrides.Invoke($null, @($output, $consumed, $remapped))
    Assert-True ($output[13]) 'B -> B must continue to emit B.'

    # Regression: Rebind Disabled preserves the original additive custom-chord behavior.
    [bool[]]$output = New-Object bool[] $buttonCount
    [bool[]]$consumed = New-Object bool[] $buttonCount
    [bool[]]$remapped = New-Object bool[] $buttonCount
    $output[13] = $true
    $output[14] = $true
    $remapped[15] = $true
    $applyOverrides.Invoke($null, @($output, $consumed, $remapped))
    Assert-True ($output[13] -and $output[14] -and $output[15]) `
        'Rebind Disabled must preserve both chord inputs while adding its output.'

    # User contract: output assignment reads normalized physical state. Generated Y output must
    # never feed back through Controller.GetButton and make capture report Y instead of B.
    $proControllerType = $assembly.GetType('BetterJoyForCemu.ProController', $true)
    $controller = [Runtime.Serialization.FormatterServices]::GetUninitializedObject(
        $proControllerType)
    [bool[]]$physicalButtons = New-Object bool[] $buttonCount
    [bool[]]$generatedButtons = New-Object bool[] $buttonCount
    $physicalButtons[13] = $true
    $generatedButtons[15] = $true
    $controllerType.GetField('buttons', [Reflection.BindingFlags]'Instance,NonPublic').SetValue(
        $controller, $physicalButtons)
    $controllerType.GetField(
        'continuousRemapButtons',
        [Reflection.BindingFlags]'Instance,NonPublic').SetValue($controller, $generatedButtons)
    $buttonType = $controllerType.GetNestedType('Button')
    Assert-True ($controller.GetButton([Enum]::ToObject($buttonType, 13))) `
        'Normalized capture lost the physical B input.'
    Assert-True (-not $controller.GetButton([Enum]::ToObject($buttonType, 15))) `
        'Generated Y output leaked into normalized controller capture.'

    # User contract: PlayStation names are assigned directly to the canonical numeric button
    # code throughout the bindings UI. They are display labels only; stored joy_<code> values stay
    # unchanged.
    $displayName = $reassignType.GetMethod(
        'ControllerButtonDisplayName',
        [Reflection.BindingFlags]'NonPublic,Static',
        $null,
        [Type[]]@([int], [bool]),
        $null)
    $playStationLabels = @{
        15 = 'SQUARE'; 16 = 'TRIANGLE'; 14 = 'CIRCLE'; 13 = 'CROSS'
        11 = 'L1'; 18 = 'R1'; 12 = 'L2'; 19 = 'R2'; 10 = 'L3'; 17 = 'R3'
        7 = 'PS'; 6 = 'SHARE'; 8 = 'MENU'; 25 = 'MIC_MUTE'; 26 = 'FN1'; 27 = 'FN2'
        20 = 'TOUCHPAD'; 21 = 'TOUCHPAD_TAP'; 3 = 'DPAD_UP'
    }
    foreach ($entry in $playStationLabels.GetEnumerator()) {
        $actual = [string]$displayName.Invoke($null, @([int]$entry.Key, $true))
        Assert-True ($actual -eq $entry.Value) `
            "PlayStation button code $($entry.Key) displayed '$actual' instead of '$($entry.Value)'."
    }
    Assert-True ($displayName.Invoke($null, @(15, $false)) -eq 'Y') `
        'Non-PlayStation bindings must retain their existing label until mapped explicitly.'

    # User contract: on PlayStation layouts the Kenney glyph replaces the text label wherever the
    # imported set has one, including touchpad press and tap, and BetterJoy's own PS glyph;
    # buttons without a glyph (Capture, SL/SR, two-finger touchpad gestures) keep their text
    # label, and non-PlayStation layouts keep text entirely.
    $kindType = $assembly.GetType('BetterJoyForCemu.ControllerKind', $true)
    $dualSense = [Enum]::ToObject($kindType, 5)
    $dualShock4 = [Enum]::ToObject($kindType, 6)
    $glyph = $reassignType.GetMethod('ControllerButtonGlyph', [Reflection.BindingFlags]'NonPublic,Static')
    $sharedGlyphCodes = 0, 1, 2, 3, 6, 7, 8, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21
    foreach ($code in $sharedGlyphCodes + 25, 26, 27) {
        Assert-True ($null -ne $glyph.Invoke($null, @([int]$code, $dualSense))) `
            "DualSense button code $code has no embedded glyph."
    }
    foreach ($code in $sharedGlyphCodes) {
        Assert-True ($null -ne $glyph.Invoke($null, @([int]$code, $dualShock4))) `
            "DualShock 4 button code $code has no embedded glyph."
    }
    foreach ($code in 4, 5, 9, 22, 23, 24) {
        Assert-True ($null -eq $glyph.Invoke($null, @([int]$code, $dualSense))) `
            "Button code $code must fall back to its text label."
    }
    Assert-True ($null -eq $glyph.Invoke($null, @(13, $null))) `
        'Non-PlayStation layouts must keep text labels.'

    $labelParts = $reassignType.GetMethod('BindLabelParts', [Reflection.BindingFlags]'NonPublic,Static')
    $parts = $labelParts.Invoke($null, @('joy_7+joy_12', $dualSense, $null))
    Assert-True ($parts.Count -eq 3 -and $parts[0] -is [Drawing.Image] -and $parts[1] -eq '+' -and
        $parts[2] -is [Drawing.Image]) 'PS + L2 must display as the PS glyph, +, and the L2 glyph.'
    $parts = $labelParts.Invoke($null, @('joy_9+joy_12', $dualSense, $null))
    Assert-True ($parts.Count -eq 3 -and $parts[0] -eq 'CAPTURE' -and $parts[1] -eq '+' -and
        $parts[2] -is [Drawing.Image]) 'A button without a glyph must keep its text beside glyphs.'
    Assert-True ($null -eq $labelParts.Invoke($null, @('key_65', $dualSense, $null))) `
        'Keyboard-only binds must keep their plain text label.'

    Write-Host 'Custom Rebind regression tests passed.'
} finally {
    Pop-Location
}
