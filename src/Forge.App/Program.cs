using Avalonia;
using System.Runtime.InteropServices;

namespace Forge.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            var message = $"Forge Tools could not start.\n\n{exception.Message}\n\nDetails were written to startup-error.log.";

            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.log"), exception.ToString());
            }
            catch
            {
                // The install directory may itself be inaccessible. Preserve the original startup error.
            }

            if (OperatingSystem.IsWindows())
                MessageBox(IntPtr.Zero, message, "Forge Tools startup error", 0x10);
            else
                Console.Error.WriteLine(message);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
