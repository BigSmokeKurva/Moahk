using System.Net;
using System.Net.Http.Json;
using System.Runtime.Caching;
using NLog;

namespace Moahk.Parser;

public class TonnelRelayerHttpClientPool : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly MemoryCache _cache = new("TonnelRelayerHttpClientPool");
    private readonly (HttpClient Client, string Id)[] _httpClients;
    public readonly int Size;

    public TonnelRelayerHttpClientPool()
    {
        var headers = new Dictionary<string, string>
        {
            ["accept"] = "*/*",
            ["accept-language"] = "ru,en;q=0.9,en-GB;q=0.8,en-US;q=0.7",
            ["origin"] = "https://marketplace.tonnel.network",
            ["priority"] = "u=1, i",
            ["referer"] = "https://marketplace.tonnel.network/",
            ["referrer-policy"] = "strict-origin-when-cross-origin",
            ["sec-ch-ua"] =
                "\"Microsoft Edge WebView2\";v=\"137\", \"Microsoft Edge\";v=\"137\", \"Not/A)Brand\";v=\"24\", \"Chromium\";v=\"137\"",
            ["sec-ch-ua-mobile"] = "?0",
            ["sec-ch-ua-platform"] = "\"Windows\"",
            ["sec-fetch-dest"] = "empty",
            ["sec-fetch-mode"] = "cors",
            ["sec-fetch-site"] = "same-site",
            ["user-agent"] =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36 Edg/137.0.0.0"
        };
        var proxies = LoadProxies();
        if (proxies.Length == 0)
            throw new Exception("Нет доступных прокси");
        _httpClients = proxies.Select((proxy, index) =>
        {
            var handler = new HttpClientHandler
            {
                Proxy = proxy,
                UseProxy = true,
                UseCookies = false
            };
            var client = new HttpClient(handler);
            foreach (var header in headers) client.DefaultRequestHeaders.Add(header.Key, header.Value);
            client.DefaultRequestHeaders.ConnectionClose = true;

            return (client, index.ToString());
        }).ToArray();
        Size = _httpClients.Length;
        Logger.Info($"Создан пул HTTP клиентов с {proxies.Length} прокси");
    }

    public void Dispose()
    {
        foreach (var httpClient in _httpClients) httpClient.Client.Dispose();

        GC.SuppressFinalize(this);
    }

    private static WebProxy[] LoadProxies()
    {
        const string fileName = "proxies.txt";
        var proxies = new List<WebProxy>();
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
                var proxyUri = new Uri($"{parts[0]}://{parts[1]}:{parts[2]}");
                proxies.Add(new WebProxy(proxyUri) { Credentials = new NetworkCredential(parts[3], parts[4]) });
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

    private async Task<HttpClient> GetHttpClient()
    {
        while (true)
        {
            HttpClient? client = null;
            lock (this)
            {
                var clientTuple = _httpClients.FirstOrDefault(c => !_cache.Contains(c.Id));
                if (clientTuple.Client != null)
                {
                    client = clientTuple.Client;
                    _cache.Add(clientTuple.Id, 0, DateTimeOffset.UtcNow.AddSeconds(1.5));
                }
            }

            if (client != null) return client;
            await Task.Delay(100);
        }
    }

    public async Task<HttpResponseMessage> PostAsJsonAsync<T>(string url, T postData)
    {
        for (var i = 0; i < 3; i++)
            try
            {
                var client = await GetHttpClient();
                var response = await client.PostAsJsonAsync(url, postData);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warn(
                        $"Попытка {i + 1}: Ошибка при POST запросе к {url}. Код ответа: {(int)response.StatusCode}");
                    continue;
                }

                return response;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Попытка {i + 1}: Ошибка при POST запросе к {url}");
            }

        throw new Exception($"Не удалось выполнить POST запрос к {url} после 3 попыток");
    }
}