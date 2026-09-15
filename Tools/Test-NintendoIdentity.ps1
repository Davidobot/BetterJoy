param(
    [string]$AssemblyPath = "$PSScriptRoot/../BetterJoyForCemu/bin/x64/Release/BetterJoy2.exe"
)

$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath).Path)
$nintendoType = $assembly.GetType('BetterJoyForCemu.NintendoController', $true)
$parser = $nintendoType.GetMethod('TryParseIdentityReply', [Reflection.BindingFlags]'Static,NonPublic')
$profileMethod = $assembly.GetType('BetterJoyForCemu.ControllerMappings', $true).GetMethod('ProfileIdFor')
$proType = $assembly.GetType('BetterJoyForCemu.ProController', $true)
$expectedMac = '606BFF1F4EC0'
$script:checks = 0

function Assert-Reply([string]$Name, [byte[]]$Reply, [bool]$UsbStatus, [bool]$Expected) {
    $arguments = [object[]]@($Reply, $UsbStatus, $null)
    $accepted = $parser.Invoke($null, $arguments)
    if ($accepted -ne $Expected) { throw "$Name : accepted=$accepted, expected=$Expected" }
    if ($accepted) {
        $address = [Net.NetworkInformation.PhysicalAddress]::new([byte[]]$arguments[2])
        if ($address.ToString() -ne $expectedMac) { throw "$Name : incorrect MAC $address" }

        # Exercise profile identity without starting HID, virtual outputs, or reading settings.
        $controller = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($proType)
        $controller.isPro = $true
        $controller.PadMacAddress = $address
        $controller.serial_number = '000000000001'
        $controller.path = "test-$Name"
        $profile = $profileMethod.Invoke($null, [object[]]@($controller))
        if ($profile -ne 'pro:606bff1f4ec0') { throw "$Name : incorrect profile $profile" }
    } elseif ($null -ne $arguments[2]) {
        throw "$Name : invalid response leaked an address"
    }
    $script:checks++
}

[byte[]]$usb = 0x81, 0x01, 0x00, 0x03, 0xC0, 0x4E, 0x1F, 0xFF, 0x6B, 0x60
[byte[]]$info = New-Object byte[] 25
$info[0] = 0x21
$info[13] = 0x82
$info[14] = 0x02
[Array]::Copy([byte[]](0x60, 0x6B, 0xFF, 0x1F, 0x4E, 0xC0), 0, $info, 19, 6)
Assert-Reply 'USB status' $usb $true $true
Assert-Reply 'Device info after USB cycle' $info $false $true
Assert-Reply 'Short USB status' ([byte[]]$usb[0..8]) $true $false
Assert-Reply 'Short device info' ([byte[]]$info[0..23]) $false $false
Assert-Reply 'Missing response' $null $true $false
$wrongReport = $usb.Clone()
$wrongReport[0] = 0x30
Assert-Reply 'Streaming input instead of status' $wrongReport $true $false
$wrongCommand = $info.Clone()
$wrongCommand[14] = 0x01
Assert-Reply 'Pairing reply instead of device info' $wrongCommand $false $false
$nack = $info.Clone()
$nack[13] = 0
Assert-Reply 'Device info NACK' $nack $false $false
foreach ($invalid in @('000000000000', '000000000001', '010203040506', 'FFFFFFFFFFFF')) {
    $invalidUsb = $usb.Clone()
    $invalidInfo = $info.Clone()
    for ($i = 0; $i -lt 6; $i++) {
        $value = [Convert]::ToByte($invalid.Substring($i * 2, 2), 16)
        $invalidUsb[9 - $i] = $value
        $invalidInfo[19 + $i] = $value
    }
    Assert-Reply "Invalid USB MAC $invalid" $invalidUsb $true $false
    Assert-Reply "Invalid device-info MAC $invalid" $invalidInfo $false $false
}
Write-Output "Passed $script:checks Nintendo identity checks."
