using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BTparingDevices;

public class RemoteForm : Form
{
    static readonly string[] TohnichiPrefixes = { "CEM3-BT", "CES_" };

    List<RemoteBtDeviceDto> allDevices = new();
    List<RemoteBtDeviceDto> displayedDevices = new();

    readonly ComboBox cboComputer = new();
    readonly Button btnLoadConfig = new();
    readonly TextBox txtUser = new();
    readonly TextBox txtPassword = new();
    readonly Button btnTest = new();
    readonly Button btnScan = new();
    readonly Button btnShowAll = new();
    readonly Button btnShowTohnichi = new();
    readonly Button btnConnect = new();
    readonly ListBox lstDevices = new();
    readonly TextBox txtLog = new();

    public RemoteForm()
    {
        BuildUi();
        TryAutoLoadConfig();
    }

    void BuildUi()
    {
        Text = "Bluetooth SPP Auto-Connect - Wersja zdalna";
        Width = 760;
        Height = 640;
        MinimumSize = new Size(640, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        var grpConn = new GroupBox
        {
            Text = "Połączenie",
            Location = new Point(12, 10),
            Size = new Size(720, 130),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Controls.Add(grpConn);

        var lblComputer = new Label { Text = "Komputer:", Location = new Point(10, 25), AutoSize = true };
        grpConn.Controls.Add(lblComputer);

        cboComputer.Location = new Point(100, 22);
        cboComputer.Size = new Size(300, 24);
        cboComputer.DropDownStyle = ComboBoxStyle.DropDown;
        grpConn.Controls.Add(cboComputer);

        btnLoadConfig.Text = "Wczytaj z config.txt";
        btnLoadConfig.Location = new Point(410, 20);
        btnLoadConfig.Size = new Size(150, 28);
        btnLoadConfig.Click += (s, e) => TryAutoLoadConfig(showMessageIfMissing: true);
        grpConn.Controls.Add(btnLoadConfig);

        var lblUser = new Label { Text = "Login (DOMENA\\użytkownik):", Location = new Point(10, 58), AutoSize = true };
        grpConn.Controls.Add(lblUser);
        txtUser.Location = new Point(200, 55);
        txtUser.Size = new Size(200, 24);
        grpConn.Controls.Add(txtUser);

        var lblPass = new Label { Text = "Hasło:", Location = new Point(410, 58), AutoSize = true };
        grpConn.Controls.Add(lblPass);
        txtPassword.Location = new Point(460, 55);
        txtPassword.Size = new Size(200, 24);
        txtPassword.UseSystemPasswordChar = true;
        grpConn.Controls.Add(txtPassword);

        btnTest.Text = "Testuj połączenie";
        btnTest.Location = new Point(10, 90);
        btnTest.Size = new Size(150, 30);
        btnTest.Click += async (s, e) => await TestConnectionAsync();
        grpConn.Controls.Add(btnTest);

        var lblHint = new Label
        {
            Text = "Wymaga: WinRM włączony na komputerze docelowym, konto z uprawnieniami administratora.",
            Location = new Point(170, 96),
            AutoSize = true,
            ForeColor = Color.DimGray
        };
        grpConn.Controls.Add(lblHint);

        btnScan.Text = "Skanuj zdalnie";
        btnScan.Location = new Point(12, 150);
        btnScan.Size = new Size(120, 32);
        btnScan.Click += async (s, e) => await ScanAsync();
        Controls.Add(btnScan);

        btnShowAll.Text = "Pokaż wszystkie";
        btnShowAll.Location = new Point(140, 150);
        btnShowAll.Size = new Size(130, 32);
        btnShowAll.Click += (s, e) => ShowDevices(allDevices);
        Controls.Add(btnShowAll);

        btnShowTohnichi.Text = "Pokaż Tohnichi";
        btnShowTohnichi.Location = new Point(278, 150);
        btnShowTohnichi.Size = new Size(130, 32);
        btnShowTohnichi.Click += (s, e) =>
        {
            var filtered = allDevices
                .Where(d => TohnichiPrefixes.Any(p => d.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            ShowDevices(filtered);
        };
        Controls.Add(btnShowTohnichi);

        lstDevices.Location = new Point(12, 190);
        lstDevices.Size = new Size(720, 220);
        lstDevices.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        lstDevices.IntegralHeight = false;
        Controls.Add(lstDevices);

        btnConnect.Text = "Paruj i połącz z wybranym (na komputerze zdalnym)";
        btnConnect.Location = new Point(12, 420);
        btnConnect.Size = new Size(320, 32);
        btnConnect.Click += async (s, e) => await ConnectSelectedAsync();
        Controls.Add(btnConnect);

        var lblLog = new Label { Text = "Dziennik:", Location = new Point(12, 460), AutoSize = true };
        Controls.Add(lblLog);

        txtLog.Multiline = true;
        txtLog.ReadOnly = true;
        txtLog.ScrollBars = ScrollBars.Vertical;
        txtLog.Location = new Point(12, 484);
        txtLog.Size = new Size(720, 100);
        txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(txtLog);
    }

    void TryAutoLoadConfig(bool showMessageIfMissing = false)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "config.txt");
        if (!File.Exists(path))
        {
            if (showMessageIfMissing)
                MessageBox.Show($"Nie znaleziono pliku config.txt w folderze aplikacji:\n{AppContext.BaseDirectory}",
                    "Brak pliku", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var names = File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#"))
            .ToList();

        cboComputer.Items.Clear();
        foreach (var n in names)
            cboComputer.Items.Add(n);

        if (names.Count > 0)
            Log($"Wczytano {names.Count} komputerów z config.txt.");
    }

    void Log(string text)
    {
        if (txtLog.InvokeRequired) { txtLog.Invoke(() => Log(text)); return; }
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
    }

    bool TryGetConnectionInfo(out string computer, out string user, out string pass)
    {
        computer = cboComputer.Text.Trim();
        user = txtUser.Text.Trim();
        pass = txtPassword.Text;

        if (string.IsNullOrWhiteSpace(computer))
        {
            MessageBox.Show("Podaj nazwę komputera docelowego.", "Brak danych", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass))
        {
            MessageBox.Show("Podaj login i hasło konta z uprawnieniami administratora na komputerze docelowym.",
                "Brak danych logowania", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    async Task TestConnectionAsync()
    {
        if (!TryGetConnectionInfo(out var computer, out var user, out var pass)) return;

        SetButtonsEnabled(false);
        Log($"Testuję połączenie z {computer}...");
        try
        {
            var (ok, stdout, stderr) = await RemoteBluetoothRunner.InvokeAsync(computer, user, pass, "Test");
            if (ok)
            {
                var result = RemoteBluetoothRunner.ParseTestResult(stdout);
                Log(result != null
                    ? $"Połączono OK. Zdalny komputer zgłasza nazwę: {result.ComputerName}"
                    : "Połączono, ale odpowiedź była pusta.");
            }
            else
            {
                Log($"Błąd połączenia: {(string.IsNullOrWhiteSpace(stderr) ? "nieznany błąd" : stderr)}");
            }
        }
        catch (Exception ex)
        {
            Log($"Błąd: {ex.Message}");
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    async Task ScanAsync()
    {
        if (!TryGetConnectionInfo(out var computer, out var user, out var pass)) return;

        SetButtonsEnabled(false);
        Log($"Łączę się z {computer} i szukam urządzeń Bluetooth w pobliżu...");
        try
        {
            var (ok, stdout, stderr) = await RemoteBluetoothRunner.InvokeAsync(computer, user, pass, "Discover");
            if (!ok)
            {
                Log($"Błąd skanowania: {(string.IsNullOrWhiteSpace(stderr) ? "nieznany błąd" : stderr)}");
                return;
            }

            allDevices = RemoteBluetoothRunner.ParseDevices(stdout).ToList();
            Log($"Znaleziono {allDevices.Count} urządzeń na komputerze {computer}.");
            ShowDevices(allDevices);
        }
        catch (Exception ex)
        {
            Log($"Błąd: {ex.Message}");
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    void ShowDevices(List<RemoteBtDeviceDto> list)
    {
        displayedDevices = list;
        lstDevices.Items.Clear();
        foreach (var d in list)
            lstDevices.Items.Add($"{d.Name}   ({d.Address})");

        if (list.Count == 0)
            Log("Brak urządzeń do wyświetlenia. Kliknij 'Skanuj zdalnie'.");
    }

    async Task ConnectSelectedAsync()
    {
        int idx = lstDevices.SelectedIndex;
        if (idx < 0 || idx >= displayedDevices.Count)
        {
            Log("Najpierw zaznacz urządzenie na liście.");
            return;
        }
        if (!TryGetConnectionInfo(out var computer, out var user, out var pass)) return;

        var target = displayedDevices[idx];

        var confirm = MessageBox.Show(
            $"Sparować urządzenie:\n{target.Name} ({target.Address})\n\nna komputerze:\n{computer}\n\nKontynuować?",
            "Potwierdzenie parowania", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        SetButtonsEnabled(false);
        try
        {
            Log($"Paruję z: {target.Name} na komputerze {computer}...");
            var (ok, stdout, stderr) = await RemoteBluetoothRunner.InvokeAsync(
                computer, user, pass, "Pair", target.AddressLong, "1234");

            if (!ok)
            {
                Log($"Błąd parowania: {(string.IsNullOrWhiteSpace(stderr) ? "nieznany błąd" : stderr)}");
                return;
            }

            var result = RemoteBluetoothRunner.ParsePairResult(stdout);
            if (result == null)
            {
                Log("Brak odpowiedzi z komputera zdalnego.");
                return;
            }

            Log(result.Paired ? "✅ Sparowano!" : "Parowanie bez PIN (SSP) lub niepowodzenie - sprawdź urządzenie.");
            Log($"Porty COM na komputerze zdalnym: {(string.IsNullOrWhiteSpace(result.ComPorts) ? "(brak)" : result.ComPorts)}");
        }
        catch (Exception ex)
        {
            Log($"Błąd: {ex.Message}");
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    void SetButtonsEnabled(bool enabled)
    {
        btnTest.Enabled = enabled;
        btnScan.Enabled = enabled;
        btnShowAll.Enabled = enabled;
        btnShowTohnichi.Enabled = enabled;
        btnConnect.Enabled = enabled;
    }
}
