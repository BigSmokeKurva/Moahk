using System.Globalization;
using AngleSharp.Html.Parser;
using Moahk.Parser.Data;
using NLog;

namespace Moahk.Parser;

public class TelegramGiftManager : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly HttpClient _client = new();
    private readonly HtmlParser _parser = new();

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task<TelegramGiftInfo> GetGiftInfoAsync(string giftId)
    {
        var response = await _client.GetStringAsync($"https://t.me/nft/{giftId}");
        using var document = await _parser.ParseDocumentAsync(response);
        var collectionElement =
            document.QuerySelector(".tgme_gift_preview > svg:nth-child(1) > g:nth-child(2) > text:nth-child(3)");
        var collection = collectionElement?.TextContent.Trim();
        var modelElement = document.QuerySelector(".table > tbody:nth-child(1) > tr:nth-child(2) > td:nth-child(2)");
        var model = modelElement?.TextContent[..modelElement.TextContent.LastIndexOf(' ')];
        var modelPercentage = modelElement?.QuerySelector("mark")?.TextContent;
        var backdropElement = document.QuerySelector(".table > tbody:nth-child(1) > tr:nth-child(3) > td:nth-child(2)");
        var backdrop = backdropElement?.TextContent[..backdropElement.TextContent.LastIndexOf(' ')];
        var backdropPercentage = backdropElement?.QuerySelector("mark")?.TextContent;
        var symbolElement = document.QuerySelector(".table > tbody:nth-child(1) > tr:nth-child(4) > td:nth-child(2)");
        var symbol = symbolElement?.TextContent[..symbolElement.TextContent.LastIndexOf(' ')];
        var symbolPercentage = symbolElement?.QuerySelector("mark")?.TextContent;
        var quantityElement = document.QuerySelector(".table > tbody:nth-child(1) > tr:nth-child(5) > td:nth-child(2)");
        var quantityText = quantityElement?.TextContent;
        var quantityParts = quantityText?.Split(['/', ' '], StringSplitOptions.RemoveEmptyEntries);
        var signatureElement = document.QuerySelector(".footer");
        if (model is null || modelPercentage is null || backdrop is null ||
            backdropPercentage is null || symbol is null || symbolPercentage is null ||
            quantityParts is null ||
            collection is null) throw new Exception($"Не удалось получить информацию о подарке {giftId}");

        return new TelegramGiftInfo
        {
            Collection = collection,
            Model = (model, double.Parse(modelPercentage[..^1].Replace(',', '.'), CultureInfo.InvariantCulture)),
            Backdrop = (backdrop,
                double.Parse(backdropPercentage[..^1].Replace(',', '.'), CultureInfo.InvariantCulture)),
            Symbol = (symbol, double.Parse(symbolPercentage[..^1].Replace(',', '.'), CultureInfo.InvariantCulture)),
            Quantity = (int.Parse(new string(quantityParts[0].Where(char.IsDigit).ToArray())),
                int.Parse(new string(quantityParts[1].Where(char.IsDigit).ToArray()))),
            Signature = signatureElement is not null,
            Id = giftId
        };
    }
}