using neat;

// Standalone shim for the diagnostic logger used by Session. The self-check deliberately
// compiles only session.cs so it can run on any .NET 8 host without WinUI or browser dependencies.
namespace neat
{
    internal static class Log
    {
        public static void Error(string tag, Exception ex, string message) { }
    }
}

