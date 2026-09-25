using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Devices.Radios;

namespace BTparingDevices;

public class MainForm : Form
{
    // Prefiksy nazw kluczy Tohnichi (na podstawie zrzutu z Menedżera urządzeń: CEM3-BT_... i CES_...)
    static readonly string[] TohnichiPrefixes = { "CEM3-BT", "CES_" };

    List<BluetoothNative.BluetoothDevice> allDevices = new();
    List<BluetoothNative.BluetoothDevice> displayedDevices = new();
    SerialPort? serial;

    readonly Label lblStatus = new();
    readonly Button btnScan = new();
    readonly Button btnShowAll = new();
    readonly Button btnShowTohnichi = new();
    readonly Button btnConnect = new();
    readonly ListBox lstDevices = new();
    readonly TextBox txtLog = new();
    readonly TextBox txtMessage = new();
    readonly Button btnSend = new();

    public MainForm()
    {
        BuildUi();
        Load += async (s, e) => await InitBluetoothAsync();
    }

    void BuildUi()
    {
        Text = "Bluetooth SPP Auto-Connect";
        Width = 760;
        Height = 660;
        MinimumSize = new Size(600, 500);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        lblStatus.Text = "Sprawdzanie Bluetooth...";
        lblStatus.Location = new Point(12, 12);
        lblStatus.AutoSize = true;
        Controls.Add(lblStatus);

        btnScan.Text = "Skanuj";
        btnScan.Location = new Point(12, 38);
        btnScan.Size = new Size(110, 32);
        btnScan.Click += async (s, e) => await ScanAsync();
        Controls.Add(btnScan);

        btnShowAll.Text = "Pokaż wszystkie";
        btnShowAll.Location = new Point(130, 38);
        btnShowAll.Size = new Size(130, 32);
        btnShowAll.Click += (s, e) => ShowDevices(allDevices);
        Controls.Add(btnShowAll);

        btnShowTohnichi.Text = "Pokaż Tohnichi";
        btnShowTohnichi.Location = new Point(268, 38);
        btnShowTohnichi.Size = new Size(130, 32);
        btnShowTohnichi.Click += (s, e) =>
        {
            var filtered = allDevices
                .Where(d => TohnichiPrefixes.Any(p => d.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            ShowDevices(filtered);
        };
        Controls.Add(btnShowTohnichi);

        lstDevices.Location = new Point(12, 80);
        lstDevices.Size = new Size(720, 220);
        lstDevices.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        lstDevices.IntegralHeight = false;
        Controls.Add(lstDevices);

        btnConnect.Text = "Paruj i połącz z wybranym";
        btnConnect.Location = new Point(12, 310);
        btnConnect.Size = new Size(220, 32);
        btnConnect.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        btnConnect.Click += async (s, e) => await ConnectSelectedAsync();
        Controls.Add(btnConnect);

        var lblLog = new Label { Text = "Dziennik:", Location = new Point(12, 352), AutoSize = true };
        Controls.Add(lblLog);

        txtLog.Multiline = true;
        txtLog.ReadOnly = true;
        txtLog.ScrollBars = ScrollBars.Vertical;
        txtLog.Location = new Point(12, 376);
        txtLog.Size = new Size(720, 180);
        txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(txtLog);

        var lblMsg = new Label { Text = "Wiadomość do urządzenia:", AutoSize = true };
        lblMsg.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        Controls.Add(lblMsg);

        txtMessage.Location = new Point(12, 585);
        txtMessage.Size = new Size(590, 27);
        txtMessage.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        txtMessage.Enabled = false;
        txtMessage.KeyDown += async (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await SendMessageAsync();
            }
        };
        Controls.Add(txtMessage);
        lblMsg.Location = new Point(12, 562);

        btnSend.Text = "Wyślij";
        btnSend.Location = new Point(612, 583);
        btnSend.Size = new Size(120, 30);
        btnSend.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        btnSend.Enabled = false;
        btnSend.Click += async (s, e) => await SendMessageAsync();
        Controls.Add(btnSend);
    }

    void Log(string text)
    {
        if (txtLog.InvokeRequired)
        {
            txtLog.Invoke(() => Log(text));
            return;
        }
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
    }

    async Task InitBluetoothAsync()
    {
        bool ok = await EnsureBluetoothOnAsync();
        lblStatus.Text = ok ? "Bluetooth: włączony" : "Bluetooth: NIEDOSTĘPNY";
        lblStatus.ForeColor = ok ? Color.DarkGreen : Color.DarkRed;
    }

    async Task<bool> EnsureBluetoothOnAsync()
    {
        var radios = await Radio.GetRadiosAsync();
        var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);

        if (bt == null)
        {
            Log("Nie znaleziono adaptera Bluetooth w tym komputerze.");
            return false;
        }

        if (bt.State == RadioState.On)
        {
            Log("Bluetooth jest już włączony.");
            return true;
        }

        Log($"Bluetooth jest wyłączony (stan: {bt.State}). Próbuję włączyć...");
        var result = await bt.SetStateAsync(RadioState.On);

        if (result == RadioAccessStatus.Allowed)
        {
            Log("Bluetooth włączony.");
            return true;
        }

        Log($"Nie udało się włączyć Bluetooth automatycznie (wynik: {result}). Włącz go ręcznie w Ustawieniach.");
        return false;
    }

    async Task ScanAsync()
    {
        SetButtonsEnabled(false);
        Log("Szukam urządzeń Bluetooth...");
        try
        {
            allDevices = await Task.Run(() => BluetoothNative.DiscoverDevices());
            Log($"Znaleziono {allDevices.Count} urządzeń.");
            ShowDevices(allDevices);
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    void ShowDevices(List<BluetoothNative.BluetoothDevice> list)
    {
        displayedDevices = list;
        lstDevices.Items.Clear();
        foreach (var d in list)
            lstDevices.Items.Add($"{d.Name}   ({d.Address})");

        if (list.Count == 0)
            Log("Brak urządzeń do wyświetlenia. Kliknij 'Skanuj'.");
    }

    async Task ConnectSelectedAsync()
    {
        int idx = lstDevices.SelectedIndex;
        if (idx < 0 || idx >= displayedDevices.Count)
        {
            Log("Najpierw zaznacz urządzenie na liście.");
            return;
        }

        var target = displayedDevices[idx];
        SetButtonsEnabled(false);

        try
        {
            Log($"Paruję z: {target.Name}...");
            bool paired = await Task.Run(() => BluetoothNative.PairDevice(target.AddressLong, "1234"));
            if (!paired)
                paired = await Task.Run(() => BluetoothNative.PairDevice(target.AddressLong, "0000"));

            Log(paired ? "Sparowano!" : "Parowanie bez PIN (SSP)...");

            Log("Czekam na port COM...");
            await Task.Delay(3000);

            string[] ports = SerialPort.GetPortNames();
            Log($"Dostępne porty: {string.Join(", ", ports)}");

            if (ports.Length == 0)
            {
                Log("Brak portów COM. Sprawdź Menedżer urządzeń.");
                return;
            }

            string comPort = ports[^1];
            Log($"Używam portu: {comPort}");

            try
            {
                serial?.Dispose();
                serial = new SerialPort(comPort, 9600) { ReadTimeout = 3000, WriteTimeout = 3000 };
                serial.Open();
                Log($"✅ Połączono przez {comPort}!");
                txtMessage.Enabled = true;
                btnSend.Enabled = true;
            }
            catch (Exception ex)
            {
                Log($"Błąd portu: {ex.Message}");
            }
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    async Task SendMessageAsync()
    {
        if (serial == null || !serial.IsOpen)
        {
            Log("Brak aktywnego połączenia. Najpierw sparuj i połącz urządzenie.");
            return;
        }

        string msg = txtMessage.Text;
        txtMessage.Clear();
        btnSend.Enabled = false;

        try
        {
            await Task.Run(() => serial.WriteLine(msg));
            Log($"Wysłano: {msg}");

            try
            {
                string response = await Task.Run(() => serial.ReadLine());
                Log($"Odpowiedź: {response}");
            }
            catch (TimeoutException)
            {
                Log("(brak odpowiedzi)");
            }
        }
        catch (Exception ex)
        {
            Log($"Błąd wysyłania: {ex.Message}");
        }
        finally
        {
            btnSend.Enabled = true;
        }
    }

    void SetButtonsEnabled(bool enabled)
    {
        btnScan.Enabled = enabled;
        btnShowAll.Enabled = enabled;
        btnShowTohnichi.Enabled = enabled;
        btnConnect.Enabled = enabled;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try { serial?.Dispose(); } catch { /* ignoruj błędy przy zamykaniu */ }
        base.OnFormClosing(e);
    }
}
