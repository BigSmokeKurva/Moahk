using System.Net.Http.Json;
using System.Runtime.Caching;
using System.Text.Json;
using Microsoft.Playwright;
using Moahk.Parser.ResponseModels;
using NLog;

namespace Moahk.Parser;

public class BrowserContextItem(
    IBrowserContext context,
    IPage page,
    string id,
    bool isAvailable = true)
{
    public IBrowserContext Context { get; } = context;
    public IPage Page { get; } = page;
    public string Id { get; } = id;
    public bool IsAvailable { get; set; } = isAvailable;
}

public class TonnelRelayerBrowserContextPool(TelegramAccountRepository telegramAccountRepository) : IAsyncDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly string _2CaptchaApi = ConfigurationManager.GetString("2CaptchaApi")
                                           ?? throw new Exception("Не задан 2CaptchaApi в конфигурации");

    private readonly MemoryCache _cache = new("TonnelRelayerHttpClientPool");
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly HttpClient _httpClient = new();
    private IBrowser? _browser;
    private BrowserContextItem[]? _browserContexts;
    private IPlaywright? _playwright;

    private CancellationToken StoppingToken => _cancellationTokenSource.Token;

    public int Size { get; private set; }

    public async ValueTask DisposeAsync()
    {
        await _cancellationTokenSource.CancelAsync();
        if (_browserContexts != null)
            foreach (var browserContext in _browserContexts)
            {
                await browserContext.Page.CloseAsync();
                await browserContext.Context.CloseAsync();
            }

        if (_browser is not null)
        {
            await _browser.CloseAsync();
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();

        GC.SuppressFinalize(this);
    }

    public async Task Start()
    {
        if (_playwright is not null)
            throw new Exception("Playwright уже инициализирован");
        _playwright = await Playwright.CreateAsync();
        var proxies = LoadProxies();
        if (proxies.Length == 0)
            throw new Exception("Нет доступных прокси");

        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false,
            Args =
            [
                "--disable-blink-features=AutomationControlled",
                "--mute-audio",
                "--window-size=1920,1080",
                "--no-sandbox",
                "--disable-search-engine-choice-screen",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--no-zygote",
                "--disable-gpu-sandbox",
                "--disable-software-rasterizer",
                "--ignore-certificate-errors",
                "--ignore-ssl-errors",
                // "--disable-web-security",
                "--headless=new",
                "--proxy-server=http://localhost:80"
            ]
        });
        var userAgent = ConfigurationManager.GetString("UserAgent");
        while (telegramAccountRepository.TonnelRelayerTgWebAppData is null && !StoppingToken.IsCancellationRequested)
            await Task.Delay(200, StoppingToken);
        _browserContexts = await Task.WhenAll(proxies.Select(async (proxy, index) =>
        {
            // ProxyServer proxyServer = new();
            // var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Parse("127.0.0.1"), 45000 + index, false);
            // proxyServer.CertificateManager.CertificateValidDays = 365;
            // if (proxyServer.CertificateManager.RootCertificate == null)
            //     await proxyServer.CertificateManager.LoadOrCreateRootCertificateAsync(cancellationToken: StoppingToken);
            // proxyServer.CertificateManager.TrustRootCertificate(true);
            // // linux trust root certificate
            // if (OperatingSystem.IsLinux())
            // {
            //     var rootCert = proxyServer.CertificateManager.RootCertificate;
            //     var certPath = "/usr/local/share/ca-certificates/titaniumproxy.crt";
            //     await File.WriteAllBytesAsync(certPath, rootCert.Export(X509ContentType.Cert), StoppingToken);
            //     // sudo update-ca-certificates
            //     var process = new Process
            //     {
            //         StartInfo = new ProcessStartInfo
            //         {
            //             FileName = "bash",
            //             Arguments = $"-c \"sudo update-ca-certificates\"",
            //             RedirectStandardOutput = true,
            //             RedirectStandardError = true,
            //             UseShellExecute = false,
            //             CreateNoWindow = true
            //         }
            //     };
            //     process.Start();
            // }
            // proxyServer.AddEndPoint(explicitEndPoint);
            var proxyUri = new Uri(proxy.Url);
            // var externalProxy = new ExternalProxy
            // {
            //     HostName = proxUri.Host,
            //     Port = proxUri.Port,
            //     UserName = proxy.Username,
            //     Password = proxy.Password,
            //     ProxyType = proxUri.Scheme == "http" ? ExternalProxyType.Http : ExternalProxyType.Socks5
            // };
            // proxyServer.UpStreamHttpProxy = externalProxy;
            // proxyServer.UpStreamHttpsProxy = externalProxy;
            // await proxyServer.StartAsync(cancellationToken: StoppingToken);
            // var proxyServerUrl = $"http://{explicitEndPoint.IpAddress}:{explicitEndPoint.Port}";
            var proxyServerUrl = $"{proxyUri.Scheme}://{proxyUri.Host}:{proxyUri.Port}";
            var contextOptions = new BrowserNewContextOptions
            {
                Proxy = new Proxy
                {
                    Server = proxyServerUrl,
                    Username = proxy.Username,
                    Password = proxy.Password
                },
                IgnoreHTTPSErrors = true,
                UserAgent = userAgent
            };
            var context = await _browser.NewContextAsync(contextOptions);
            var page = await context.NewPageAsync();
            await page.AddInitScriptAsync(
                """
                console.clear = () => console.log('Console was cleared')
                const i = setInterval(() => {
                    if (window.turnstile) {
                        clearInterval(i)
                        window.turnstile.render = (a, b) => {
                            let params = {
                                sitekey: b.sitekey,
                                pageurl: window.location.href,
                                data: b.cData,
                                pagedata: b.chlPageData,
                                action: b.action,
                                userAgent: navigator.userAgent
                            }
                            console.log('intercepted-params:' + JSON.stringify(params))
                            window.cfCallback = b.callback
                            return
                        }
                    }
                }, 50)
                """);
            var browserContextItem = new BrowserContextItem(context, page, index.ToString());
            page.Console += async (_, msg) =>
            {
                var txt = msg.Text;
                if (!txt.Contains("intercepted-params:"))
                    return;
                txt = txt.Replace("intercepted-params:", string.Empty);
                browserContextItem.IsAvailable = false;
                var json = JsonSerializer.Deserialize<Dictionary<string, string>>(txt)!;
                try
                {
                    Logger.Info("Решение Turnstile");
                    var token = await SolveTurnstile(json);
                    await page.EvaluateAsync($"cfCallback('{token}')");
                    Logger.Info($"Решение Turnstile для {browserContextItem.Id} успешно: {token}");
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Ошибка при решении Turnstile");
                }
                finally
                {
                    browserContextItem.IsAvailable = true;
                    lock (this)
                    {
                        _cache.Remove(browserContextItem.Id);
                    }
                }
            };
            await page.GotoAsync(
                "https://gifts3.tonnel.network/api/pageGifts");
            return browserContextItem;
        }));
        Size = proxies.Length;
        Logger.Info($"Создан пул браузеров с {proxies.Length} прокси");
    }

    private async Task<string> SolveTurnstile(Dictionary<string, string> data)
    {
        var content = new
        {
            key = _2CaptchaApi,
            sitekey = data["sitekey"],
            pageurl = data["pageurl"],
            pagedata = data["pagedata"],
            method = "turnstile",
            data = data["data"],
            action = data["action"],
            useragent = data["userAgent"],
            json = "1"
        };
        var response = await _httpClient.PostAsJsonAsync("https://2captcha.com/in.php", content,
            StoppingToken);

        var taskContent =
            await response.Content.ReadFromJsonAsync<TwoCaptchaCreateTaskResponse>(StoppingToken);
        if (taskContent!.Status != 1)
            throw new Exception($"Ошибка при создании задачи: {taskContent.Request}");
        while (!StoppingToken.IsCancellationRequested)
        {
            await Task.Delay(2000, StoppingToken);
            response = await _httpClient.GetAsync(
                $"http://2captcha.com/res.php?key={_2CaptchaApi}&action=get&json=1&id={taskContent.Request}",
                StoppingToken);
            var taskResultContent = await response.Content.ReadFromJsonAsync<TwoCaptchaResultTaskResponse>(
                StoppingToken);

            if (taskResultContent!.Request == "CAPCHA_NOT_READY")
                continue;
            if (taskResultContent.Request != "CAPCHA_NOT_READY" && taskResultContent.Status == 1)
                return taskResultContent.Request!;
            throw new Exception(
                $"Ошибка при получении решения задачи: {taskResultContent.Request}");
        }

        throw new OperationCanceledException("Задача была отменена");
    }


    private static (string Url, string Username, string Password)[] LoadProxies()
    {
        const string fileName = "proxies.txt";
        var proxies = new List<(string Url, string Username, string Password)>();
        foreach (var line in File.ReadAllLines(fileName))
        {
            var parts = line.Split(':');
            if (parts.Length != 5)
            {
                Logger.Warn($"Неверный формат прокси: {fileName} {line}");
                continue;
            }

            try
            {
                var proxyUrl = $"{parts[0]}://{parts[1]}:{parts[2]}";
                proxies.Add((proxyUrl, parts[3], parts[4]));
            }
            catch (Exception ex)
            {
                Logger.Warn($"Ошибка парсинга прокси: {fileName} {line}. {ex.Message}");
            }
        }

        if (proxies.Count == 0)
            throw new Exception($"Не удалось загрузить ни одного валидного прокси из {fileName}");
        return proxies.ToArray();
    }

    private async Task<BrowserContextItem> GetContext()
    {
        while (true)
        {
            BrowserContextItem? browserContextItem;
            lock (this)
            {
                browserContextItem = _browserContexts!.FirstOrDefault(c => c.IsAvailable && !_cache.Contains(c.Id));
                if (browserContextItem?.Context != null)
                {
                    _cache.Add(browserContextItem.Id, 0, DateTimeOffset.UtcNow.AddSeconds(3));
                    browserContextItem.IsAvailable = false;
                }
            }

            if (browserContextItem != null) return browserContextItem;
            await Task.Delay(100, StoppingToken);
        }
    }

    public async Task<TI?> PostAsJsonAsync<TI, T>(string url, T postData)
    {
        var jsonString = JsonSerializer.Serialize(postData);
        for (var i = 0; i < 5; i++)
        {
            var browserContextItem = await GetContext();
            try
            {
                const string script = """
                                      async ({url, jsonString}) => {
                                        var r = await fetch(url, {
                                          "headers": {
                                            "accept": "*/*",
                                            "accept-language": "ru,en;q=0.9,en-GB;q=0.8,en-US;q=0.7",
                                            "content-type": "application/json",
                                            "priority": "u=1, i",
                                            "sec-ch-ua": "\"Microsoft Edge WebView2\";v=\"137\", \"Microsoft Edge\";v=\"137\", \"Not/A)Brand\";v=\"24\", \"Chromium\";v=\"137\"",
                                            "sec-ch-ua-mobile": "?0",
                                            "sec-ch-ua-platform": "\"Windows\"",
                                            "sec-fetch-dest": "empty",
                                            "sec-fetch-mode": "cors",
                                            "sec-fetch-site": "same-site"
                                          },
                                          "referrer": "https://marketplace.tonnel.network/",
                                          "referrerPolicy": "strict-origin-when-cross-origin",
                                          "body": jsonString,
                                          "method": "POST",
                                          "mode": "cors",
                                          "credentials": "include"
                                        });
                                        return JSON.stringify({
                                          status: r.status,
                                          text: await r.text(),
                                        })
                                      }
                                      """;

                var response = await browserContextItem.Page.EvaluateAsync<string>(script, new
                {
                    url,
                    jsonString
                });
                var result = JsonSerializer.Deserialize<Dictionary<string, object>>(response)!;
                var status = ((JsonElement)result["status"]).GetInt32();
                if (status == 403)
                {
                    try
                    {
                        await browserContextItem.Page.ReloadAsync();
                        lock (this)
                        {
                            _cache.Set(browserContextItem.Id, 0, DateTimeOffset.UtcNow.AddMinutes(1.5));
                        }
                    }
                    catch
                    {
                        // ignored
                    }

                    continue;
                }

                if (status != 200)
                    throw new Exception("Ошибка при выполнении запроса: " + result["text"]);

                return JsonSerializer.Deserialize<TI>(((JsonElement)result["text"]).GetString() ??
                                                      throw new InvalidOperationException());
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Попытка {i + 1}: Ошибка при POST запросе к {url}");
            }
            finally
            {
                browserContextItem.IsAvailable = true;
            }
        }

        throw new Exception($"Не удалось выполнить POST запрос к {url} после 3 попыток");
    }
}