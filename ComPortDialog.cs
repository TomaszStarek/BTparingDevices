using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BTparingDevices;

/// <summary>
/// Okno "Wybierz port COM" pokazywane przed parowaniem. Lista zawiera TYLKO wolne
/// porty (zajęte są wykluczone) oraz opcję "Automatycznie" (port przydzieli Windows).
/// </summary>
public class ComPortDialog : Form
{
    const int MaxPortShown = 256;

    readonly ComboBox cboPort = new();
    readonly List<int> freePorts = new();

    /// <summary>Wybrany numer portu COM albo null = automatycznie (Windows przydzieli).</summary>
    public int? SelectedPort { get; private set; }

    public ComPortDialog(string deviceName, string targetInfo, IEnumerable<int> usedPorts)
    {
        var used = new HashSet<int>(usedPorts);

        Text = "Wybór portu COM";
        Width = 480;
        Height = 330;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9.5F);

        var lblInfo = new Label
        {
            Text = $"Urządzenie: {deviceName}\n{targetInfo}",
            Location = new Point(16, 14),
            AutoSize = true
        };
        Controls.Add(lblInfo);

        var lblPort = new Label { Text = "Port COM dla tego urządzenia:", Location = new Point(16, 68), AutoSize = true };
        Controls.Add(lblPort);

        cboPort.DropDownStyle = ComboBoxStyle.DropDownList;
        cboPort.Location = new Point(16, 92);
        cboPort.Size = new Size(430, 26);
        cboPort.Items.Add("Automatycznie (przydzieli Windows)");
        for (int n = 1; n <= MaxPortShown; n++)
        {
            if (used.Contains(n)) continue;
            freePorts.Add(n);
            cboPort.Items.Add($"COM{n}");
        }
        cboPort.SelectedIndex = 0;
        Controls.Add(cboPort);

        string usedText = used.Count == 0
            ? "(brak)"
            : string.Join(", ", used.Where(n => n <= MaxPortShown).OrderBy(n => n).Select(n => $"COM{n}"));
        var lblUsed = new Label
        {
            Text = $"Zajęte porty (niedostępne): {usedText}",
            Location = new Point(16, 130),
            MaximumSize = new Size(430, 90),
            AutoSize = true,
            ForeColor = Color.DimGray
        };
        Controls.Add(lblUsed);

        var btnOk = new Button
        {
            Text = "Paruj",
            Location = new Point(250, 245),
            Size = new Size(95, 32),
            DialogResult = DialogResult.OK
        };
        btnOk.Click += (s, e) =>
        {
            int idx = cboPort.SelectedIndex;
            SelectedPort = idx <= 0 ? null : freePorts[idx - 1];
        };
        Controls.Add(btnOk);

        var btnCancel = new Button
        {
            Text = "Anuluj",
            Location = new Point(351, 245),
            Size = new Size(95, 32),
            DialogResult = DialogResult.Cancel
        };
        Controls.Add(btnCancel);

        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }
}
