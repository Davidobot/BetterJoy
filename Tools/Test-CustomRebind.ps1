param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '..\BetterJoyForCemu\bin\x64\Release\BetterJoy2.exe')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Configuration
Add-Type -AssemblyName System.Windows.Forms
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

    # User contract: model-specific names are assigned directly to the canonical numeric button
    # code throughout the bindings UI. They are display labels only; stored joy_<code> values stay
    # unchanged. In particular, a right Joy-Con's physical face buttons occupy the canonical
    # DPAD slots, so those numeric codes must display as B/A/Y/X without an intermediate mapping.
    $kindType = $assembly.GetType('BetterJoyForCemu.ControllerKind', $true)
    $leftJoyCon = [Enum]::ToObject($kindType, 0)
    $rightJoyCon = [Enum]::ToObject($kindType, 1)
    $proController = [Enum]::ToObject($kindType, 2)
    $snesController = [Enum]::ToObject($kindType, 3)
    $n64Controller = [Enum]::ToObject($kindType, 4)
    $dualSense = [Enum]::ToObject($kindType, 5)
    $dualShock4 = [Enum]::ToObject($kindType, 6)
    $displayName = $reassignType.GetMethods([Reflection.BindingFlags]'NonPublic,Static') |
        Where-Object {
            $_.Name -eq 'ControllerButtonDisplayName' -and $_.GetParameters().Count -eq 2
        } | Select-Object -First 1
    $playStationLabels = @{
        15 = 'SQUARE'; 16 = 'TRIANGLE'; 14 = 'CIRCLE'; 13 = 'CROSS'
        11 = 'L1'; 18 = 'R1'; 12 = 'L2'; 19 = 'R2'; 10 = 'L3'; 17 = 'R3'
        7 = 'PS'; 6 = 'SHARE'; 8 = 'MENU'; 25 = 'MIC_MUTE'; 26 = 'FN1'; 27 = 'FN2'
        20 = 'TOUCHPAD'; 21 = 'TOUCHPAD_TAP'; 3 = 'DPAD_UP'
    }
    foreach ($entry in $playStationLabels.GetEnumerator()) {
        $actual = [string]$displayName.Invoke($null, @([int]$entry.Key, $dualSense))
        Assert-True ($actual -eq $entry.Value) `
            "PlayStation button code $($entry.Key) displayed '$actual' instead of '$($entry.Value)'."
    }
    $proLabels = @{ 11 = 'L'; 12 = 'ZL'; 18 = 'R'; 19 = 'ZR'; 10 = 'L STICK'; 17 = 'R STICK'; 13 = 'B' }
    foreach ($entry in $proLabels.GetEnumerator()) {
        Assert-True ($displayName.Invoke($null, @([int]$entry.Key, $proController)) -eq $entry.Value) `
            "Switch Pro button code $($entry.Key) did not use '$($entry.Value)'."
    }
    $rightJoyConLabels = @{
        0 = 'B'; 1 = 'A'; 2 = 'Y'; 3 = 'X'
        13 = 'DPAD_DOWN'; 14 = 'DPAD_RIGHT'; 15 = 'DPAD_LEFT'; 16 = 'DPAD_UP'
        11 = 'R'; 12 = 'ZR'; 18 = 'L'; 19 = 'ZL'; 10 = 'R STICK'; 17 = 'L STICK'
    }
    foreach ($entry in $rightJoyConLabels.GetEnumerator()) {
        Assert-True ($displayName.Invoke($null, @([int]$entry.Key, $rightJoyCon)) -eq $entry.Value) `
            "Right Joy-Con button code $($entry.Key) did not use '$($entry.Value)'."
    }
    Assert-True ($displayName.Invoke($null, @(0, $leftJoyCon)) -eq 'DPAD_DOWN') `
        'Left Joy-Con button code 0 must remain the physical D-pad Down button.'
    Assert-True ($displayName.Invoke($null, @(15, $null)) -eq 'Y') `
        'An unknown controller model must retain the canonical fallback label.'

    # User contract: on PlayStation layouts the Kenney glyph replaces the text label wherever the
    # imported set has one, including touchpad press/tap and the shared Kenney two-finger gesture
    # glyphs, and BetterJoy's own PS glyph; buttons without a glyph (Capture and SL/SR) keep their
    # text label, and unsupported controller layouts keep text entirely.
    $glyph = $reassignType.GetMethod('ControllerButtonGlyph', [Reflection.BindingFlags]'NonPublic,Static')
    $sharedGlyphCodes = 0, 1, 2, 3, 6, 7, 8, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21
    foreach ($code in $sharedGlyphCodes + 22, 23, 24, 25, 26, 27) {
        Assert-True ($null -ne $glyph.Invoke($null, @([int]$code, $dualSense))) `
            "DualSense button code $code has no embedded glyph."
    }
    foreach ($code in $sharedGlyphCodes + 22, 23, 24) {
        Assert-True ($null -ne $glyph.Invoke($null, @([int]$code, $dualShock4))) `
            "DualShock 4 button code $code has no embedded glyph."
    }
    foreach ($code in 4, 5, 9) {
        Assert-True ($null -eq $glyph.Invoke($null, @([int]$code, $dualSense))) `
            "Button code $code must fall back to its text label."
    }
    Assert-True ($null -eq $glyph.Invoke($null, @(13, $null))) `
        'An unknown controller model must keep text labels.'

    # User contract: Switch models use the unmodified Kenney glyph for the physical button
    # represented by each canonical code. Right Joy-Con face/D-pad slots therefore deliberately
    # use the inverse glyph families from Left/Pro. Capture uses the renamed, unmodified Kenney
    # generic circle glyph; SNES and N64 remain text until their own model-specific assets arrive.
    $switchGlyphCodes = 0..19
    foreach ($kind in @($leftJoyCon, $rightJoyCon, $proController)) {
        foreach ($code in $switchGlyphCodes) {
            Assert-True ($null -ne $glyph.Invoke($null, @([int]$code, $kind))) `
                "Switch controller $kind button code $code has no embedded glyph."
        }
    }
    Assert-True ($null -eq $glyph.Invoke($null, @(13, $snesController))) `
        'SNES bindings must remain text until SNES-specific assets are added.'
    Assert-True ($null -eq $glyph.Invoke($null, @(13, $n64Controller))) `
        'N64 bindings must remain text until N64-specific assets are added.'
    $glyphName = $reassignType.GetMethod(
        'ControllerButtonGlyphName', [Reflection.BindingFlags]'NonPublic,Static')
    $rightJoyConGlyphs = @{
        0 = 'switch_button_b'; 1 = 'switch_button_a'; 2 = 'switch_button_y'; 3 = 'switch_button_x'
        13 = 'switch_dpad_down'; 14 = 'switch_dpad_right'
        15 = 'switch_dpad_left'; 16 = 'switch_dpad_up'
        11 = 'switch_button_r'; 12 = 'switch_button_zr'
        18 = 'switch_button_l'; 19 = 'switch_button_zl'
        10 = 'switch_stick_r_press'; 17 = 'switch_stick_l_press'
        9 = 'switch_capture'
    }
    foreach ($entry in $rightJoyConGlyphs.GetEnumerator()) {
        $actual = [string]$glyphName.Invoke($null, @([int]$entry.Key, $rightJoyCon))
        Assert-True ($actual -eq $entry.Value) `
            "Right Joy-Con button code $($entry.Key) selected '$actual' instead of '$($entry.Value)'."
    }
    Assert-True ($glyphName.Invoke($null, @(0, $leftJoyCon)) -eq 'switch_dpad_down') `
        'Left Joy-Con button code 0 must select the D-pad Down glyph.'
    Assert-True ($glyphName.Invoke($null, @(13, $proController)) -eq 'switch_button_b') `
        'Switch Pro button code 13 must select the B glyph.'
    Assert-True ($glyphName.Invoke($null, @(22, $dualSense)) -eq 'touch_two') `
        'Two-finger tap must select the official Kenney two-finger glyph.'
    Assert-True ($glyphName.Invoke($null, @(23, $dualSense)) -eq 'touch_swipe_two_up') `
        'Two-finger scroll up must select the official Kenney upward swipe glyph.'
    Assert-True ($glyphName.Invoke($null, @(24, $dualSense)) -eq 'touch_swipe_two_down') `
        'Two-finger scroll down must select the official Kenney downward swipe glyph.'

    # User contract: standard keyboard keys use Mr. Breakfast's light keycaps directly from the
    # Windows virtual-key value. Unsupported or ambiguous keys retain their text label.
    $keyboardGlyphName = $reassignType.GetMethod(
        'KeyboardKeyGlyphName', [Reflection.BindingFlags]'NonPublic,Static')
    $keyboardGlyphs = @{
        8 = 'backspace_light'; 9 = 'tab_light'; 13 = 'return_light'
        16 = 'shift_light'; 17 = 'control_light'; 18 = 'alt_light'; 27 = 'escape_light'
        32 = 'space_text_light'; 37 = 'arrow_left_light'; 46 = 'delete_light'
        48 = '0_light'; 65 = 'a_key_light'; 67 = 'c_light'; 88 = 'x_key_light'
        91 = 'super_light'; 112 = 'f1_light'; 123 = 'f12_light'
        144 = 'num_light'; 145 = 'scroll_light'; 186 = ';_light'; 222 = "'_light"
    }
    foreach ($entry in $keyboardGlyphs.GetEnumerator()) {
        $actual = [string]$keyboardGlyphName.Invoke($null, @([int]$entry.Key))
        Assert-True ($actual -eq $entry.Value) `
            "Keyboard key code $($entry.Key) selected '$actual' instead of '$($entry.Value)'."
    }
    Assert-True ($null -eq $keyboardGlyphName.Invoke($null, @(135))) `
        'F24 must retain its text label because the selected set only includes F1-F12.'
    Assert-True ($null -eq $keyboardGlyphName.Invoke($null, @(96))) `
        'Numpad 0 must retain its text label because the set has no numpad-specific zero glyph.'
    $keyboardGlyph = $reassignType.GetMethod(
        'KeyboardKeyGlyph', [Reflection.BindingFlags]'NonPublic,Static')
    Assert-True ($null -ne $keyboardGlyph.Invoke($null, @(17))) `
        'The embedded Ctrl key glyph could not be loaded.'
    Assert-True ($null -eq $keyboardGlyph.Invoke($null, @(135))) `
        'Unsupported keyboard keys must not load an unrelated glyph.'

    $labelParts = $reassignType.GetMethod('BindLabelParts', [Reflection.BindingFlags]'NonPublic,Static')
    $parts = $labelParts.Invoke($null, @('joy_7+joy_12', $dualSense, $null))
    Assert-True ($parts.Count -eq 3 -and $parts[0] -is [Drawing.Image] -and $parts[1] -eq '+' -and
        $parts[2] -is [Drawing.Image]) 'PS + L2 must display as the PS glyph, +, and the L2 glyph.'
    $parts = $labelParts.Invoke($null, @('joy_9+joy_12', $dualSense, $null))
    Assert-True ($parts.Count -eq 3 -and $parts[0] -eq 'CAPTURE' -and $parts[1] -eq '+' -and
        $parts[2] -is [Drawing.Image]) 'A button without a glyph must keep its text beside glyphs.'
    $parts = $labelParts.Invoke($null, @('key_17+key_18+key_46', $dualSense, $null))
    Assert-True ($parts.Count -eq 5 -and $parts[0] -is [Drawing.Image] -and
        $parts[1] -eq '+' -and $parts[2] -is [Drawing.Image] -and $parts[3] -eq '+' -and
        $parts[4] -is [Drawing.Image]) 'Ctrl + Alt + Delete must display as three keyboard glyphs.'

    # User contract: named Windows output presets on Custom binds use the same keyboard glyphs
    # as captured key_* outputs without changing their stable act_* stored values.
    $presetDisplayBinding = $mappingsType.GetMethod('CustomActionDisplayBinding')
    $presetBindings = @{
        'act_ctrl_alt_delete' = 'key_17+key_18+key_46'
        'act_ctrl_shift_escape' = 'key_17+key_16+key_27'
        'act_alt_tab_next' = 'key_18+key_9'
        'act_alt_shift_tab_previous' = 'key_18+key_16+key_9'
        'act_alt_tab_left' = 'key_18+key_9+key_37'
        'act_alt_tab_right' = 'key_18+key_9+key_39'
    }
    foreach ($entry in $presetBindings.GetEnumerator()) {
        $displayBinding = [string]$presetDisplayBinding.Invoke($null, @($entry.Key))
        Assert-True ($displayBinding -eq $entry.Value) `
            "$($entry.Key) selected '$displayBinding' instead of '$($entry.Value)'."
        $parts = $labelParts.Invoke($null, @($displayBinding, $dualSense, $null))
        Assert-True ($null -ne $parts -and ($parts | Where-Object { $_ -is [Drawing.Image] }).Count -gt 0) `
            "$($entry.Key) must resolve to keyboard glyphs on the Custom binds output button."
    }
    Assert-True ($null -eq $presetDisplayBinding.Invoke($null, @('act_media_play'))) `
        'Media presets must retain their text labels when no keyboard glyph sequence applies.'

    # Exercise the actual Custom binds output-label path, not only its two helpers.
    # The application normally initializes AppPaths before this UI method runs. Keep this
    # isolated regression process on ControllerMappings' empty in-memory store so it never reads
    # or writes the user's real profile file.
    $mappingsType.GetField(
        'loaded', [Reflection.BindingFlags]'Static,NonPublic').SetValue($null, $true)
    $reassign = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($reassignType)
    $toolTip = New-Object Windows.Forms.ToolTip
    $reassignType.GetField(
        'tip_reassign', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($reassign, $toolTip)
    $splitButtonType = $assembly.GetType('BetterJoyForCemu.SplitButton', $true)
    $outputButton = [Activator]::CreateInstance($splitButtonType)
    $setPrettyName = $reassignType.GetMethod(
        'SetCustomBindingPrettyName', [Reflection.BindingFlags]'Instance,NonPublic')
    $labelPartsField = $splitButtonType.GetField(
        'labelParts', [Reflection.BindingFlags]'Instance,NonPublic')
    $setPrettyName.Invoke($reassign, @($outputButton, 'act_ctrl_alt_delete'))
    $outputParts = $labelPartsField.GetValue($outputButton)
    Assert-True ($outputParts.Count -eq 5 -and
        ($outputParts | Where-Object { $_ -is [Drawing.Image] }).Count -eq 3) `
        'The Custom binds output button did not render Ctrl + Alt + Delete as three glyphs.'
    $setPrettyName.Invoke($reassign, @($outputButton, 'act_media_play'))
    Assert-True ($null -eq $labelPartsField.GetValue($outputButton) -and $outputButton.Text -eq 'Play') `
        'A text-only media output retained stale keyboard glyphs.'
    $outputButton.Dispose()
    $toolTip.Dispose()
    Assert-True ($null -eq $labelParts.Invoke($null, @('key_135', $dualSense, $null))) `
        'Unsupported keyboard-only binds must keep their plain text label.'
    $parts = $labelParts.Invoke($null, @('joy_0+joy_11', $rightJoyCon, $null))
    Assert-True ($parts.Count -eq 3 -and $parts[0] -is [Drawing.Image] -and $parts[1] -eq '+' -and
        $parts[2] -is [Drawing.Image]) 'Right Joy-Con B + R must display as two model-specific glyphs.'

    Write-Host 'Custom Rebind regression tests passed.'
} finally {
    Pop-Location
}
