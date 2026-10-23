namespace FakeServerEmail.Hosting;

public static class ConsoleOutput
{
    public static void Write(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    public static void TrySetTitle(string title)
    {
        try
        {
            Console.Title = title;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
        }
    }

    public static void WaitForKey()
    {
        if (Console.IsInputRedirected)
            return;
        Console.WriteLine();
        Console.WriteLine("Pressione qualquer tecla para fechar.");
        try
        {
            Console.ReadKey(true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
