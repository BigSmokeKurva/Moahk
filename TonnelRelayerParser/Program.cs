using Moahk.Data;
using NLog;

namespace Moahk;

internal static class Program
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    static Program()
    {
        LogManager.Setup().LoadConfigurationFromFile("nlog.config");
    }

    private static async Task Main()
    {
        Microsoft.Playwright.Program.Main(["install", "chromium"]);
        await ApplicationDbContextInitialiser.InitialiseAsync();
        await using var parser = new Parser.Parser();
        await parser.Start();
        await Task.Delay(Timeout.Infinite);
    }
}