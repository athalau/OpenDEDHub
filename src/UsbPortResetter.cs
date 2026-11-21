using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenDEDHub
{
    internal static class UsbPortResetter
    {
        //private const string TargetVidPid = "VID_2E8A&PID_000A"; // Raspberry Pi Pico CDC
        //private static readonly string TargetVidPid = AppConfig.LoadTargetVidPid();

        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DIGCF_ALLCLASSES = 0x00000004;
        private const uint DIF_PROPERTYCHANGE = 0x00000012;
        private const uint DICS_PROPCHANGE = 0x00000003;
        private const uint DICS_FLAG_GLOBAL = 0x00000001;
        private const uint CM_REENUMERATE_SYNCHRONOUS = 0x00000001;
        private const uint CM_REENUMERATE_RETRY_INSTALLATION = 0x00000002;
        private const int CR_SUCCESS = 0x00000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_CLASSINSTALL_HEADER
        {
            public uint cbSize;
            public uint InstallFunction;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_PROPCHANGE_PARAMS
        {
            public SP_CLASSINSTALL_HEADER ClassInstallHeader;
            public uint StateChange;
            public uint Scope;
            public uint HwProfile;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(
            IntPtr ClassGuid,
            string Enumerator,
            IntPtr hwndParent,
            uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(
            IntPtr DeviceInfoSet,
            uint MemberIndex,
            ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInstanceId(
            IntPtr DeviceInfoSet,
            ref SP_DEVINFO_DATA DeviceInfoData,
            StringBuilder DeviceInstanceId,
            int DeviceInstanceIdSize,
            out int RequiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiSetClassInstallParams(
            IntPtr DeviceInfoSet,
            ref SP_DEVINFO_DATA DeviceInfoData,
            ref SP_PROPCHANGE_PARAMS ClassInstallParams,
            int ClassInstallParamsSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiCallClassInstaller(
            uint InstallFunction,
            IntPtr DeviceInfoSet,
            ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("cfgmgr32.dll", SetLastError = true)]
        private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", SetLastError = true)]
        private static extern int CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);

        public static bool ResetDedUsbDevice(Action<string> log)
        {
            string targetVidPid = ConfigLoader.GetTargetVidPid(log);

            IntPtr devInfoSet = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
            {
                log("USB Reset Error: SetupDiGetClassDevs failed.");
                return false;
            }

            try
            {
                SP_DEVINFO_DATA devInfoData = new SP_DEVINFO_DATA();
                devInfoData.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

                uint index = 0;
                while (SetupDiEnumDeviceInfo(devInfoSet, index, ref devInfoData))
                {
                    index++;
                    var idBuilder = new StringBuilder(512);
                    if (!SetupDiGetDeviceInstanceId(devInfoSet, ref devInfoData, idBuilder, idBuilder.Capacity, out _))
                        continue;

                    string instanceId = idBuilder.ToString();

                    // Match the root composite USB device (not the MI_00 child interface)
                    if (instanceId.Contains(targetVidPid, StringComparison.OrdinalIgnoreCase) &&
                        !instanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                    {
                        log($"Found target USB device: {instanceId}");

                        // Step 1: Issue DICS_PROPCHANGE (Forces the USB driver stack to cycle the device)
                        SP_PROPCHANGE_PARAMS propParams = new SP_PROPCHANGE_PARAMS
                        {
                            ClassInstallHeader = new SP_CLASSINSTALL_HEADER
                            {
                                cbSize = (uint)Marshal.SizeOf(typeof(SP_CLASSINSTALL_HEADER)),
                                InstallFunction = DIF_PROPERTYCHANGE
                            },
                            StateChange = DICS_PROPCHANGE,
                            Scope = DICS_FLAG_GLOBAL,
                            HwProfile = 0
                        };

                        bool propOk = SetupDiSetClassInstallParams(
                            devInfoSet,
                            ref devInfoData,
                            ref propParams,
                            Marshal.SizeOf(typeof(SP_PROPCHANGE_PARAMS))) &&
                            SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, devInfoSet, ref devInfoData);

                        // Step 2: Re-enumerate the parent USB hub node to force physical bus re-handshake
                        if (CM_Get_Parent(out uint parentDevInst, devInfoData.DevInst, 0) == CR_SUCCESS)
                        {
                            CM_Reenumerate_DevNode(parentDevInst, CM_REENUMERATE_SYNCHRONOUS | CM_REENUMERATE_RETRY_INSTALLATION);
                        }

                        log(propOk
                            ? "USB port cycle succeeded."
                            : $"USB port cycle returned Win32 code {Marshal.GetLastWin32Error()}.");
                        return propOk;
                    }
                }

                log($"USB Reset Warning: No device matching {targetVidPid} found on USB bus.");
                return false;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
        }
    }
}
