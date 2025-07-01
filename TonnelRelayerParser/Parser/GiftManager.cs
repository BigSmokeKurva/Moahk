using System.Globalization;
using AngleSharp.Html.Parser;
using NLog;

namespace Moahk.Parser;

public static class GiftManager
{
    private static readonly HttpClient Client = new();
    private static readonly HtmlParser Parser = new();
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static async Task<GiftInfo> GetGiftInfoAsync(string giftId)
    {
        var response = await Client.GetStringAsync($"https://t.me/nft/{giftId}");
        using var document = await Parser.ParseDocumentAsync(response);
        var modelElement = document.QuerySelector(".table > tbody:nth-child(1) > tr:nth-child(2) > td:nth-child(2)");
        var model = modelElement?.TextContent;
        var modelPercentage = modelElement?.QuerySelector("mark")?.TextContent;
        var backdropElement = document.QuerySelector(".table > tbody:nth-child(1) > tr:nth-child(3) > td:nth-child(2)");
        var backdrop = backdropElement?.TextContent;
        var backdropPercentage = backdropElement?.QuerySelector("mark")?.TextContent;
        var symbolElement = document.QuerySelector(".table > tbody:nth-child(1) > tr:nth-child(4) > td:nth-child(2)");
        var symbol = symbolElement?.TextContent;
        var symbolPercentage = symbolElement?.QuerySelector("mark")?.TextContent;
        var quantityElement = document.QuerySelector(".table > tbody:nth-child(1) > tr:nth-child(5) > td:nth-child(2)");
        var quantityText = quantityElement?.TextContent;
        var quantityParts = quantityText?.Split(['/', ' '], StringSplitOptions.RemoveEmptyEntries);
        var isSoldElement = document.QuerySelector(".footer");
        if (model is null || modelPercentage is null || backdrop is null ||
            backdropPercentage is null || symbol is null || symbolPercentage is null ||
            quantityParts is null) throw new Exception($"Не удалось получить информацию о подарке {giftId}");

        return new GiftInfo
        {
            Model = (model, double.Parse(modelPercentage[..^1].Replace(',', '.'), CultureInfo.InvariantCulture)),
            Backdrop = (backdrop,
                double.Parse(backdropPercentage[..^1].Replace(',', '.'), CultureInfo.InvariantCulture)),
            Symbol = (symbol, double.Parse(symbolPercentage[..^1].Replace(',', '.'), CultureInfo.InvariantCulture)),
            Quantity = (int.Parse(new string(quantityParts[0].Where(char.IsDigit).ToArray())),
                int.Parse(new string(quantityParts[1].Where(char.IsDigit).ToArray()))),
            IsSold = isSoldElement is not null,
            Id = giftId
        };
    }
}