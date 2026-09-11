param(
    [string]$AssemblyPath = "$PSScriptRoot/../BetterJoyForCemu/bin/x64/Release/BetterJoy2.exe"
)

$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath).Path)
$padType = $assembly.GetType('BetterJoyForCemu.DualSenseController', $true)
$managerType = $assembly.GetType('BetterJoyForCemu.JoyconManager', $true)
$mappingType = $assembly.GetType('BetterJoyForCemu.ControllerMappings', $true)
$instanceFlags = [Reflection.BindingFlags]'Instance,NonPublic'
$staticFlags = [Reflection.BindingFlags]'Static,Public,NonPublic'
# Program reads these defaults during type initialization; keep them local to this test process.
$settings = [System.Configuration.ConfigurationManager]::AppSettings
$readOnly = [System.Collections.Specialized.NameObjectCollectionBase].GetProperty('IsReadOnly', $instanceFlags)
$readOnly.SetValue($settings, $false)
$settings.Set('UseHidHide', 'false')
$settings.Set('DebugLogging', 'false')
$settings.Set('AHRS_beta', '0')
$readOnly.SetValue($settings, $true)
$managerField = $assembly.GetType('BetterJoyForCemu.Program', $true).GetField('mgr', $staticFlags)
$complete = $padType.GetMethod('ConfirmFreshBluetoothPairing', $instanceFlags)
$fresh = $padType.GetField('freshBluetoothPairingPending', $instanceFlags)
$sleep = $padType.GetField('usbSleepOnConnectPending', $instanceFlags)
$stateField = $padType.GetField('state')
$mappingType.GetField('loaded', $staticFlags).SetValue($null, $true)
$profiles = $mappingType.GetField('profiles', $staticFlags).GetValue($null)
$radioType = $assembly.GetType('BetterJoyForCemu.BluetoothRadio', $true)
$radioType.GetField('radioAvailableEverChecked', $staticFlags).SetValue($null, $true)
$radioType.GetField('radioAvailableCheckedAt', $staticFlags).SetValue($null, [long]::MaxValue)
$checks = 0

# All profiles and controllers exist only in this process; no HID handles or saved settings.
foreach ($transport in @('usb', 'bluetooth')) {
    foreach ($radioAvailable in @($false, $true)) {
        $radioType.GetField('radioAvailableCached', $staticFlags).SetValue($null, $radioAvailable)
    foreach ($mode in @('enabled', 'bluetooth', 'disabled')) {
        foreach ($wasLive in @($false, $true)) {
            $managerField.SetValue($null, [Activator]::CreateInstance($managerType))
            $pad = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($padType)
            $padType.GetField('isUSB', $instanceFlags).SetValue($pad, $true)
            $padType.GetField('path').SetValue($pad, 'test-pairing-sleep')
            $padType.GetField('serial_number').SetValue($pad, '001122334455')
            $padType.GetField('PadMacAddress').SetValue($pad, [Net.NetworkInformation.PhysicalAddress]::Parse('001122334455'))
            $stateField.SetValue($pad, [Enum]::Parse($stateField.FieldType, 'IMU_DATA_OK'))
            $padType.GetField('usbSleepOnConnectInitialBluetoothStateKnown', $instanceFlags).SetValue($pad, $true)
            $padType.GetField('usbSleepOnConnectBluetoothWasLive', $instanceFlags).SetValue($pad, $wasLive)
            $fresh.SetValue($pad, $true)
            $profileId = $mappingType.GetMethod('ProfileIdFor').Invoke($null, [object[]]@($pad))
            $options = New-Object 'System.Collections.Generic.Dictionary[string,string]'
            $options['PreferredTransport'] = $transport
            $options['USBSleepOnConnect'] = $mode
            $profiles[$profileId] = $options

            $expected = !$wasLive -and ($mode -eq 'enabled' -or
                ($mode -eq 'bluetooth' -and $transport -eq 'bluetooth' -and $radioAvailable))
            $queued = $complete.Invoke($pad, @())
            if ($queued -ne $expected -or $sleep.GetValue($pad) -ne [int]$expected -or $fresh.GetValue($pad)) {
                throw "Completion failed: transport=$transport sleep=$mode bluetoothWasLive=$wasLive"
            }
            if ($complete.Invoke($pad, @())) { throw 'Completion queued sleep twice' }
            $checks++
        }
    }
    }
}

Write-Output "Passed $checks DualSense pairing completion checks, including Disabled and repeated confirmation."
