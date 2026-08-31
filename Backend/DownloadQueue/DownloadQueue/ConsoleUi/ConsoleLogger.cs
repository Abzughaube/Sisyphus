internal sealed class ConsoleLogger
{
    public void Write(ConsoleColor color, string message)
    {
        WriteInternal(color, message, isError: false);
    }

    public void WriteError(ConsoleColor color, string message)
    {
        WriteInternal(color, message, isError: true);
    }

    private static void WriteInternal(
        ConsoleColor color,
        string message,
        bool isError)
    {
        var originalColor = Console.ForegroundColor;

        try
        {
            Console.ForegroundColor = color;

            if (isError)
                Console.Error.WriteLine(message);
            else
                Console.WriteLine(message);
        }
        finally
        {
            Console.ForegroundColor = originalColor;
        }
    }
}