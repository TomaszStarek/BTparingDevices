using System;
using System.Windows.Forms;

namespace BTparingDevices;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var startup = new StartupForm();
        if (startup.ShowDialog() != DialogResult.OK || startup.Mode == StartupForm.ChosenMode.None)
            return;

        if (startup.Mode == StartupForm.ChosenMode.Remote)
            Application.Run(new RemoteForm());
        else
            Application.Run(new LocalForm());
    }
}
