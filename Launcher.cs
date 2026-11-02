using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class Launcher
{
    [STAThread]
    private static void Main()
    {
        var folder = AppDomain.CurrentDomain.BaseDirectory;
        var app = Path.Combine(folder, "OtterDesktop.App.exe");
        var runtime = Path.Combine(folder, "runtime");

        if (!File.Exists(app) || !Directory.Exists(runtime))
        {
            MessageBox.Show(
                "Otter Desktop is missing part of its application folder. Extract the complete ZIP before running it.",
                "Otter Desktop",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(app)
            {
                WorkingDirectory = folder,
                UseShellExecute = false
            };
            startInfo.EnvironmentVariables["DOTNET_ROOT"] = runtime;
            startInfo.EnvironmentVariables["DOTNET_ROOT_X64"] = runtime;
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Otter Desktop could not start.\n\n" + ex.Message,
                "Otter Desktop",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
