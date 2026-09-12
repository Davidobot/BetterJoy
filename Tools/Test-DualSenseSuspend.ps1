param(
    [string]$AssemblyPath = "$PSScriptRoot/../BetterJoyForCemu/bin/x64/Release/BetterJoy2.exe"
)

$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath).Path)

$usbType = $assembly.GetType('BetterJoyForCemu.UsbDeviceReenumerator', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic,Public'
$isDualSense = $usbType.GetMethod('IsDualSenseUsbDevice', $flags)
$portChecks = @(
    @('USB\VID_054C&PID_0CE6\CONTROLLER', $true),
    @('usb\vid_054c&pid_0df2&mi_03\controller', $true),
    @('USB\VID_054C&PID_05C4\DS4', $false),
    @('HID\VID_054C&PID_0DF2\CONTROLLER', $false),
    @('', $false)
)
foreach ($check in $portChecks) {
    $actual = $isDualSense.Invoke($null, @($check[0]))
    if ($actual -ne $check[1]) {
        throw "USB port-cycle target classification failed for '$($check[0])'"
    }
}

# Exercise shutdown/work races without opening a device or changing saved settings.
Add-Type -TypeDefinition @'
using System;
using System.Reflection;
using System.Threading;
public static class SuspendWorkChecks {
    public static void Run(Assembly assembly) {
        Type type = assembly.GetType("BetterJoyForCemu.JoyconManager", true);
        object manager = Activator.CreateInstance(type);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        MethodInfo run = type.GetMethod("RunControllerUsbWork", flags);
        MethodInfo stop = type.GetMethod("StopScanning", flags);
        MethodInfo wait = type.GetMethod("WaitForStop", flags);
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var stopped = new ManualResetEventSlim(false);
        Exception failure = null;
        int calls = 0;
        Action action = () => { Interlocked.Increment(ref calls); entered.Set(); release.Wait(); };
        var worker = new Thread(() => {
            try { run.Invoke(manager, new object[] { action }); }
            catch (Exception ex) { failure = ex; entered.Set(); }
        });
        var stopper = new Thread(() => {
            try { stop.Invoke(manager, null); }
            catch (Exception ex) { failure = ex; }
            finally { stopped.Set(); }
        });
        worker.IsBackground = stopper.IsBackground = true;
        worker.Start();
        try {
            if (!entered.Wait(2000)) throw new Exception("USB work did not start");
            stopper.Start();
            if (!(bool)wait.Invoke(manager, new object[] { 2000 }))
                throw new Exception("Stop did not signal cancellation");
            if (stopped.IsSet) throw new Exception("Stop returned while USB work still owned a handle");
            release.Set();
            if (!worker.Join(2000) || !stopped.Wait(2000)) throw new Exception("USB work did not drain");
            if (failure != null) throw failure;
            run.Invoke(manager, new object[] { new Action(() => calls++) });
            if (calls != 1) throw new Exception("Queued USB work ran after stopping");
            stop.Invoke(manager, null);
        } finally {
            release.Set();
            worker.Join(2000);
        }
    }
}
'@
[SuspendWorkChecks]::Run($assembly)
Write-Output 'Passed suspend port targeting and cancellation/drain checks. No device was opened.'
