param(
    [string]$AssemblyPath = "$PSScriptRoot/../BetterJoyForCemu/bin/x64/Release/BetterJoy2.exe"
)

$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath).Path)
$padType = $assembly.GetType('BetterJoyForCemu.DualSenseController', $true)
$wake = $padType.GetMethod('IsChargeOnlyUsbWakeRequested', [Reflection.BindingFlags]'Static,NonPublic')
$script:checks = 0

function Assert-Wake([string]$Name, [byte[]]$Report, [int]$Received,
        [bool]$Repair, [bool]$Quiet, [bool]$Released, [bool]$Expected,
        [bool]$ExpectedReleased) {
    $arguments = [object[]]@($Report, $Received, $Repair, $Quiet, $Released)
    $actual = $wake.Invoke($null, $arguments)
    if ($actual -ne $Expected -or $arguments[4] -ne $ExpectedReleased) {
        throw "$Name : wake=$actual released=$($arguments[4])"
    }
    $script:checks++
}

[byte[]]$idle = New-Object byte[] 64
$idle[0] = 0x01
[byte[]]$ps = $idle.Clone()
$ps[10] = 0x01
[byte[]]$status = $idle.Clone()
$status[0] = 0x31

Assert-Wake 'Repair ignores charge status after silence' $status 64 $true $true $false $false $false
Assert-Wake 'Repair ignores ordinary input after silence' $idle 64 $true $true $false $false $true
Assert-Wake 'Repair accepts PS as first input after silence' $ps 64 $true $true $false $true $false
Assert-Wake 'Repair accepts released-to-pressed PS' $ps 64 $true $false $true $true $true
Assert-Wake 'Repair ignores initially held PS while streaming' $ps 64 $true $false $false $false $false
Assert-Wake 'Repair ignores truncated PS report' $ps 11 $true $true $false $false $false
Assert-Wake 'Repair ignores missing report' $null 64 $true $true $false $false $false
Assert-Wake 'Timeout is not a PS press' $ps 0 $true $true $false $false $false
Assert-Wake 'Other sleep paths retain dormant-input wake' $status 64 $false $true $false $true $false
Assert-Wake 'Other sleep paths retain PS edge wake' $ps 64 $false $false $true $true $true
Assert-Wake 'Other sleep paths ignore streaming idle input' $idle 64 $false $false $false $false $true

Write-Output "Passed $script:checks DualSense repair wake checks."
