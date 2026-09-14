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

    # User contract: Rebind consumption removes the original normalized physical input before
    # normal physical-to-virtual mapping. Legacy continuous remaps remain additive here; Custom
    # bind controller outputs are tested separately as direct virtual-report buttons below.
    [bool[]]$output = New-Object bool[] $buttonCount
    [bool[]]$consumed = New-Object bool[] $buttonCount
    [bool[]]$remapped = New-Object bool[] $buttonCount
    $output[13] = $true
    $consumed[13] = $true
    $applyOverrides.Invoke($null, @($output, $consumed, $remapped))
    Assert-True (-not $output[13]) `
        'Rebind consumption did not remove the original physical B input.'

    # Regression: the pre-existing continuous physical remap path is unchanged.
    [bool[]]$output = New-Object bool[] $buttonCount
    [bool[]]$consumed = New-Object bool[] $buttonCount
    [bool[]]$remapped = New-Object bool[] $buttonCount
    $remapped[15] = $true
    $applyOverrides.Invoke($null, @($output, $consumed, $remapped))
    Assert-True ($output[15]) 'Legacy continuous remap output was no longer additive.'

    # User contract: joy_* in a Custom bind output is a direct virtual target. Position 15 is
    # Xbox X / PlayStation Square; it must never pass through the physical Y -> virtual X mapping
    # a second time, and the original virtual state remains additive when Rebind is Disabled.
    $xboxStateType = $assembly.GetType(
        'BetterJoyForCemu.VirtualOutput.OutputControllerXbox360InputState', $true)
    $psStateType = $assembly.GetType(
        'BetterJoyForCemu.VirtualOutput.OutputControllerDualShock4InputState', $true)
    $applyXboxVirtual = $controllerType.GetMethod(
        'ApplyCustomXboxVirtualButtons', [Reflection.BindingFlags]'NonPublic,Static')
    $applyPsVirtual = $controllerType.GetMethod(
        'ApplyCustomPlayStationVirtualButtons', [Reflection.BindingFlags]'NonPublic,Static')
    [bool[]]$virtualButtons = New-Object bool[] $buttonCount
    $virtualButtons[15] = $true
    $xboxState = [Activator]::CreateInstance($xboxStateType)
    $xboxState.a = $true
    $xboxResult = $applyXboxVirtual.Invoke($null, @($xboxState, $virtualButtons))
    Assert-True ($xboxResult.a -and $xboxResult.x -and -not $xboxResult.y) `
        'Virtual position 15 did not directly emit Xbox X while preserving existing Xbox A.'
    $psState = [Activator]::CreateInstance($psStateType)
    $psState.cross = $true
    $psResult = $applyPsVirtual.Invoke($null, @($psState, $virtualButtons))
    Assert-True ($psResult.cross -and $psResult.square -and -not $psResult.triangle) `
        'Virtual position 15 did not directly emit PlayStation Square while preserving Cross.'

    [bool[]]$virtualButtons = New-Object bool[] $buttonCount
    $virtualButtons[12] = $true
    $virtualButtons[19] = $true
    $xboxResult = $applyXboxVirtual.Invoke(
        $null, @([Activator]::CreateInstance($xboxStateType), $virtualButtons))
    Assert-True ($xboxResult.trigger_left -eq 255 -and $xboxResult.trigger_right -eq 255) `
        'Virtual LT/RT did not produce full Xbox trigger values.'
    $psResult = $applyPsVirtual.Invoke(
        $null, @([Activator]::CreateInstance($psStateType), $virtualButtons))
    Assert-True ($psResult.trigger_left -and $psResult.trigger_right -and
        $psResult.trigger_left_value -eq 255 -and $psResult.trigger_right_value -eq 255) `
        'Virtual L2/R2 did not produce digital and analog PlayStation trigger output.'

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

    # User contract: output recording starts from the normalized physical code and stores the
    # button produced by that controller's default layout. Dual-stick PlayStation Square remains
    # position 15 (Xbox X / PlayStation Square); sideways Joy-Con rotation is resolved once at
    # capture time rather than being deferred to output emission.
    $defaultVirtualButton = $reassignType.GetMethod(
        'DefaultVirtualButtonCode', [Reflection.BindingFlags]'NonPublic,Static')
    Assert-True ($defaultVirtualButton.Invoke($null, @(15, $dualSense, 'dualsense:test')) -eq 15) `
        'DualSense Square did not record its default virtual west-face position.'
    Assert-True ($defaultVirtualButton.Invoke($null, @(0, $rightJoyCon, 'solo-right:test')) -eq 15) `
        'A sideways right Joy-Con B press did not record the default rotated virtual X position.'
    Assert-True ($defaultVirtualButton.Invoke($null, @(15, $null, 'pair:left+right')) -eq 15) `
        'A joined Joy-Con pair changed an already-normalized virtual button position.'

    # User contract: the same stored virtual position is presented for the selected Use-as
    # controller, never as another physical controller button. Position 15 is Xbox X,
    # DualShock/DualSense Square; position 13 is Xbox A, PlayStation Cross.
    $virtualDisplayName = $reassignType.GetMethod(
        'VirtualControllerButtonDisplayName', [Reflection.BindingFlags]'NonPublic,Static')
    Assert-True ($virtualDisplayName.Invoke($null, @(15, 'xbox360')) -eq 'X') `
        'Virtual position 15 was not labeled Xbox X.'
    Assert-True ($virtualDisplayName.Invoke($null, @(13, 'xbox360')) -eq 'A') `
        'Virtual position 13 was not labeled Xbox A.'
    Assert-True ($virtualDisplayName.Invoke($null, @(15, 'dualshock4')) -eq 'SQUARE') `
        'Virtual position 15 was not labeled DualShock Square.'
    Assert-True ($virtualDisplayName.Invoke($null, @(6, 'dualsense_viiper')) -eq 'CREATE') `
        'Virtual position 6 was not labeled DualSense Create.'
    $virtualCodes = $reassignType.GetMethod(
        'VirtualControllerButtonCodes', [Reflection.BindingFlags]'NonPublic,Static')
    $xboxCodes = @($virtualCodes.Invoke($null, @('xbox360')))
    $playStationCodes = @($virtualCodes.Invoke($null, @('dualsense_viiper')))
    Assert-True ($xboxCodes -contains 15 -and $xboxCodes -notcontains 20 -and
        $xboxCodes -notcontains 25) `
        'Xbox output choices included physical-only buttons or omitted Xbox X.'
    Assert-True ($playStationCodes -contains 15 -and $playStationCodes -contains 20 -and
        $playStationCodes -notcontains 25) `
        'PlayStation output choices did not match the virtual controller surface.'

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

    # Output glyphs follow Use as, independently of the physical controller selected on the left.
    # These are the attributed, unmodified Mr. Breakfast Xbox prompts and existing Kenney PS art.
    $virtualGlyph = $reassignType.GetMethod(
        'VirtualControllerButtonGlyph', [Reflection.BindingFlags]'NonPublic,Static')
    foreach ($code in @(0, 1, 2, 3, 6, 8, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19)) {
        Assert-True ($null -ne $virtualGlyph.Invoke($null, @([int]$code, 'xbox360'))) `
            "Xbox virtual button position $code has no embedded glyph."
    }
    Assert-True ($null -eq $virtualGlyph.Invoke($null, @(7, 'xbox360'))) `
        'Xbox Guide must keep its text label because the imported set has no Guide glyph.'
    foreach ($code in @(6, 8, 13, 14, 15, 16, 20)) {
        Assert-True ($null -ne $virtualGlyph.Invoke($null, @([int]$code, 'dualsense_viiper'))) `
            "DualSense virtual button position $code has no embedded glyph."
    }

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
    # Windows virtual-key value. The full numpad reuses the corresponding digit/operator art
    # because this source does not provide separate numpad variants.
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
    $numpadGlyphs = @{
        96 = '0_light'; 97 = '1_light'; 98 = '2_light'; 99 = '3_light'
        100 = '4_light'; 101 = '5_light'; 102 = '6_light'; 103 = '7_light'
        104 = '8_light'; 105 = '9_light'; 106 = 'asterisk_light'; 107 = '+_light'
        108 = ',_light'; 109 = '-_light'; 110 = '._light'; 111 = 'forward_slash_light'
    }
    foreach ($entry in $numpadGlyphs.GetEnumerator()) {
        $actual = [string]$keyboardGlyphName.Invoke($null, @([int]$entry.Key))
        Assert-True ($actual -eq $entry.Value) `
            "Numpad key code $($entry.Key) selected '$actual' instead of '$($entry.Value)'."
    }
    $keyboardGlyph = $reassignType.GetMethod(
        'KeyboardKeyGlyph', [Reflection.BindingFlags]'NonPublic,Static')
    Assert-True ($null -ne $keyboardGlyph.Invoke($null, @(17))) `
        'The embedded Ctrl key glyph could not be loaded.'
    Assert-True ($null -eq $keyboardGlyph.Invoke($null, @(135))) `
        'Unsupported keyboard keys must not load an unrelated glyph.'

    # Every supported standard Windows key code must resolve to an embedded image. The Apps /
    # context-menu key remains textual because the selected source has no identifiable glyph.
    $standardKeyboardCodes = @(
        8, 9, 13, 16, 17, 18, 19, 20, 27, 32, 33, 34, 35, 36, 37, 38, 39, 40,
        44, 45, 46
    ) + @(48..57) + @(65..90) + @(91, 92) + @(96..111) + @(112..123) + @(
        144, 145, 160, 161, 162, 163, 164, 165, 186, 187, 188, 189, 190, 191,
        192, 219, 220, 221, 222, 226
    )
    Assert-True (($standardKeyboardCodes | Sort-Object -Unique).Count -eq 107) `
        'The standard keyboard regression inventory unexpectedly changed.'
    foreach ($keyCode in $standardKeyboardCodes) {
        Assert-True ($null -ne $keyboardGlyph.Invoke($null, @([int]$keyCode))) `
            "Supported standard key code $keyCode did not load its embedded glyph."
    }
    Assert-True ($null -eq $keyboardGlyphName.Invoke($null, @(93))) `
        'The Apps key must stay textual until an identifiable source glyph is available.'

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

    # User contract: use the available Mr. Breakfast media art on Custom bind outputs while
    # actions without matching art retain their text labels.
    $customActionParts = $reassignType.GetMethod(
        'CustomActionLabelParts', [Reflection.BindingFlags]'NonPublic,Static')
    $findCustomAction = $mappingsType.GetMethod('FindCustomAction')
    $pauseChoice = $findCustomAction.Invoke($null, @('act_media_pause'))
    Assert-True ($pauseChoice.DisplayGlyphNames.Count -eq 1 -and
        $pauseChoice.DisplayGlyphNames[0] -eq 'pause_symbolic_light') `
        'The Pause preset descriptor must select pause_symbolic_light.png.'

    # Every future preset that declares glyph metadata must automatically resolve to embedded art.
    foreach ($choice in $mappingsType.GetField('CustomActionChoices').GetValue($null)) {
        foreach ($glyphName in @($choice.DisplayGlyphNames)) {
            if ([String]::IsNullOrEmpty($glyphName)) { continue }
            $stream = $assembly.GetManifestResourceStream("InputPrompts.$glyphName.png")
            Assert-True ($null -ne $stream) `
                "$($choice.Value) declares missing glyph resource $glyphName.png."
            $stream.Dispose()
        }
    }
    foreach ($value in @('act_media_play', 'act_media_stop',
            'act_media_next', 'act_media_previous')) {
        $parts = $customActionParts.Invoke($null, @($value, $dualSense, $null))
        Assert-True ($parts.Count -eq 1 -and $parts[0] -is [Drawing.Image]) `
            "$value must display its available media glyph."
    }
    $pauseParts = $customActionParts.Invoke($null, @('act_media_pause', $dualSense, $null))
    $symbolicPauseStream = $assembly.GetManifestResourceStream(
        'InputPrompts.pause_symbolic_light.png')
    Assert-True ($null -ne $symbolicPauseStream) `
        'The attributed symbolic Pause resource was not embedded.'
    $symbolicPauseImage = [Drawing.Image]::FromStream($symbolicPauseStream)
    Assert-True ($pauseParts.Count -eq 1 -and $pauseParts[0] -is [Drawing.Image] -and
        $pauseParts[0].Width -eq $symbolicPauseImage.Width -and
        $pauseParts[0].Height -eq $symbolicPauseImage.Height) `
        'Media Pause must display pause_symbolic_light.png.'
    $symbolicPauseImage.Dispose()
    $symbolicPauseStream.Dispose()
    $parts = $customActionParts.Invoke($null, @('act_media_play_pause', $dualSense, $null))
    Assert-True ($parts.Count -eq 3 -and $parts[0] -is [Drawing.Image] -and
        $parts[1] -eq ' / ' -and $parts[2] -is [Drawing.Image]) `
        'Play / Pause must compose the separate Play and Pause glyphs.'
    foreach ($value in @('act_volume_up', 'act_volume_down', 'act_volume_mute')) {
        Assert-True ($null -eq $customActionParts.Invoke($null, @($value, $dualSense, $null))) `
            "$value must retain text because the selected source has no matching glyph."
    }

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
    Assert-True ($labelPartsField.GetValue($outputButton).Count -eq 1) `
        'The Custom binds output button did not render the Play glyph.'
    $setPrettyName.Invoke($reassign, @($outputButton, 'act_volume_up'))
    Assert-True ($null -eq $labelPartsField.GetValue($outputButton) -and
        $outputButton.Text -eq 'Volume up') `
        'A text-only volume output retained stale media glyphs.'
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
