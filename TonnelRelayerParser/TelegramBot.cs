using Moahk.ResponseModels;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Moahk;

public class TelegramBot
{
    private static readonly long[] AdminId = [7458768874, 7293810669];
    private readonly TelegramBotClient _botClient = new("7989604756:AAH3CJeYIa_lzHecT4uGgGuFbOXaRR9APyM");
    private long _chatId;
    private long _chatId2;

    public TelegramBot()
    {
        if (File.Exists("chat_id.txt"))
        {
            var chatIdText = File.ReadAllText("chat_id.txt");
            _chatId = long.TryParse(chatIdText, out var chatId) ? chatId : 0;
        }
        else
        {
            _chatId = 0;
        }

        if (File.Exists("chat_id2.txt"))
        {
            var chatIdText = File.ReadAllText("chat_id2.txt");
            _chatId2 = long.TryParse(chatIdText, out var chatId) ? chatId : 0;
        }
        else
        {
            _chatId2 = 0;
        }

        _botClient.OnMessage += async (sender, args) =>
        {
            var parts = sender.Text?.Split(' ', 2);
            var command = parts?[0].ToLowerInvariant();
            var argsText = parts is { Length: > 1 } ? parts[1] : string.Empty;

            if (command != null && command.StartsWith('/')) await OnCommand(command, argsText, sender);
        };
    }

    private async Task OnCommand(string command, string args, Message msg)
    {
        switch (command)
        {
            case "/chat" when msg.From != null && AdminId.Contains(msg.From.Id):
                _chatId = msg.Chat.Id;
                await File.WriteAllTextAsync("chat_id.txt", _chatId.ToString());
                await _botClient.SendMessage(msg.Chat, "OK");
                break;
            case "/chat2" when msg.From != null && AdminId.Contains(msg.From.Id):
                _chatId2 = msg.Chat.Id;
                await File.WriteAllTextAsync("chat_id2.txt", _chatId2.ToString());
                await _botClient.SendMessage(msg.Chat, "OK");
                break;
        }
    }

    public async Task SendMessageAsync((GiftInfo, TonnelRelayerGiftInfo) giftInfo, double currentPrice,
        double middlePrice,
        double percentDiff, Activity activity)
    {
        var msg = $"""
                   Подарок: <b>{giftInfo.Item2.Name}</b>
                   Модель: <b>{giftInfo.Item2.Model}</b>
                   Фон: <b>{giftInfo.Item2.Backdrop}</b>
                   Цена сейчас: <b>{currentPrice:F2}</b>
                   Сред. Макс. цена за 14 дней: <b>{middlePrice:F2}</b>
                   Разница: <b>{percentDiff:F2}%</b>
                   Активность: <b>{activity switch
                   {
                       Activity.Low => "Низкая",
                       Activity.Medium => "Средняя",
                       _ => "Высокая"
                   }}</b>
                   {(giftInfo.Item1.IsSold ? "<b>Грязный</b>" : null)}
                   """;
        // клавиатура с двумя кнопками (ссылками)
        var keyboard = new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithUrl("Ссылка на подарок",
                    $"https://t.me/nft/{giftInfo.Item1.Id}"),
                InlineKeyboardButton.WithUrl("Ссылка на бота",
                    $"https://t.me/tonnel_network_bot/gift?startapp={giftInfo.Item2.GiftId}")
            ],
            [
                InlineKeyboardButton.WithUrl("Ссылка на сайт",
                    $"https://market.tonnel.network/?giftDrawerId={giftInfo.Item2.GiftId}")
            ]
        ]);
        await _botClient.SendMessage(
            _chatId,
            msg,
            ParseMode.Html,
            replyMarkup: keyboard
        );
    }

    public async Task SendMessage2Async((GiftInfo, TonnelRelayerGiftInfo) giftInfo, double currentPrice,
        double middlePrice,
        double percentDiff, Activity activity, PortalsSearch.Result portalsSearchResponse)
    {
        var msg = $"""
                   🎁 <b>{giftInfo.Item2.Name} | {giftInfo.Item2.Model} ({giftInfo.Item2.Backdrop})</b> 🎨
                   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
                   💎 Цены:
                   ▫️ PORTAL: {currentPrice:F2}  ({percentDiff:F2}%)
                   ▫️ Сред. макс. (14 дн): {middlePrice:F2}
                   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
                   ⚠️ Активность: {activity switch
                   {
                       Activity.Low => "Низкая",
                       Activity.Medium => "Средняя",
                       _ => "Высокая"
                   }}
                   🧹 Состояние: {(giftInfo.Item1.IsSold ? "Грязный" : "Чистый")}
                   """;
        // клавиатура с двумя кнопками (ссылками)
        var keyboard = new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithUrl("Ссылка на подарок",
                    $"https://t.me/nft/{giftInfo.Item1.Id}"),
                InlineKeyboardButton.WithUrl("Ссылка на бота",
                    $"https://t.me/portals/market?startapp=gift_{portalsSearchResponse.Id}")
            ]
        ]);
        await _botClient.SendMessage(
            _chatId2,
            msg,
            ParseMode.Html,
            replyMarkup: keyboard
        );
    }
}