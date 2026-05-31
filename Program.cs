using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== Bluetooth SPP Auto-Connect ===\n");

        // 1. Znajdź urządzenie
        Console.WriteLine("Szukam urządzeń Bluetooth...");
        var devices = BluetoothNative.DiscoverDevices();

        if (devices.Count == 0)
        {
            Console.WriteLine("Nie znaleziono żadnych urządzeń. Upewnij się że BT jest włączony.");
            Console.ReadKey();
            return;
        }

        Console.WriteLine($"Znaleziono {devices.Count} urządzeń:");
        for (int i = 0; i < devices.Count; i++)
            Console.WriteLine($"  [{i}] {devices[i].Name} ({devices[i].Address})");

        // 2. Wybierz urządzenie (możesz zmienić na filtr po nazwie)
        Console.Write("\nWybierz numer urządzenia: ");
        int idx = int.Parse(Console.ReadLine() ?? "0");
        var target = devices[idx];

        // 3. Sparuj
        Console.WriteLine($"\nParuję z: {target.Name}...");
        bool paired = BluetoothNative.PairDevice(target.AddressLong, "1234"); // zmień PIN jeśli inne
        if (!paired)
        {
            // Wiele urządzeń SPP nie wymaga PIN-u — próbuj bez
            paired = BluetoothNative.PairDevice(target.AddressLong, "0000");
        }
        Console.WriteLine(paired ? "Sparowano!" : "Parowanie bez PIN (SSP)...");

        // 4. Poczekaj chwilę — Windows tworzy port COM
        Console.WriteLine("Czekam na port COM...");
        await Task.Delay(3000);

        // 5. Znajdź nowy port COM
        string[] ports = SerialPort.GetPortNames();
        Console.WriteLine($"Dostępne porty: {string.Join(", ", ports)}");

        if (ports.Length == 0)
        {
            Console.WriteLine("Brak portów COM. Sprawdź Menedżer urządzeń.");
            Console.ReadKey();
            return;
        }

        // Weź ostatni dodany port (zwykle najnowszy = właśnie sparowany)
        string comPort = ports[^1];
        Console.WriteLine($"Używam portu: {comPort}");

        // 6. Połącz przez SerialPort
        using var serial = new SerialPort(comPort, 9600)
        {
            ReadTimeout = 3000,
            WriteTimeout = 3000
        };

        try
        {
            serial.Open();
            Console.WriteLine($"✅ Połączono przez {comPort}!\n");

            // Pętla komunikacji
            Console.WriteLine("Wpisz wiadomość (Enter = wyślij, 'q' = koniec):");
            while (true)
            {
                string? input = Console.ReadLine();
                if (input == "q") break;

                serial.WriteLine(input ?? "");

                try
                {
                    string response = serial.ReadLine();
                    Console.WriteLine($"Odpowiedź: {response}");
                }
                catch (TimeoutException)
                {
                    Console.WriteLine("(brak odpowiedzi)");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Błąd portu: {ex.Message}");
        }

        Console.ReadKey();
    }
}

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
            Console.WriteLine("Brak adaptera Bluetooth!");
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

        // Paruj z PIN-em
        uint result = BluetoothAuthenticateDevice(
            IntPtr.Zero,
            hRadio,
            ref deviceInfo,
            pin,
            (uint)pin.Length);

        if (result == 0) // ERROR_SUCCESS
        {
            // Włącz usługę SPP
            var sppGuid = SPP_SERVICE_GUID;
            BluetoothSetServiceState(hRadio, ref deviceInfo, ref sppGuid, BLUETOOTH_SERVICE_ENABLE);
            return true;
        }

        Console.WriteLine($"BluetoothAuthenticateDevice error: {result}");
        return false;
    }
}