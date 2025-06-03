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
        using var telegramRepository = new TelegramRepository();
        await telegramRepository.Start();
        using var parser = new Parser();
        await parser.Start();
        Console.ReadKey();


        //     using var client = new HttpClient(new HttpClientHandler()
        //     {
        //         // socks5:161.123.209.134:6634:ozyrlnes:pzcflzw08d1w
        //         Proxy = new WebProxy("socks5://104.239.76.74:6733")
        //         {
        //             Credentials = new NetworkCredential("ozyrlnes", "pzcflzw08d1w")
        //         },
        //     });
        //     client.DefaultRequestHeaders.Add("accept", "*/*");
        //     client.DefaultRequestHeaders.Add("accept-language", "ru,en;q=0.9,en-GB;q=0.8,en-US;q=0.7");
        //     client.DefaultRequestHeaders.Add("origin", "https://marketplace.tonnel.network");
        //     client.DefaultRequestHeaders.Add("priority", "u=1, i");
        //     client.DefaultRequestHeaders.Add("referer", "https://marketplace.tonnel.network/");
        //     client.DefaultRequestHeaders.Add("sec-ch-ua", "\"Microsoft Edge\";v=\"136\", \"Microsoft Edge WebView2\";v=\"136\", \"Not.A/Brand\";v=\"99\", \"Chromium\";v=\"136\"");
        //     client.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
        //     client.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
        //     client.DefaultRequestHeaders.Add("sec-fetch-dest", "empty");
        //     client.DefaultRequestHeaders.Add("sec-fetch-mode", "cors");
        //     client.DefaultRequestHeaders.Add("sec-fetch-site", "same-site");
        //     client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36 Edg/136.0.0.0");
        //     
        //     var allCount = 0;
        //     var page = 1;
        //     while (true)
        //     {
        //         using var r = await client.PostAsJsonAsync("https://gifts2.tonnel.network/api/pageGifts", new
        //         {
        //             page,
        //             limit = 30,
        //             sort = "{\"message_post_time\":-1,\"gift_id\":-1}",
        //             filter = "{\"price\":{\"$exists\":true},\"buyer\":{\"$exists\":false},\"asset\":\"TON\"}",
        //             @ref = 0,
        //             price_range = (object?)null,
        //             user_auth = "user=%7B%22id%22%3A1213195844%2C%22first_name%22%3A%22BigSmokeKurva%22%2C%22last_name%22%3A%22%22%2C%22username%22%3A%22BigSmokeKurva%22%2C%22language_code%22%3A%22ru%22%2C%22allows_write_to_pm%22%3Atrue%2C%22photo_url%22%3A%22https%3A%5C%2F%5C%2Ft.me%5C%2Fi%5C%2Fuserpic%5C%2F320%5C%2FoN7mPYycdlgwi73Rf4I7zcJuzYNADTGv1eejMsWZltE.svg%22%7D&chat_instance=991134629403625865&chat_type=sender&auth_date=1748269136&signature=FofV1H-ucpRCoDrf31cwx4gR_DYYvjpNvjVdRbVNR6a8YY7ABrCHNAXPZJi-K-qgAfZLzUM_NIiVnEJWTrSIAw&hash=6b51f86f59451b5a7b9553f3740cbb1e1ecea659fcc834ebbf634c5f3bc34d01"
        //         });
        //     
        //         if (r.IsSuccessStatusCode)
        //         {
        //             var data = await r.Content.ReadFromJsonAsync<object[]>();
        //             allCount += data?.Length ?? 0;
        //             Console.WriteLine($"Status: {r.StatusCode}; DataLength: {data?.Length ?? 0}; Page: {page}; AllCount: {allCount}");
        //         }
        //         else
        //         {
        //             Console.WriteLine($"Status: {r.StatusCode}; Page: {page}; Error");
        //         }
        //         page++;
        //     }
    }
}