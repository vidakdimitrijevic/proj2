public static class ThreadSafeLogger
{
    private static readonly object logLock = new();

    public static void Info(string message)
    {
        lock (logLock)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
        }
    }
}
