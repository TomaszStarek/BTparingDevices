using System;
using System.Drawing;
using System.Windows.Forms;

namespace BTparingDevices;

public class StartupForm : Form
{
    public enum ChosenMode { None, Local, Remote }
    public ChosenMode Mode { get; private set; } = ChosenMode.None;

    public StartupForm()
    {
        Text = "Bluetooth SPP Auto-Connect - wybierz tryb";
        Width = 440;
        Height = 260;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 10F);

        var lbl = new Label
        {
            Text = "Wybierz tryb pracy aplikacji:",
            Location = new Point(20, 18),
            AutoSize = true
        };
        Controls.Add(lbl);

        var btnLocal = new Button
        {
            Text = "Wersja lokalna\n(paruj urządzenia na TYM komputerze)",
            Location = new Point(20, 55),
            Size = new Size(380, 60),
            TextAlign = ContentAlignment.MiddleCenter
        };
        btnLocal.Click += (s, e) => { Mode = ChosenMode.Local; DialogResult = DialogResult.OK; Close(); };
        Controls.Add(btnLocal);

        var btnRemote = new Button
        {
            Text = "Wersja zdalna\n(połącz się przez sieć z innym komputerem)",
            Location = new Point(20, 125),
            Size = new Size(380, 60),
            TextAlign = ContentAlignment.MiddleCenter
        };
        btnRemote.Click += (s, e) => { Mode = ChosenMode.Remote; DialogResult = DialogResult.OK; Close(); };
        Controls.Add(btnRemote);
    }
}
