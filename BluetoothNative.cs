using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

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
    const uint BLUETOOTH_SERVICE_DISABLE = 0x00000000;

    // ---- Zarządzanie numerem portu COM ----
    // Windows trzyma przydział portów w "COM Name Arbiter" (COMDB, msports.dll), a nazwę portu
    // urządzenia BT w rejestrze: ...\Enum\BTHENUM\{SPP}\<instancja>\Device Parameters\PortName.
    [DllImport("msports.dll")]
    static extern int ComDBOpen(out IntPtr phComDB);
    [DllImport("msports.dll")]
    static extern int ComDBClose(IntPtr hComDB);
    [DllImport("msports.dll")]
    static extern int ComDBGetCurrentPortUsage(IntPtr hComDB, byte[] buffer, uint bufferSize, uint reportType, out uint maxPortsReported);
    [DllImport("msports.dll")]
    static extern int ComDBClaimPort(IntPtr hComDB, uint comNumber, int forceClaim, out int forced);
    [DllImport("msports.dll")]
    static extern int ComDBReleasePort(IntPtr hComDB, uint comNumber);

    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern uint BluetoothGetDeviceInfo(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi);

    const string BthEnumPath = @"SYSTEM\CurrentControlSet\Enum\BTHENUM";

    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static int ParsePort(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) return 0;
        return int.TryParse(s.AsSpan(3), out int n) ? n : 0;
    }

    /// <summary>Numery portów COM zajętych (zarezerwowanych w COMDB lub aktywnych w SERIALCOMM).</summary>
    public static HashSet<int> GetUsedComPorts()
    {
        var used = new HashSet<int>();

        if (ComDBOpen(out IntPtr h) == 0)
        {
            try
            {
                var buf = new byte[4096];
                if (ComDBGetCurrentPortUsage(h, buf, (uint)buf.Length, 1 /* CDB_REPORT_BYTES */, out _) == 0)
                {
                    for (int i = 0; i < buf.Length; i++)
                        if (buf[i] != 0) used.Add(i + 1);
                }
            }
            finally { ComDBClose(h); }
        }

        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (k != null)
                foreach (var name in k.GetValueNames())
                {
                    int n = ParsePort(k.GetValue(name)?.ToString());
                    if (n > 0) used.Add(n);
                }
        }
        catch { /* brak dostępu - zostajemy przy COMDB */ }

        return used;
    }

    static string? FindPortKeyPath(ulong address)
    {
        string hex = address.ToString("X12");
        using var root = Registry.LocalMachine.OpenSubKey(BthEnumPath);
        if (root == null) return null;

        foreach (var devKeyName in root.GetSubKeyNames())
        {
            if (!devKeyName.StartsWith("{00001101-", StringComparison.OrdinalIgnoreCase)) continue;
            using var devKey = root.OpenSubKey(devKeyName);
            if (devKey == null) continue;

            foreach (var inst in devKey.GetSubKeyNames())
            {
                if (inst.IndexOf(hex, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string path = $@"{BthEnumPath}\{devKeyName}\{inst}\Device Parameters";
                using var p = Registry.LocalMachine.OpenSubKey(path);
                if (p?.GetValue("PortName") != null) return path;
            }
        }
        return null;
    }

    /// <summary>Aktualny port COM urządzenia (np. "COM7") albo null, jeśli jeszcze nie utworzony.</summary>
    public static string? GetDevicePort(ulong address)
    {
        try
        {
            string? path = FindPortKeyPath(address);
            if (path == null) return null;
            using var k = Registry.LocalMachine.OpenSubKey(path);
            return k?.GetValue("PortName")?.ToString();
        }
        catch { return null; }
    }

    /// <summary>Przestawia port COM urządzenia. Zwraca "" gdy OK, w przeciwnym razie opis błędu.</summary>
    public static string SetDevicePort(ulong address, int newPort)
    {
        try
        {
            string? path = FindPortKeyPath(address);
            if (path == null) return "Nie znaleziono portu COM tego urządzenia w rejestrze.";

            string? oldName;
            using (var k = Registry.LocalMachine.OpenSubKey(path))
                oldName = k?.GetValue("PortName")?.ToString();
            int oldNum = ParsePort(oldName);
            if (oldNum == newPort) return "";

            int r = ComDBOpen(out IntPtr h);
            if (r != 0) return $"Nie można otworzyć bazy portów COM (ComDBOpen={r}).";
            try
            {
                r = ComDBClaimPort(h, (uint)newPort, 0, out _);
                if (r != 0) return $"Port COM{newPort} jest zajęty (ComDBClaimPort={r}).";

                using (var k = Registry.LocalMachine.OpenSubKey(path, writable: true))
                {
                    if (k == null)
                    {
                        ComDBReleasePort(h, (uint)newPort);
                        return "Brak dostępu do zapisu w rejestrze (uruchom jako administrator).";
                    }
                    k.SetValue("PortName", $"COM{newPort}", RegistryValueKind.String);
                }

                if (oldNum > 0) ComDBReleasePort(h, (uint)oldNum);
            }
            finally { ComDBClose(h); }

            return "";
        }
        catch (Exception ex)
        {
            return $"Błąd zmiany portu: {ex.Message}";
        }
    }

    /// <summary>Wyłącza i włącza usługę SPP, żeby Windows odtworzył port z nową nazwą.</summary>
    public static bool RestartSppService(ulong address)
    {
        var rp = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        IntPtr hFind = BluetoothFindFirstRadio(ref rp, out IntPtr hRadio);
        if (hFind == IntPtr.Zero) return false;
        BluetoothFindRadioClose(hFind);

        var di = new BLUETOOTH_DEVICE_INFO
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(),
            Address = address
        };
        BluetoothGetDeviceInfo(hRadio, ref di);

        var guid = SPP_SERVICE_GUID;
        BluetoothSetServiceState(hRadio, ref di, ref guid, BLUETOOTH_SERVICE_DISABLE);
        System.Threading.Thread.Sleep(1500);
        return BluetoothSetServiceState(hRadio, ref di, ref guid, BLUETOOTH_SERVICE_ENABLE) == 0;
    }

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
