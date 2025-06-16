using System.Web;
using NLog;
using TL;
using WTelegram;

namespace Moahk.Parser;

public class TelegramRepository : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static string? _tonnelRelayerTonnelRelayerTgWebAppData;
    private static string? _portalsTgWebAppData;

    private static string? _tonnelRelayerDecodedTgWebAppData;
    private static string? _portalsDecodedTgWebAppData;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Client _client;


    static TelegramRepository()
    {
        Helpers.Log = (_, _) => { };
    }

    public TelegramRepository()
    {
        _client = new Client(Config);
    }

    public static string? TonnelRelayerTgWebAppData
    {
        get => _tonnelRelayerTonnelRelayerTgWebAppData;
        private set
        {
            _tonnelRelayerTonnelRelayerTgWebAppData = value;
            TonnelRelayerDecodedTgWebAppData = HttpUtility.UrlDecode(value?.Split("&tgWebAppVersion")[0]);
            Logger.Info("TonnelRelayer TgWebAppData updated");
        }
    }

    public static string? TonnelRelayerDecodedTgWebAppData
    {
        get => _tonnelRelayerDecodedTgWebAppData ?? throw new Exception("TgWebAppData is null.");
        private set => _tonnelRelayerDecodedTgWebAppData = value;
    }

    public static string? PortalsTgWebAppData
    {
        get => _portalsTgWebAppData;
        private set
        {
            _portalsTgWebAppData = value;
            PortalsDecodedTgWebAppData = HttpUtility.UrlDecode(value?.Split("&tgWebAppVersion")[0]);
            Logger.Info("Portals TgWebAppData updated");
        }
    }

    public static string? PortalsDecodedTgWebAppData
    {
        get => _portalsDecodedTgWebAppData ?? throw new Exception("Portals TgWebAppData is null.");
        private set => _portalsDecodedTgWebAppData = value;
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        GC.SuppressFinalize(this);
    }

    private async Task RunRequestAppWebViewLoop()
    {
        Logger.Info("Запущен цикл RequestAppWebView");
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                TonnelRelayerTgWebAppData = await RequestAppWebView("tonnel_network_bot", "gift");
                PortalsTgWebAppData = await RequestAppWebView("portals", "market");
            }
            catch (Exception e)
            {
                Logger.Error(e, "Ошибка при выполнении RequestAppWebView");
            }

            await Task.Delay(TimeSpan.FromMinutes(10), _cancellationTokenSource.Token);
        }
    }

    private async Task<string?> RequestAppWebView(string botName, string shortName)
    {
        try
        {
            var chat = await _client.Contacts_ResolveUsername(botName);
            if (chat?.User == null)
            {
                Logger.Error($"Пользователь '{botName}' не найден.");
                return null;
            }

            var app = new InputBotAppShortName
            {
                bot_id = new InputUser(chat.User.ID, chat.User.access_hash),
                short_name = shortName
            };

            var webView = await _client.Messages_RequestAppWebView(
                new InputPeerUser(chat.User.ID, chat.User.access_hash),
                app,
                "desktop",
                write_allowed: true
            );

            if (webView is WebViewResultUrl webViewUrl && webViewUrl.url.Contains("tgWebAppData="))
            {
                var parts = webViewUrl.url.Split("tgWebAppData=");
                return parts.Length > 1 ? parts[1] : null;
            }

            Logger.Error("tgWebAppData не найден в URL.");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Ошибка при получении WebView URL");
            return null;
        }
    }

    private string? Config(string what)
    {
        switch (what)
        {
            case "api_id": return "24448376";
            case "api_hash": return "23ed26560fb3e44fffd1c71319166286";
            case "user_id" when _client.UserId != 0:
                return _client.UserId.ToString();
            case "phone_number":
                Logger.Info("Please enter your phone number in the format +1234567890");
                return Console.ReadLine() ?? throw new NullReferenceException();
            case "verification_code":
                Logger.Info("Please enter the verification code sent to your phone");
                return Console.ReadLine() ?? throw new NullReferenceException();
            case "last_name":
            case "first_name": throw new Exception("User sign-up is required.");
            case "password":
                Logger.Info("Please enter your password");
                return Console.ReadLine() ?? throw new NullReferenceException();
            case "session_pathname":
                var dir = Path.Combine(Directory.GetCurrentDirectory(), "session");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return Path.Combine(dir, "session.dat");
            default: return null;
        }
    }

    public async Task Start()
    {
        try
        {
            var myself = await _client.LoginUserIfNeeded();

            Logger.Info($"We are logged-in as {myself.MainUsername} (id {myself.ID})");

            _ = RunRequestAppWebViewLoop();
        }
        catch (Exception ex)
        {
            Logger.Fatal(ex, "An error occurred while logging in.");
            throw;
        }
    }
}