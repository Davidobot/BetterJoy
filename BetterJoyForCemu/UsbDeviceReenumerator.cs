using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace BetterJoyForCemu {
    // Resolves a DualSense HID interface to its physical USB hub port before suspend, then asks
    // the hub driver to perform the same disconnect/reconnect transition as a cable replug.
    internal static class UsbDeviceReenumerator {
        private static readonly Guid UsbHubInterfaceGuid =
            new Guid("F18A0E88-C30C-11D0-8815-00A0C906BED8");
        private const uint CrSuccess = 0x00000000;
        private const uint CrBufferSmall = 0x0000001A;
        private const uint CmRemoveUiNotOk = 0x00000001;
        private const uint CmRemoveNoRestart = 0x00000002;
        private const uint IoctlUsbHubCyclePort = 0x00220444;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const int MaxHubPorts = 255;

        internal sealed class PortTarget {
            internal string HidPath { get; set; }
            internal string DeviceInstanceId { get; set; }
            internal string HubInstanceId { get; set; }
            internal string HubPath { get; set; }
            internal uint PortNumber { get; set; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CyclePortParameters {
            internal uint ConnectionIndex;
            internal uint StatusReturned;
        }

        private enum PnpVetoType {
            Ok,
            TypeUnknown,
            LegacyDevice,
            PendingClose,
            WindowsApp,
            WindowsService,
            OutstandingOpen,
            Device,
            Driver,
            IllegalDeviceRequest,
            InsufficientPower,
            NonDisableable,
            LegacyDriver,
            InsufficientRights
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_Interface_List_Size(
            out uint bufferLength, ref Guid interfaceClassGuid,
            string deviceInstanceId, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_Interface_List(
            ref Guid interfaceClassGuid, string deviceInstanceId,
            char[] buffer, uint bufferLength, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode,
            EntryPoint = "CM_Locate_DevNodeW")]
        private static extern uint CM_Locate_DevNode(
            out uint deviceInstance, string deviceInstanceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_Parent(
            out uint parentDeviceInstance, uint deviceInstance, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Reenumerate_DevNode(
            uint deviceInstance, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode,
            EntryPoint = "CM_Query_And_Remove_SubTreeW")]
        private static extern uint CM_Query_And_Remove_SubTree(
            uint deviceInstance, out PnpVetoType vetoType,
            StringBuilder vetoName, uint vetoNameLength, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle device, uint controlCode,
            ref CyclePortParameters inputBuffer, int inputBufferSize,
            ref CyclePortParameters outputBuffer, int outputBufferSize,
            out int bytesReturned, IntPtr overlapped);

        internal static bool IsDualSenseUsbDevice(string instanceId) {
            if (String.IsNullOrEmpty(instanceId))
                return false;
            return instanceId.StartsWith(@"USB\VID_054C&PID_0CE6",
                       StringComparison.OrdinalIgnoreCase) ||
                instanceId.StartsWith(@"USB\VID_054C&PID_0DF2",
                       StringComparison.OrdinalIgnoreCase);
        }

        // Ordinary in-session pseudo-sleep only needs a PnP nudge. Suspend uses the stronger hub
        // port cycle below after all application handles have been released.
        internal static bool TryReenumerateHidInterface(string hidPath,
                out string detail) {
            detail = "no path";
            if (String.IsNullOrWhiteSpace(hidPath))
                return false;

            try {
                IPnPDevice device = PnPDevice.GetDeviceByInterfaceId(
                    hidPath, DeviceLocationFlags.Normal);
                uint locate = CM_Locate_DevNode(out uint deviceInstance,
                    device.InstanceId, 0);
                if (locate != CrSuccess) {
                    detail = "CM_Locate_DevNode=0x" + locate.ToString("X8");
                    return false;
                }

                uint direct = CM_Reenumerate_DevNode(deviceInstance, 0);
                uint parentResult = UInt32.MaxValue;
                if (CM_Get_Parent(out uint parentDeviceInstance,
                        deviceInstance, 0) == CrSuccess)
                    parentResult = CM_Reenumerate_DevNode(parentDeviceInstance, 0);

                detail = "direct=0x" + direct.ToString("X8") +
                    " parent=0x" + parentResult.ToString("X8");
                return direct == CrSuccess || parentResult == CrSuccess;
            } catch (Exception ex) {
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        internal static bool TryResolveDualSensePort(string hidPath,
                out PortTarget target, out string detail) {
            target = null;
            detail = "no path";
            if (String.IsNullOrWhiteSpace(hidPath))
                return false;

            try {
                IPnPDevice device = PnPDevice.GetDeviceByInterfaceId(
                    hidPath, DeviceLocationFlags.Normal);
                IPnPDevice usbDevice = null;
                for (int depth = 0; device != null && depth < 10;
                        depth++, device = device.Parent) {
                    if (IsDualSenseUsbDevice(device.InstanceId))
                        usbDevice = device;
                    else if (usbDevice != null)
                        break;
                }
                if (usbDevice == null || usbDevice.Parent == null) {
                    detail = "Sony USB parent not found";
                    return false;
                }

                uint port = usbDevice.GetProperty<uint>(DevicePropertyKey.Device_Address);
                if (port == 0 || port > MaxHubPorts) {
                    detail = "invalid USB port " + port;
                    return false;
                }

                string hubPath = GetDeviceInterfacePath(
                    UsbHubInterfaceGuid, usbDevice.Parent.InstanceId);
                if (String.IsNullOrEmpty(hubPath)) {
                    detail = "hub interface not found for " +
                        usbDevice.Parent.InstanceId;
                    return false;
                }

                target = new PortTarget {
                    HidPath = hidPath,
                    DeviceInstanceId = usbDevice.InstanceId,
                    HubInstanceId = usbDevice.Parent.InstanceId,
                    HubPath = hubPath,
                    PortNumber = port
                };
                detail = "device=" + target.DeviceInstanceId +
                    " hub=" + target.HubInstanceId + " port=" + port;
                return true;
            } catch (Exception ex) {
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        internal static bool TryCyclePort(PortTarget target, out string detail) {
            detail = "no target";
            if (target == null || String.IsNullOrEmpty(target.HubPath) ||
                    target.PortNumber == 0 || target.PortNumber > MaxHubPorts)
                return false;

            try {
                // The target was captured while the HID node was still present. Revalidate it
                // immediately before the IOCTL so a cable move cannot cycle an unrelated port.
                IPnPDevice device = PnPDevice.GetDeviceByInstanceId(
                    target.DeviceInstanceId, DeviceLocationFlags.Normal);
                if (device == null || device.Parent == null ||
                        !String.Equals(device.Parent.InstanceId, target.HubInstanceId,
                            StringComparison.OrdinalIgnoreCase) ||
                        device.GetProperty<uint>(DevicePropertyKey.Device_Address) !=
                            target.PortNumber) {
                    detail = "USB topology changed before cycle";
                    return false;
                }

                using (SafeFileHandle hub = CreateFile(target.HubPath,
                        GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                        IntPtr.Zero, OpenExisting, 0, IntPtr.Zero)) {
                    if (hub == null || hub.IsInvalid) {
                        detail = "CreateFile failed error=" + Marshal.GetLastWin32Error();
                        return false;
                    }

                    var request = new CyclePortParameters {
                        ConnectionIndex = target.PortNumber
                    };
                    bool sent = DeviceIoControl(hub, IoctlUsbHubCyclePort,
                        ref request, Marshal.SizeOf(typeof(CyclePortParameters)),
                        ref request, Marshal.SizeOf(typeof(CyclePortParameters)),
                        out int bytesReturned, IntPtr.Zero);
                    detail = "device=" + target.DeviceInstanceId +
                        " hub=" + target.HubInstanceId +
                        " port=" + target.PortNumber +
                        " status=" + request.StatusReturned +
                        " bytes=" + bytesReturned +
                        (sent ? "" : " error=" + Marshal.GetLastWin32Error());

                    bool removed = TryRemoveDevNodeForSuspend(target,
                        out string removeDetail);
                    detail += " fallbackRemove=" + removed +
                        " fallbackDetail=" + removeDetail;
                    return removed;
                }
            } catch (Exception ex) {
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static bool TryRemoveDevNodeForSuspend(PortTarget target,
                out string detail) {
            detail = "no target";
            if (target == null ||
                    String.IsNullOrWhiteSpace(target.DeviceInstanceId))
                return false;

            uint locate = CM_Locate_DevNode(out uint deviceInstance,
                target.DeviceInstanceId, 0);
            if (locate != CrSuccess) {
                detail = "CM_Locate_DevNode=0x" + locate.ToString("X8");
                return false;
            }

            var vetoName = new StringBuilder(260);
            uint remove = CM_Query_And_Remove_SubTree(deviceInstance,
                out PnpVetoType vetoType, vetoName, (uint)vetoName.Capacity,
                CmRemoveUiNotOk | CmRemoveNoRestart);
            detail = "CM_Query_And_Remove_SubTree=0x" +
                remove.ToString("X8") + " veto=" + vetoType +
                (vetoName.Length > 0 ? " vetoName=" + vetoName : "");
            return remove == CrSuccess;
        }

        private static string GetDeviceInterfacePath(Guid interfaceGuid,
                string deviceInstanceId) {
            for (int attempt = 0; attempt < 3; attempt++) {
                uint result = CM_Get_Device_Interface_List_Size(out uint length,
                    ref interfaceGuid, deviceInstanceId, 0);
                if (result != CrSuccess || length <= 1)
                    return null;

                char[] buffer = new char[length];
                result = CM_Get_Device_Interface_List(ref interfaceGuid,
                    deviceInstanceId, buffer, length, 0);
                if (result == CrBufferSmall)
                    continue;
                if (result != CrSuccess)
                    return null;

                return new string(buffer).Split(new[] { '\0' },
                    StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            }
            return null;
        }
    }
}
