using Moahk.Data;
using Moahk.Parser;
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
        await ApplicationDbContextInitialiser.InitialiseAsync();
        using var telegramRepository = new TelegramRepository();
        await telegramRepository.Start();
        using var parser = new Parser.Parser();
        await parser.Start();
        // var bot = new TelegramBot();
        Console.ReadKey();
    }
}