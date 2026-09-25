using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BTparingDevices;

public record RemoteBtDeviceDto(string Name, string Address, string AddressLong);
public record RemotePairResultDto(bool Paired, string ComPorts);
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

function Connect-BtDeviceRemote {
    param([string]$BtNativeSource, [string]$AddressLong, [string]$Pin)
    Add-Type -TypeDefinition $BtNativeSource -Language CSharp -ErrorAction Stop
    $addr = [UInt64]::Parse($AddressLong)
    $ok = [BtRemoteNative]::PairDevice($addr, $Pin)
    if (-not $ok) { $ok = [BtRemoteNative]::PairDevice($addr, '0000') }
    Start-Sleep -Seconds 3
    $ports = [System.IO.Ports.SerialPort]::GetPortNames()
    [PSCustomObject]@{ Paired = $ok; ComPorts = ($ports -join ', ') }
}
""";

    // Szablon skryptu URUCHAMIANEGO LOKALNIE (u operatora), ktory laczy sie
    // przez WinRM z komputerem docelowym - wzorowany na Run-CleanUserJunk-Remote.ps1.
    const string DriverTemplate = """
$ErrorActionPreference = 'Stop'
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
    param($BtNativeSourceText, $WorkerText, $Action, $AddressLong, $Pin)
    . ([ScriptBlock]::Create($WorkerText))
    if ($Action -eq 'Discover') { Get-BtDevicesRemote -BtNativeSource $BtNativeSourceText }
    elseif ($Action -eq 'Pair') { Connect-BtDeviceRemote -BtNativeSource $BtNativeSourceText -AddressLong $AddressLong -Pin $Pin }
    elseif ($Action -eq 'Test') { [PSCustomObject]@{ Ok = $true; ComputerName = $env:COMPUTERNAME } }
}

$result = Invoke-Command -ComputerName '__COMPUTER__' -Credential $cred -ScriptBlock $wrapperSb -ArgumentList $BtNativeSource, $workerText, '__ACTION__', '__ADDR__', '__PIN__' -ErrorAction Stop

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
        string? addressLong = null, string? pin = null)
    {
        string driver = DriverTemplate
            .Replace("__USER__", EscapeSingleQuote(username))
            .Replace("__BTNATIVE__", BtNativeCSharpSource)
            .Replace("__WORKER__", WorkerPsFunctions)
            .Replace("__COMPUTER__", EscapeSingleQuote(computer))
            .Replace("__ACTION__", EscapeSingleQuote(action))
            .Replace("__ADDR__", EscapeSingleQuote(addressLong ?? ""))
            .Replace("__PIN__", EscapeSingleQuote(pin ?? ""));

        byte[] bytes = Encoding.Unicode.GetBytes(driver);
        string encoded = Convert.ToBase64String(bytes);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Haslo przekazywane zmienna srodowiskowa procesu potomnego (nie w wierszu
        // polecen), zeby nie bylo widoczne np. w Menedzerze zadan / historii.
        psi.Environment["BTPAIR_REMOTE_PW"] = password;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Nie udalo sie uruchomic powershell.exe.");
        string stdout = await proc.StandardOutput.ReadToEndAsync();
        string stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        return (proc.ExitCode == 0, stdout.Trim(), stderr.Trim());
    }

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static RemoteBtDeviceDto[] ParseDevices(string json)
        => JsonSerializer.Deserialize<RemoteBtDeviceDto[]>(json, JsonOpts) ?? Array.Empty<RemoteBtDeviceDto>();

    public static RemotePairResultDto? ParsePairResult(string json)
    {
        var arr = JsonSerializer.Deserialize<RemotePairResultDto[]>(json, JsonOpts);
        return arr is { Length: > 0 } ? arr[0] : null;
    }

    public static RemoteTestResultDto? ParseTestResult(string json)
    {
        var arr = JsonSerializer.Deserialize<RemoteTestResultDto[]>(json, JsonOpts);
        return arr is { Length: > 0 } ? arr[0] : null;
    }

    static string EscapeSingleQuote(string s) => s.Replace("'", "''");
}
