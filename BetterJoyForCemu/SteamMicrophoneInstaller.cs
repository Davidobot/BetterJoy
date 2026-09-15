using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterJoyForCemu {
    // Creates/binds BetterJoy's own root-enumerated Steam Streaming Microphone device instance -
    // called from EntryPoint.cs via the "-installsteammic" flag, launched by Installer/BetterJoy.iss.
    //
    // This has to run from BetterJoy2.exe itself (a native x64 build) rather than directly
    // from the Inno Setup script: Setup.exe is always a 32-bit (WOW64) process regardless of
    // ArchitecturesInstallIn64BitMode (confirmed by inspecting its actual PE header), so SetupAPI
    // calls made from Pascal Script hit the WOW64-redirected 32-bit copies of setupapi.dll/
    // newdev.dll - which don't reliably install a native x64 kernel driver. Running from this
    // already-native-x64 exe avoids that entirely.
    //
    // The bundled INF/CAT are Valve's own, byte-for-byte unmodified copies (editing them breaks
    // the CAT's file-hash signature - see SteamMicrophoneEndpoint.cs), so this always targets
    // Steam's own hardware ID, but as BetterJoy's own separate device instance (root-enumerated
    // devices support multiple instances per hardware ID, confirmed on real hardware) - never
    // reusing an instance Steam itself created for its own Remote Play/Link voice forwarding.
    // OwnerMarker tags which instance is BetterJoy's own, since both would otherwise share the
    // same hardware ID and, until renamed, the same default name.
    internal static class SteamMicrophoneInstaller {
        public const string HardwareId = @"ROOT\SteamStreamingMicrophone";
        public const string DeviceName = "SteamStreamingMicrophone";
        // No migration for an already-installed devnode still carrying the old marker - if that
        // ever matters again, re-run the "steammic" installer task to recreate it under this one.
        public const string OwnerMarker = "BetterJoy2 Microphone";

        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DICD_GENERATE_ID = 0x00000001;
        private const uint SPDRP_HARDWAREID = 0x00000001;
        private const uint SPDRP_FRIENDLYNAME = 0x0000000C;
        private const uint DIF_REGISTERDEVICE = 0x00000019;
        private const uint DIF_REMOVE = 0x00000005;
        private const uint INSTALLFLAG_FORCE = 0x00000001;
        private const uint INSTALLFLAG_NONINTERACTIVE = 0x00000004;

        private static readonly Guid MediaClassGuid = new Guid("4d36e96c-e325-11ce-bfc1-08002be10318");

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVINFO_DATA {
            public uint cbSize;
            public Guid classGuid;
            public uint devInst;
            public IntPtr reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator,
            IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex,
            ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiCreateDeviceInfoW(IntPtr deviceInfoSet, string deviceName,
            ref Guid classGuid, string deviceDescription, IntPtr hwndParent, uint creationFlags,
            ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool SetupDiOpenDeviceInfoW(IntPtr deviceInfoSet, string deviceInstanceId,
            IntPtr hwndParent, uint openFlags, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData, uint property, byte[] propertyBuffer, uint propertyBufferSize);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData, uint property, out uint propertyRegDataType,
            byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string hardwareId,
            string fullInfPath, uint installFlags, ref bool rebootRequired);

        [DllImport("cfgmgr32.dll")]
        internal static extern int CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int bufferLen, uint flags);

        // Exit codes: 0 = already installed or freshly installed with no reboot needed,
        // 3010 = freshly installed, reboot needed (matches the ERROR_SUCCESS_REBOOT_REQUIRED
        // convention BetterJoy.iss's NeedsRestart() already uses for the other bundled drivers),
        // 1 = failed.
        public static int Install(string infPath) {
            try {
                if (OwnInstanceExists())
                    return 0;
                if (!CreateAndBind(infPath, out bool rebootRequired))
                    return 1;
                return rebootRequired ? 3010 : 0;
            } catch {
                return 1;
            }
        }

        private static bool OwnInstanceExists() {
            Guid classGuid = MediaClassGuid;
            IntPtr devInfoSet = SetupDiGetClassDevsW(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (devInfoSet == IntPtr.Zero)
                return false;

            try {
                var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (uint index = 0; SetupDiEnumDeviceInfo(devInfoSet, index, ref info); index++) {
                    if (HasProperty(devInfoSet, ref info, SPDRP_HARDWAREID, HardwareId) &&
                            HasProperty(devInfoSet, ref info, SPDRP_FRIENDLYNAME, OwnerMarker))
                        return true;
                    info.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();
                }
                return false;
            } finally {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
        }

        private static bool HasProperty(IntPtr devInfoSet, ref SP_DEVINFO_DATA info, uint property,
                string expectedValue) {
            var buffer = new byte[512];
            if (!SetupDiGetDeviceRegistryPropertyW(devInfoSet, ref info, property, out _, buffer,
                    (uint)buffer.Length, out uint requiredSize) || requiredSize < 2)
                return false;

            string value = Encoding.Unicode.GetString(buffer, 0, (int)requiredSize).TrimEnd('\0');
            return string.Equals(value, expectedValue, StringComparison.OrdinalIgnoreCase);
        }

        private static bool CreateAndBind(string infPath, out bool rebootRequired) {
            rebootRequired = false;
            IntPtr devInfoSet = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (devInfoSet == IntPtr.Zero)
                return false;

            try {
                Guid classGuid = MediaClassGuid;
                var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiCreateDeviceInfoW(devInfoSet, DeviceName, ref classGuid, "", IntPtr.Zero,
                        DICD_GENERATE_ID, ref info))
                    return false;

                byte[] hwidBytes = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
                if (!SetupDiSetDeviceRegistryPropertyW(devInfoSet, ref info, SPDRP_HARDWAREID, hwidBytes,
                        (uint)hwidBytes.Length))
                    return false;

                if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, devInfoSet, ref info))
                    return false;

                bool bound = UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, HardwareId, infPath,
                    INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE, ref rebootRequired);

                if (!bound) {
                    SetupDiCallClassInstaller(DIF_REMOVE, devInfoSet, ref info);
                    return false;
                }

                // Tag this instance as BetterJoy's own so OwnInstanceExists (and
                // SteamMicrophoneEndpoint.cs at runtime) can find it again without ever touching a
                // separate instance Steam itself created. Best-effort: if this fails, the device
                // still works fine under its default name - not worth rolling the install back for.
                byte[] markerBytes = Encoding.Unicode.GetBytes(OwnerMarker + "\0");
                SetupDiSetDeviceRegistryPropertyW(devInfoSet, ref info, SPDRP_FRIENDLYNAME, markerBytes,
                    (uint)markerBytes.Length);

                return true;
            } finally {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
        }
    }
}
