// Entry point. WinForms apps are conventionally a static Main with
// [STAThread] (single-threaded apartment, required for COM/clipboard/
// file dialog interop) that initializes the application config and
// runs the main form.

namespace VostokModManager;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
