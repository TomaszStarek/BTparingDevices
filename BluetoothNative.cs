using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BTparingDevices;

// ============================================================
// Natywne Windows Bluetooth API przez P/Invoke
// ============================================================
public static class BluetoothNative
{
    [StructLayout(LayoutKind.Sequential)]
    struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public uint dwSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public uint dwSize;
        public bool fReturnAuthenticated;
        public bool fReturnRemembered;
        public bool fReturnUnknown;
        public bool fReturnConnected;
        public bool fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day;
        public ushort Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        public bool fConnected;
        public bool fRemembered;
        public bool fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern bool BluetoothFindRadioClose(IntPtr hFind);

    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern uint BluetoothAuthenticateDevice(IntPtr hwndParent, IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi,
        [MarshalAs(UnmanagedType.LPWStr)] string? pszPasskey, uint ulPasskeyLength);

    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern uint BluetoothSetServiceState(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi,
        ref Guid pGuidService, uint dwServiceFlags);

    // ---- Automatyczna akceptacja parowania (bez okna "Pair Device - Allow/Cancel") ----
    // Windows domyślnie pokazuje to okno, gdy ŻADNA aplikacja nie zarejestrowała własnej
    // obsługi zapytania o parowanie (Secure Simple Pairing "Just Works"). Rejestrując
    // własny callback PRZED wywołaniem BluetoothAuthenticateDevice, przejmujemy to
    // zapytanie i odpowiadamy na nie programowo - okno się wtedy nie pojawia.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate bool PFN_AUTHENTICATION_CALLBACK_EX(IntPtr pvParam, ref BLUETOOTH_AUTHENTICATION_CALLBACK_PARAMS pAuthParams);

    [StructLayout(LayoutKind.Sequential)]
    struct BLUETOOTH_AUTHENTICATION_CALLBACK_PARAMS
    {
        public BLUETOOTH_DEVICE_INFO deviceInfo;
        public uint authenticationMethod;
        public uint ioCapability;
        public uint authenticationRequirements;
        public uint Numeric_Value_Passkey;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct BLUETOOTH_AUTHENTICATE_RESPONSE
    {
        [FieldOffset(0)] public ulong bthAddressRemote;
        [FieldOffset(8)] public uint authMethod;
        [FieldOffset(12)] public uint numericValueOrPasskey;
        [FieldOffset(44)] public int negativeResponse;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern uint BluetoothRegisterForAuthenticationEx(ref BLUETOOTH_DEVICE_INFO pbtdiIn, out IntPtr phRegHandle,
        PFN_AUTHENTICATION_CALLBACK_EX pfnCallbackIn, IntPtr pvParam);

    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern uint BluetoothUnregisterAuthentication(IntPtr hRegHandle);

    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern uint BluetoothSendAuthenticationResponseEx(IntPtr hRadioIn, ref BLUETOOTH_AUTHENTICATE_RESPONSE pauthResponse);

    static bool AutoAcceptCallback(IntPtr pvParam, ref BLUETOOTH_AUTHENTICATION_CALLBACK_PARAMS p)
    {
        var resp = new BLUETOOTH_AUTHENTICATE_RESPONSE
        {
            bthAddressRemote = p.deviceInfo.Address,
            authMethod = p.authenticationMethod,
            numericValueOrPasskey = p.Numeric_Value_Passkey,
            negativeResponse = 0 // 0 = akceptuj parowanie
        };
        BluetoothSendAuthenticationResponseEx(IntPtr.Zero, ref resp);
        return true;
    }

    static readonly Guid SPP_SERVICE_GUID = new("00001101-0000-1000-8000-00805F9B34FB");
    const uint BLUETOOTH_SERVICE_ENABLE = 0x00000001;

    public record BluetoothDevice(string Name, string Address, ulong AddressLong);

    public static List<BluetoothDevice> DiscoverDevices()
    {
        var result = new List<BluetoothDevice>();

        // Znajdź radio
        var radioParams = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        IntPtr hRadioFind = BluetoothFindFirstRadio(ref radioParams, out IntPtr hRadio);
        if (hRadioFind == IntPtr.Zero)
        {
            return result;
        }
        BluetoothFindRadioClose(hRadioFind);

        // Szukaj urządzeń
        var searchParams = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
            fReturnAuthenticated = true,
            fReturnRemembered = true,
            fReturnUnknown = true,      // nieznane = nieosparowane (to nas interesuje)
            fReturnConnected = true,
            fIssueInquiry = true,       // aktywne skanowanie
            cTimeoutMultiplier = 4,     // ~5 sekund (każda jednostka = ~1.28s)
            hRadio = hRadio
        };

        var deviceInfo = new BLUETOOTH_DEVICE_INFO
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>()
        };

        IntPtr hDevFind = BluetoothFindFirstDevice(ref searchParams, ref deviceInfo);
        if (hDevFind == IntPtr.Zero)
            return result;

        do
        {
            string addr = string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                (deviceInfo.Address >> 40) & 0xFF,
                (deviceInfo.Address >> 32) & 0xFF,
                (deviceInfo.Address >> 24) & 0xFF,
                (deviceInfo.Address >> 16) & 0xFF,
                (deviceInfo.Address >> 8) & 0xFF,
                deviceInfo.Address & 0xFF);

            result.Add(new BluetoothDevice(
                deviceInfo.szName ?? "Nieznane",
                addr,
                deviceInfo.Address));

        } while (BluetoothFindNextDevice(hDevFind, ref deviceInfo));

        BluetoothFindDeviceClose(hDevFind);
        return result;
    }

    public static bool PairDevice(ulong address, string pin)
    {
        var radioParams = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        IntPtr hRadioFind = BluetoothFindFirstRadio(ref radioParams, out IntPtr hRadio);
        if (hRadioFind == IntPtr.Zero) return false;
        BluetoothFindRadioClose(hRadioFind);

        var deviceInfo = new BLUETOOTH_DEVICE_INFO
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(),
            Address = address
        };

        // Zarejestruj auto-akceptację PRZED próbą parowania - dzięki temu Windows
        // nie pokaże okna "Pair Device - Allow/Cancel", tylko odda sterowanie nam.
        PFN_AUTHENTICATION_CALLBACK_EX callback = AutoAcceptCallback;
        uint regResult = BluetoothRegisterForAuthenticationEx(ref deviceInfo, out IntPtr hAuthReg, callback, IntPtr.Zero);

        // Paruj z PIN-em
        uint result = BluetoothAuthenticateDevice(
            IntPtr.Zero,
            hRadio,
            ref deviceInfo,
            pin,
            (uint)pin.Length);

        if (regResult == 0)
            BluetoothUnregisterAuthentication(hAuthReg);
        GC.KeepAlive(callback);

        if (result == 0) // ERROR_SUCCESS
        {
            // Włącz usługę SPP
            var sppGuid = SPP_SERVICE_GUID;
            BluetoothSetServiceState(hRadio, ref deviceInfo, ref sppGuid, BLUETOOTH_SERVICE_ENABLE);
            return true;
        }

        return false;
    }
}
