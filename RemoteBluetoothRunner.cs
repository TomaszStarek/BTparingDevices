using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BTparingDevices;

public record RemoteBtDeviceDto(string Name, string Address, string AddressLong);
public record RemotePairResultDto(bool Paired, string ComPorts, string? ComPort, string? Message);
public record RemotePortsDto(string? Used);
public record RemoteTestResultDto(bool Ok, string ComputerName);

/// <summary>
/// Uruchamia zdalne skanowanie/parowanie Bluetooth na innym komputerze przez
/// PowerShell Remoting (WinRM) - dokladnie tym samym mechanizmem co
/// RemoteCleanGUI.ps1 / Run-CleanUserJunk-Remote.ps1: tresc "skryptu roboczego"
/// (tu: kod P/Invoke identyczny jak w BluetoothNative.cs) jest wysylana jako
/// tekst przez Invoke-Command -ScriptBlock, wiec NIE trzeba niczego instalowac
/// ani kopiowac na komputer docelowy - wystarczy wlaczony WinRM
/// (Enable-PSRemoting -Force) i konto z uprawnieniami administratora.
/// </summary>
public static class RemoteBluetoothRunner
{
    // Kod C# kompilowany "w locie" (Add-Type) NA KOMPUTERZE ZDALNYM.
    // To ten sam P/Invoke co w BluetoothNative.cs, tylko jako tekst do przeslania.
    const string BtNativeCSharpSource = """
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

public static class BtRemoteNative
{
    [StructLayout(LayoutKind.Sequential)]
    struct BLUETOOTH_FIND_RADIO_PARAMS { public uint dwSize; }

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
    struct SYSTEMTIME { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

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
    static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr phRadio);
    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern bool BluetoothFindRadioClose(IntPtr hFind);
    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS p, ref BLUETOOTH_DEVICE_INFO info);
    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO info);
    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern bool BluetoothFindDeviceClose(IntPtr hFind);
    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern uint BluetoothAuthenticateDevice(IntPtr hwndParent, IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO info, [MarshalAs(UnmanagedType.LPWStr)] string pin, uint pinLen);
    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern uint BluetoothSetServiceState(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO info, ref Guid guid, uint flags);

    delegate bool PFN_AUTHENTICATION_CALLBACK_EX(IntPtr pvParam, ref BLUETOOTH_AUTHENTICATION_CALLBACK_PARAMS p);

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
        [FieldOffset(0)]
        public ulong bthAddressRemote;
        [FieldOffset(8)]
        public uint authMethod;
        [FieldOffset(12)]
        public uint numericValueOrPasskey;
        [FieldOffset(44)]
        public int negativeResponse;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern uint BluetoothRegisterForAuthenticationEx(ref BLUETOOTH_DEVICE_INFO pbtdiIn, out IntPtr phRegHandle, PFN_AUTHENTICATION_CALLBACK_EX pfnCallbackIn, IntPtr pvParam);
    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern uint BluetoothUnregisterAuthentication(IntPtr hRegHandle);
    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern uint BluetoothSendAuthenticationResponseEx(IntPtr hRadioIn, ref BLUETOOTH_AUTHENTICATE_RESPONSE pauthResponse);

    static bool AutoAcceptCallback(IntPtr pvParam, ref BLUETOOTH_AUTHENTICATION_CALLBACK_PARAMS p)
    {
        BLUETOOTH_AUTHENTICATE_RESPONSE resp = new BLUETOOTH_AUTHENTICATE_RESPONSE();
        resp.bthAddressRemote = p.deviceInfo.Address;
        resp.authMethod = p.authenticationMethod;
        resp.numericValueOrPasskey = p.Numeric_Value_Passkey;
        resp.negativeResponse = 0;
        BluetoothSendAuthenticationResponseEx(IntPtr.Zero, ref resp);
        return true;
    }

    static readonly Guid SPP_GUID = new Guid("00001101-0000-1000-8000-00805F9B34FB");
    const uint SERVICE_ENABLE = 0x00000001;

    // ---- Zarzadzanie numerem portu COM (COMDB + rejestr) ----
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

    static int ParsePort(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        if (!s.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) return 0;
        int n;
        if (int.TryParse(s.Substring(3), out n)) return n;
        return 0;
    }

    // Zwraca zajete numery portow jako tekst "1,3,4"
    public static string GetUsedPorts()
    {
        bool[] used = new bool[4097];
        IntPtr h;
        if (ComDBOpen(out h) == 0)
        {
            try
            {
                byte[] buf = new byte[4096];
                uint max;
                if (ComDBGetCurrentPortUsage(h, buf, (uint)buf.Length, 1, out max) == 0)
                {
                    for (int i = 0; i < buf.Length; i++)
                    {
                        if (buf[i] != 0) used[i + 1] = true;
                    }
                }
            }
            finally { ComDBClose(h); }
        }

        try
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM"))
            {
                if (k != null)
                {
                    foreach (string name in k.GetValueNames())
                    {
                        object v = k.GetValue(name);
                        int num = ParsePort(v == null ? null : v.ToString());
                        if (num > 0 && num <= 4096) used[num] = true;
                    }
                }
            }
        }
        catch { }

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 1; i <= 4096; i++)
        {
            if (used[i])
            {
                if (sb.Length > 0) sb.Append(",");
                sb.Append(i);
            }
        }
        return sb.ToString();
    }

    static string FindPortKeyPath(ulong address)
    {
        string hex = address.ToString("X12");
        using (RegistryKey root = Registry.LocalMachine.OpenSubKey(BthEnumPath))
        {
            if (root == null) return null;
            foreach (string devKeyName in root.GetSubKeyNames())
            {
                if (!devKeyName.StartsWith("{00001101-", StringComparison.OrdinalIgnoreCase)) continue;
                using (RegistryKey devKey = root.OpenSubKey(devKeyName))
                {
                    if (devKey == null) continue;
                    foreach (string inst in devKey.GetSubKeyNames())
                    {
                        if (inst.IndexOf(hex, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string path = BthEnumPath + "\\" + devKeyName + "\\" + inst + "\\Device Parameters";
                        using (RegistryKey p = Registry.LocalMachine.OpenSubKey(path))
                        {
                            if (p != null && p.GetValue("PortName") != null) return path;
                        }
                    }
                }
            }
        }
        return null;
    }

    // Aktualny port urzadzenia (np. "COM7") albo pusty tekst
    public static string GetDevicePort(ulong address)
    {
        try
        {
            string path = FindPortKeyPath(address);
            if (path == null) return "";
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
            {
                if (k == null) return "";
                object v = k.GetValue("PortName");
                return v == null ? "" : v.ToString();
            }
        }
        catch { return ""; }
    }

    // Zwraca "" gdy OK, w przeciwnym razie opis bledu
    public static string SetDevicePort(ulong address, int newPort)
    {
        try
        {
            string path = FindPortKeyPath(address);
            if (path == null) return "Nie znaleziono portu COM tego urzadzenia w rejestrze.";

            string oldName = null;
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
            {
                if (k != null)
                {
                    object v = k.GetValue("PortName");
                    if (v != null) oldName = v.ToString();
                }
            }
            int oldNum = ParsePort(oldName);
            if (oldNum == newPort) return "";

            IntPtr h;
            int r = ComDBOpen(out h);
            if (r != 0) return "Nie mozna otworzyc bazy portow COM (ComDBOpen=" + r + ").";
            try
            {
                int forced;
                r = ComDBClaimPort(h, (uint)newPort, 0, out forced);
                if (r != 0) return "Port COM" + newPort + " jest zajety (ComDBClaimPort=" + r + ").";

                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path, true))
                {
                    if (k == null)
                    {
                        ComDBReleasePort(h, (uint)newPort);
                        return "Brak dostepu do zapisu w rejestrze.";
                    }
                    k.SetValue("PortName", "COM" + newPort, RegistryValueKind.String);
                }

                if (oldNum > 0) ComDBReleasePort(h, (uint)oldNum);
            }
            finally { ComDBClose(h); }
            return "";
        }
        catch (Exception ex)
        {
            return "Blad zmiany portu: " + ex.Message;
        }
    }

    // Wylacza i wlacza usluge SPP, zeby Windows odtworzyl port z nowa nazwa
    public static bool RestartSppService(ulong address)
    {
        BLUETOOTH_FIND_RADIO_PARAMS rp = new BLUETOOTH_FIND_RADIO_PARAMS();
        rp.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS));
        IntPtr hRadio;
        IntPtr hFind = BluetoothFindFirstRadio(ref rp, out hRadio);
        if (hFind == IntPtr.Zero) return false;
        BluetoothFindRadioClose(hFind);

        BLUETOOTH_DEVICE_INFO di = new BLUETOOTH_DEVICE_INFO();
        di.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO));
        di.Address = address;
        BluetoothGetDeviceInfo(hRadio, ref di);

        Guid guid = SPP_GUID;
        BluetoothSetServiceState(hRadio, ref di, ref guid, 0);
        System.Threading.Thread.Sleep(1500);
        return BluetoothSetServiceState(hRadio, ref di, ref guid, SERVICE_ENABLE) == 0;
    }

    public static List<string[]> DiscoverDevices()
    {
        var result = new List<string[]>();
        var rp = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        IntPtr hRadio;
        IntPtr hRadioFind = BluetoothFindFirstRadio(ref rp, out hRadio);
        if (hRadioFind == IntPtr.Zero) return result;
        BluetoothFindRadioClose(hRadioFind);

        var sp = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
            fReturnAuthenticated = true,
            fReturnRemembered = true,
            fReturnUnknown = true,
            fReturnConnected = true,
            fIssueInquiry = true,
            cTimeoutMultiplier = 4,
            hRadio = hRadio
        };
        var di = new BLUETOOTH_DEVICE_INFO { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };
        IntPtr hDev = BluetoothFindFirstDevice(ref sp, ref di);
        if (hDev == IntPtr.Zero) return result;

        do
        {
            string addr = string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                (di.Address >> 40) & 0xFF, (di.Address >> 32) & 0xFF, (di.Address >> 24) & 0xFF,
                (di.Address >> 16) & 0xFF, (di.Address >> 8) & 0xFF, di.Address & 0xFF);
            result.Add(new[] { di.szName ?? "Nieznane", addr, di.Address.ToString() });
        } while (BluetoothFindNextDevice(hDev, ref di));

        BluetoothFindDeviceClose(hDev);
        return result;
    }

    public static bool PairDevice(ulong address, string pin)
    {
        var rp = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        IntPtr hRadio;
        IntPtr hRadioFind = BluetoothFindFirstRadio(ref rp, out hRadio);
        if (hRadioFind == IntPtr.Zero) return false;
        BluetoothFindRadioClose(hRadioFind);

        var di = new BLUETOOTH_DEVICE_INFO { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(), Address = address };

        // Zarejestruj auto-akceptacje PRZED parowaniem, zeby nie pokazalo sie
        // okno "Pair Device - Allow/Cancel" na ekranie komputera docelowego.
        PFN_AUTHENTICATION_CALLBACK_EX callback = new PFN_AUTHENTICATION_CALLBACK_EX(AutoAcceptCallback);
        IntPtr hAuthReg;
        uint regResult = BluetoothRegisterForAuthenticationEx(ref di, out hAuthReg, callback, IntPtr.Zero);

        uint result = BluetoothAuthenticateDevice(IntPtr.Zero, hRadio, ref di, pin, (uint)pin.Length);

        if (regResult == 0) { BluetoothUnregisterAuthentication(hAuthReg); }
        GC.KeepAlive(callback);

        if (result == 0)
        {
            var guid = SPP_GUID;
            BluetoothSetServiceState(hRadio, ref di, ref guid, SERVICE_ENABLE);
            return true;
        }
        return false;
    }
}
""";

    // Funkcje PowerShell wykonywane WEWNATRZ zdalnej sesji (Invoke-Command).
    const string WorkerPsFunctions = """
function Get-BtDevicesRemote {
    param([string]$BtNativeSource)
    Add-Type -TypeDefinition $BtNativeSource -Language CSharp -ErrorAction Stop
    $rows = [BtRemoteNative]::DiscoverDevices()
    foreach ($r in $rows) {
        [PSCustomObject]@{ Name = $r[0]; Address = $r[1]; AddressLong = $r[2] }
    }
}

function Get-BtUsedPortsRemote {
    param([string]$BtNativeSource)
    Add-Type -TypeDefinition $BtNativeSource -Language CSharp -ErrorAction Stop
    [PSCustomObject]@{ Used = [BtRemoteNative]::GetUsedPorts() }
}

function Connect-BtDeviceRemote {
    param([string]$BtNativeSource, [string]$AddressLong, [string]$Pin, [string]$DesiredPort)
    Add-Type -TypeDefinition $BtNativeSource -Language CSharp -ErrorAction Stop
    $addr = [UInt64]::Parse($AddressLong)
    $ok = [BtRemoteNative]::PairDevice($addr, $Pin)
    if (-not $ok) { $ok = [BtRemoteNative]::PairDevice($addr, '0000') }

    # poczekaj az Windows utworzy port COM dla tego urzadzenia (do ~10 s)
    $port = ''
    for ($i = 0; $i -lt 10 -and -not $port; $i++) {
        Start-Sleep -Seconds 1
        $port = [BtRemoteNative]::GetDevicePort($addr)
    }

    $msg = ''
    if ($DesiredPort -and $port -and ($port -ne ('COM' + $DesiredPort))) {
        $err = [BtRemoteNative]::SetDevicePort($addr, [int]$DesiredPort)
        if ($err) {
            $msg = $err
        } else {
            [void][BtRemoteNative]::RestartSppService($addr)
            Start-Sleep -Seconds 2
            $newPort = [BtRemoteNative]::GetDevicePort($addr)
            if ($newPort) { $port = $newPort }
        }
    }
    if ($DesiredPort -and -not $port) { $msg = 'Nie ustalono portu COM urzadzenia (brak wpisu w rejestrze).' }

    $ports = [System.IO.Ports.SerialPort]::GetPortNames()
    [PSCustomObject]@{ Paired = $ok; ComPorts = ($ports -join ', '); ComPort = $port; Message = $msg }
}
""";

    // Szablon skryptu URUCHAMIANEGO LOKALNIE (u operatora), ktory laczy sie
    // przez WinRM z komputerem docelowym - wzorowany na Run-CleanUserJunk-Remote.ps1.
    const string DriverTemplate = """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$plainPw = $env:BTPAIR_REMOTE_PW
if (-not $plainPw) { throw 'Brak hasla (zmienna srodowiskowa BTPAIR_REMOTE_PW).' }
$securePw = ConvertTo-SecureString -String $plainPw -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential('__USER__', $securePw)

$BtNativeSource = @'
__BTNATIVE__
'@

$workerText = @'
__WORKER__
'@

$wrapperSb = {
    param($BtNativeSourceText, $WorkerText, $Action, $AddressLong, $Pin, $DesiredPort)
    . ([ScriptBlock]::Create($WorkerText))
    if ($Action -eq 'Discover') { Get-BtDevicesRemote -BtNativeSource $BtNativeSourceText }
    elseif ($Action -eq 'Pair') { Connect-BtDeviceRemote -BtNativeSource $BtNativeSourceText -AddressLong $AddressLong -Pin $Pin -DesiredPort $DesiredPort }
    elseif ($Action -eq 'Ports') { Get-BtUsedPortsRemote -BtNativeSource $BtNativeSourceText }
    elseif ($Action -eq 'Test') { [PSCustomObject]@{ Ok = $true; ComputerName = $env:COMPUTERNAME } }
}

$result = Invoke-Command -ComputerName '__COMPUTER__' -Credential $cred -ScriptBlock $wrapperSb -ArgumentList $BtNativeSource, $workerText, '__ACTION__', '__ADDR__', '__PIN__', '__PORT__' -ErrorAction Stop

$arr = @($result)
if ($arr.Count -eq 0) {
    Write-Output '[]'
} else {
    $json = $arr | ConvertTo-Json -Depth 6 -Compress
    if ($arr.Count -eq 1) { $json = "[$json]" }
    Write-Output $json
}
""";

    public static async Task<(bool Success, string StdOut, string StdErr)> InvokeAsync(
        string computer, string username, string password, string action,
        string? addressLong = null, string? pin = null, int? desiredPort = null,
        int timeoutSeconds = 300)
    {
        string driver = DriverTemplate
            .Replace("__USER__", EscapeSingleQuote(username))
            .Replace("__BTNATIVE__", BtNativeCSharpSource)
            .Replace("__WORKER__", WorkerPsFunctions)
            .Replace("__COMPUTER__", EscapeSingleQuote(computer))
            .Replace("__ACTION__", EscapeSingleQuote(action))
            .Replace("__ADDR__", EscapeSingleQuote(addressLong ?? ""))
            .Replace("__PIN__", EscapeSingleQuote(pin ?? ""))
            .Replace("__PORT__", desiredPort?.ToString() ?? "");

        // POPRAWKA: wczesniej skrypt szedl przez stdin ("-Command -"). PowerShell czyta
        // wtedy skrypt linia po linii jak w konsoli interaktywnej, a tutaj-lancuchy @'...'@
        // z kodem C# (z pustymi liniami) potrafia sie wtedy "rozsypac" - skrypt nic nie
        // wypisuje, kod wyjscia bywa 0, a program dostaje PUSTY stdout i zglasza
        // "The input does not contain any JSON tokens".
        //
        // Teraz: skrypt zapisujemy do pliku tymczasowego (UTF-8) i uruchamiamy jako
        // ScriptBlock::Create(ReadAllText(...)). Dzieki temu:
        //  - brak limitu dlugosci wiersza polecen (nic dlugiego nie idzie w argumentach),
        //  - brak problemow z parsowaniem stdin,
        //  - ExecutionPolicy nie ma znaczenia (nie uruchamiamy pliku .ps1, tylko czytamy tekst).
        string scriptPath = Path.Combine(Path.GetTempPath(), "btpair_" + Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(scriptPath, driver, new UTF8Encoding(false));

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-OutputFormat");
            psi.ArgumentList.Add("Text");   // bez opakowania bledow w CLIXML
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("& ([scriptblock]::Create([System.IO.File]::ReadAllText('"
                                 + EscapeSingleQuote(scriptPath) + "', [System.Text.Encoding]::UTF8)))");

            // Haslo przekazywane zmienna srodowiskowa procesu potomnego (nie w wierszu
            // polecen), zeby nie bylo widoczne np. w Menedzerze zadan / historii.
            psi.Environment["BTPAIR_REMOTE_PW"] = password;

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Nie udalo sie uruchomic powershell.exe.");

            // Stdout i stderr czytane rownolegle - sekwencyjne ReadToEnd moze sie zakleszczyc.
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return (false, "", $"Przekroczono limit czasu ({timeoutSeconds} s) - komputer '{computer}' nie odpowiada (WinRM / zapora / TrustedHosts?).");
            }

            string rawOut = await outTask;
            string stderr = CleanStdErr(await errTask);
            string json = ExtractJson(rawOut);

            if (json.Length == 0)
            {
                // Nigdy nie zwracamy pustego "sukcesu" - inaczej parser JSON rzuca
                // niezrozumialy blad "does not contain any JSON tokens".
                var sb = new StringBuilder();
                sb.Append($"Zdalne polecenie nie zwrocilo danych (kod wyjscia {proc.ExitCode}).");
                if (!string.IsNullOrWhiteSpace(stderr)) sb.Append(' ').Append(stderr);
                else if (!string.IsNullOrWhiteSpace(rawOut))
                    sb.Append(" Nieoczekiwany wynik: ").Append(rawOut.Trim().Length > 500 ? rawOut.Trim()[..500] : rawOut.Trim());
                else
                    sb.Append(" Sprawdz: WinRM na komputerze docelowym (Enable-PSRemoting -Force), " +
                              "TrustedHosts po stronie operatora, nazwe komputera, login i haslo.");
                return (false, "", sb.ToString());
            }

            return (proc.ExitCode == 0, json, stderr);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    /// <summary>Wyciaga ostatnia linie wygladajaca na JSON-a (tablica) - odporne na ewentualne
    /// dodatkowe linie (ostrzezenia, banery) w stdout.</summary>
    static string ExtractJson(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return "";
        var lines = stdout.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("[") && t.EndsWith("]")) return t;
        }
        return "";
    }

    /// <summary>Jesli PowerShell mimo wszystko opakowal bledy w CLIXML - wyciaga z niego czytelny tekst.</summary>
    static string CleanStdErr(string stderr)
    {
        stderr = (stderr ?? "").Trim();
        if (!stderr.StartsWith("#< CLIXML")) return stderr;
        var parts = Regex.Matches(stderr, "<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline)
                         .Select(m => m.Groups[1].Value
                             .Replace("_x000D__x000A_", " ")
                             .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&")
                             .Trim())
                         .Where(x => x.Length > 0);
        string text = string.Join(" ", parts);
        return text.Length > 0 ? text : stderr;
    }

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    static string RequireJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Komputer zdalny zwrocil pusta odpowiedz (brak danych JSON).");
        return json;
    }

    public static RemoteBtDeviceDto[] ParseDevices(string json)
        => JsonSerializer.Deserialize<RemoteBtDeviceDto[]>(RequireJson(json), JsonOpts) ?? Array.Empty<RemoteBtDeviceDto>();

    public static RemotePairResultDto? ParsePairResult(string json)
    {
        var arr = JsonSerializer.Deserialize<RemotePairResultDto[]>(RequireJson(json), JsonOpts);
        return arr is { Length: > 0 } ? arr[0] : null;
    }

    /// <summary>Zajęte porty COM na komputerze zdalnym (zwrócone jako "1,3,4").</summary>
    public static int[] ParseUsedPorts(string json)
    {
        var arr = JsonSerializer.Deserialize<RemotePortsDto[]>(RequireJson(json), JsonOpts);
        string used = arr is { Length: > 0 } ? arr[0].Used ?? "" : "";
        return used.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Select(x => int.TryParse(x, out int n) ? n : 0)
                   .Where(n => n > 0)
                   .ToArray();
    }

    public static RemoteTestResultDto? ParseTestResult(string json)
    {
        var arr = JsonSerializer.Deserialize<RemoteTestResultDto[]>(RequireJson(json), JsonOpts);
        return arr is { Length: > 0 } ? arr[0] : null;
    }

    static string EscapeSingleQuote(string s) => s.Replace("'", "''");
}
