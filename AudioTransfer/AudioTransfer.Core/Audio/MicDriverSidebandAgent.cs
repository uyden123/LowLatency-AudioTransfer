using System;

using System.Runtime.InteropServices;
using System.Collections.Generic;
using Microsoft.Win32.SafeHandles;

namespace AudioTransfer.Core.Audio
{
    public sealed class MicDriverSidebandAgent : IDisposable
    {
        private static readonly Guid KSPROPSETID_VirtualMic_Sideband = new Guid("C22E60E6-764E-40E9-9630-67A01603F292");
        private const int KSPROPERTY_SIDEBAND_PUSH_MIC_DATA = 0;

        private const uint IOCTL_KS_PROPERTY = 0x002F0003;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;

        [StructLayout(LayoutKind.Sequential)]
        struct KSPROPERTY
        {
            public Guid Set;
            public uint Id;
            public uint Flags;
        }

        private SafeFileHandle? hDevice;
        private string? devicePath;

        public bool IsOpen => hDevice != null && !hDevice.IsInvalid;

        public MicDriverSidebandAgent()
        {
        }

        public bool Open()
        {
            // Find the device path for the Virtual Microphone
            // In a real scenario, we'd use SetupDiGetClassDevs with KSCATEGORY_AUDIO
            // For simplicity in this CLI, we'll try to find any device with our specific name or just iterate.
            // But since this is a virtual root device, let's try a known symbolic link pattern or use SetupDi.
            
            devicePath = FindDevicePath();
            if (string.IsNullOrEmpty(devicePath))
            {
                AudioTransfer.Core.Logging.CoreLogger.Instance.Log("[Sideband] Error: Could not find Virtual Audio Driver device path.");
                return false;
            }
            AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[Sideband] Found device path: {devicePath}");

            hDevice = CreateFile(devicePath, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (hDevice.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[Sideband] Error: Failed to open device. Win32 Error: {err}");
                return false;
            }
            AudioTransfer.Core.Logging.CoreLogger.Instance.Log("[Sideband] Device opened successfully.");

            return true;
        }

        public void PushData(byte[] data, int length)
        {
            if (!IsOpen) return;

            KSPROPERTY prop = new KSPROPERTY
            {
                Set = KSPROPSETID_VirtualMic_Sideband,
                Id = KSPROPERTY_SIDEBAND_PUSH_MIC_DATA,
                Flags = 2 // KSPROPERTY_TYPE_SET
            };

            int propSize = Marshal.SizeOf(prop);
            IntPtr propPtr = Marshal.AllocHGlobal(propSize);
            Marshal.StructureToPtr(prop, propPtr, false);

            IntPtr dataPtr = Marshal.AllocHGlobal(length);
            Marshal.Copy(data, 0, dataPtr, length);

            uint bytesReturned = 0;
            bool success = DeviceIoControl(hDevice!.DangerousGetHandle(), IOCTL_KS_PROPERTY, 
                propPtr, (uint)propSize, 
                dataPtr, (uint)length, 
                out bytesReturned, IntPtr.Zero);

            Marshal.FreeHGlobal(propPtr);
            Marshal.FreeHGlobal(dataPtr);

            if (!success)
            {
                // This is expected if the driver isn't loaded or doesn't support the IOCTL
                // For now, we'll just ignore or log once.
                AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[Sideband] Warning: Failed to send data to driver. Win32 Error: {Marshal.GetLastWin32Error()}");
            }
        }

        private string? FindDevicePath()
        {
            try
            {
                // Use KSCATEGORY_AUDIO to enumerate audio device interfaces
                Guid categoryAudio = new Guid("6994AD04-93EF-11D0-A3CC-00A0C9223196");

                IntPtr hDevInfo = SetupDiGetClassDevs(ref categoryAudio, null, IntPtr.Zero, 0x12); // DIGCF_DEVICEINTERFACE | DIGCF_PRESENT
                if (hDevInfo == (IntPtr)(-1)) return null;

                try
                {
                    SP_DEVICE_INTERFACE_DATA interfaceData = new SP_DEVICE_INTERFACE_DATA();
                    interfaceData.cbSize = (uint)Marshal.SizeOf(interfaceData);

                    for (uint i = 0; SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref categoryAudio, i, ref interfaceData); i++)
                    {
                        uint requiredSize = 0;
                        SetupDiGetDeviceInterfaceDetail(hDevInfo, ref interfaceData, IntPtr.Zero, 0, ref requiredSize, IntPtr.Zero);

                        if (requiredSize == 0) continue;

                        IntPtr detailPtr = Marshal.AllocHGlobal((int)requiredSize);
                        try
                        {
                            // Only write the cbSize header (4 bytes). Do NOT use StructureToPtr
                            // with the managed struct, as the fixed 256-char DevicePath field
                            // makes the managed struct larger than requiredSize, corrupting memory.
                            uint cbSize = (uint)(IntPtr.Size == 8 ? 8 : 6); // 4 + sizeof(WCHAR) padded to alignment
                            Marshal.WriteInt32(detailPtr, (int)cbSize);

                            if (SetupDiGetDeviceInterfaceDetail(hDevInfo, ref interfaceData, detailPtr, requiredSize, ref requiredSize, IntPtr.Zero))
                            {
                                // DevicePath starts at offset 4 (right after the DWORD cbSize)
                                string path = Marshal.PtrToStringUni(IntPtr.Add(detailPtr, 4))!;
                                if (path.IndexOf("WaveMicArray1", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    return path;
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(detailPtr);
                        }
                    }
                }
                finally
                {
                    SetupDiDestroyDeviceInfoList(hDevInfo);
                }

                return null;
            }
            catch (Exception ex)
            {
                AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[Sideband] Error in FindDevicePath: {ex.Message}");
                return null;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, ref uint RequiredSize, IntPtr DeviceInfoData);

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SP_DEVICE_INTERFACE_DETAIL_DATA
        {
            public uint cbSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string DevicePath;
        }

        public void Dispose()
        {
            hDevice?.Dispose();
        }
    }
}
