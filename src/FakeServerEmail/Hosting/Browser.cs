using System.ComponentModel;
using System.Diagnostics;

namespace FakeServerEmail.Hosting;

public static class Browser
{
    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Console.WriteLine("Abra no navegador: " + url);
        }
    }
}
