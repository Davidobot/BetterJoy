using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterJoyForCemu {
    // Implements Microsoft KB Q165194 ("How To Launch an Interactive Process from a Windows
    // Service") - the only documented, working way for a Session-0 service to get code running
    // on the real desktop. Used by BetterJoyService to start the keyboard/mouse remap helper
    // (see InputHelper) inside whichever session is currently active, since Session 0 itself has
    // no desktop for WindowsInput's hooks/injection to attach to.
    internal static class SessionLauncher {
        private const int TOKEN_DUPLICATE = 0x0002;
        private const int TOKEN_QUERY = 0x0008;
        private const int TOKEN_ASSIGN_PRIMARY = 0x0001;
        private const int TOKEN_ADJUST_DEFAULT = 0x0080;
        private const int TOKEN_ADJUST_SESSIONID = 0x0100;
        private const int TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const int TOKEN_ALL_ACCESS = 0x000F01FF;

        private const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const int CREATE_NO_WINDOW = 0x08000000;

        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        private enum SECURITY_IMPERSONATION_LEVEL {
            SecurityAnonymous,
            SecurityIdentification,
            SecurityImpersonation,
            SecurityDelegation,
        }

        private enum TOKEN_TYPE {
            TokenPrimary = 1,
            TokenImpersonation,
        }

        // CharSet must be set here explicitly - DllImport's CharSet = Unicode on
        // CreateProcessAsUser (below) only governs string parameters passed directly; it does
        // NOT cascade into a struct passed by ref. Without this, lpDesktop marshals as ANSI
        // while CreateProcessAsUserW (the entry point actually resolved) reads STARTUPINFOW
        // expecting UTF-16 - a width mismatch that manifested as heap corruption
        // (STATUS_HEAP_CORRUPTION) rather than a clean failure.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID_AND_ATTRIBUTES {
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES {
            public int PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privileges;
        }

        // Despite living in the WTS* family, this one is actually exported by kernel32.dll, not
        // wtsapi32.dll - a well-known gotcha with this particular API.
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(
            IntPtr hExistingToken,
            uint dwDesiredAccess,
            IntPtr lpTokenAttributes,
            SECURITY_IMPERSONATION_LEVEL impersonationLevel,
            TOKEN_TYPE tokenType,
            out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(
            IntPtr hToken,
            string lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            int dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(
            IntPtr tokenHandle,
            bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES newState,
            int bufferLength,
            IntPtr previousState,
            IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        // LocalSystem holds SeTcbPrivilege/SeAssignPrimaryTokenPrivilege/SeIncreaseQuotaPrivilege
        // but they are not enabled by default - CreateProcessAsUser fails without them.
        private static void EnablePrivilege(IntPtr token, string privilegeName) {
            if (!LookupPrivilegeValue(null, privilegeName, out LUID luid))
                return;

            var privileges = new TOKEN_PRIVILEGES {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED },
            };

            AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }

        // Launches commandLine inside whichever session currently owns the physical
        // console/active desktop - returns false (no exception) for any of the many ordinary
        // reasons this can't happen right now (no one logged on yet, session switching mid-call,
        // etc.) since BetterJoyService just retries on the next session-change event.
        public static bool TryLaunchInActiveSession(string commandLine, out int processId) {
            processId = 0;
            IntPtr serviceToken = IntPtr.Zero;
            IntPtr userToken = IntPtr.Zero;
            IntPtr primaryToken = IntPtr.Zero;
            IntPtr environmentBlock = IntPtr.Zero;

            try {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out serviceToken))
                    return false;

                EnablePrivilege(serviceToken, "SeTcbPrivilege");
                EnablePrivilege(serviceToken, "SeAssignPrimaryTokenPrivilege");
                EnablePrivilege(serviceToken, "SeIncreaseQuotaPrivilege");

                uint sessionId = WTSGetActiveConsoleSessionId();
                if (sessionId == 0xFFFFFFFF)
                    return false; // no session attached to the console right now

                if (!WTSQueryUserToken(sessionId, out userToken))
                    return false; // no one logged on in that session yet

                if (!DuplicateTokenEx(
                        userToken,
                        TOKEN_ALL_ACCESS,
                        IntPtr.Zero,
                        SECURITY_IMPERSONATION_LEVEL.SecurityIdentification,
                        TOKEN_TYPE.TokenPrimary,
                        out primaryToken))
                    return false;

                var startupInfo = new STARTUPINFO();
                startupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                startupInfo.lpDesktop = "winsta0\\default";

                // lpCommandLine must be a mutable buffer per the Win32 docs (CreateProcess[AsUser]
                // can write a null terminator into it while splitting argv) - a plain interned
                // .NET string here risks corrupting that string everywhere else it's used.
                var mutableCommandLine = new StringBuilder(commandLine, commandLine.Length + 16);

                // Without a real per-user block here, CreateProcessAsUser (despite the
                // CREATE_UNICODE_ENVIRONMENT flag already being set below) gives the child
                // process the *calling* process's environment - this service's own SYSTEM
                // environment - even though it correctly runs under the target user's security
                // token. %APPDATA%/%USERPROFILE%/etc. would then resolve to SYSTEM's profile,
                // which the user's token has no write access to. InputHelper never surfaced this
                // (it only ever touches explicit ProgramData paths, never a %...% variable), but
                // it broke VIIPER outright: confirmed via the Windows Event Log and a live port
                // check - VIIPER's launch never produced a listening process at all, consistent
                // with it crashing immediately trying to write its key file under a resolved
                // %APPDATA% it has no access to. Best-effort: if this fails, fall through with
                // IntPtr.Zero exactly as before rather than aborting the whole launch.
                CreateEnvironmentBlock(out environmentBlock, primaryToken, false);

                bool created = CreateProcessAsUser(
                    primaryToken,
                    null,
                    mutableCommandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                    environmentBlock,
                    null,
                    ref startupInfo,
                    out PROCESS_INFORMATION processInfo);

                if (!created)
                    return false;

                processId = processInfo.dwProcessId;
                CloseHandle(processInfo.hProcess);
                CloseHandle(processInfo.hThread);
                return true;
            } finally {
                if (serviceToken != IntPtr.Zero) CloseHandle(serviceToken);
                if (userToken != IntPtr.Zero) CloseHandle(userToken);
                if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
                if (environmentBlock != IntPtr.Zero) DestroyEnvironmentBlock(environmentBlock);
            }
        }
    }
}
