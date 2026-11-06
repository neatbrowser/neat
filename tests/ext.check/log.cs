namespace neat;

// Stands in for the app's Log (src/neat/web/log.cs, which needs WinUI) so ext.cs compiles here.
internal static class Log
{
    public static string? Last;

    public static void Write(string tag, string msg) => Last = msg;

    public static void Error(string tag, Exception? ex, string? note = null) => Last = note;
}
