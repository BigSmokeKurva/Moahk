using System.Net.Http.Json;
using Moahk.Parser.ResponseModels;
using NLog;

namespace Moahk.Parser;

public class GiftBubbleRepository : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static GiftBubblesDataGift[] _gifts = [];
    private static readonly object _giftsLock = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly HttpClient _client = new();

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        GC.SuppressFinalize(this);
    }

    public void Start()
    {
        _ = RunRequestLoop();
        Logger.Info("GiftBubbleRepository started");
    }

    private async Task RunRequestLoop()
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                await GetDataGifts();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Ошибка в цикле запросов GiftBubbleRepository");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), _cancellationTokenSource.Token); // Adjust the delay as needed
        }
    }

    private async Task GetDataGifts()
    {
        var response =
            await _client.GetFromJsonAsync<GiftBubblesDataGift[]>("https://gift-bubbles.up.railway.app/data-gifts");
        lock (_giftsLock)
        {
            _gifts = response ?? [];
        }
    }

    public static GiftBubblesDataGift? GetGiftData(string collection)
    {
        lock (_giftsLock)
        {
            return _gifts.FirstOrDefault(x => x.Name == collection);
        }
    }
}