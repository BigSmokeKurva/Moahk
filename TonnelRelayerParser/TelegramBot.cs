using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Moahk.Data;
using Moahk.Data.Enums;
using Moahk.Parser;
using NLog;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Moahk;

public class TelegramBot
{
    private static readonly long[] Admins = ConfigurationManager.GetLongArray("Admins");
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
    private readonly TelegramBotClient _botClient = new(ConfigurationManager.GetString("BotToken"));
    private readonly User _me;

    public TelegramBot()
    {
        _me = _botClient.GetMe().Result;
        _botClient.OnMessage += OnMessage;
        _botClient.OnUpdate += OnUpdate;
        _botClient.OnError += (sender, args) =>
        {
            Logger.Error(sender, "An error occurred in the Telegram bot.");
            return Task.CompletedTask;
        };
    }

    private async Task OnMessage(Message msg, UpdateType type)
    {
        await using var dbContext = new ApplicationDbContext();
        var user = await dbContext.AddUserAsync(msg.From?.Id ?? throw new Exception("UserId is null"));
        if (msg.Text is not { } text || msg.Chat.Type != ChatType.Private)
            return;
        if (text.StartsWith('/'))
        {
            var space = text.IndexOf(' ');
            if (space < 0) space = text.Length;
            var command = text[..space].ToLower();
            if (command.LastIndexOf('@') is > 0 and var at)
                if (command[(at + 1)..].Equals(_me.Username, StringComparison.OrdinalIgnoreCase))
                    command = command[..at];
                else
                    return;
            await OnCommand(command, text[space..].TrimStart(), msg, dbContext, user);
        }
        else
        {
            await OnTextMessage(msg, dbContext, user);
        }
    }

    private async Task OnTextMessage(Message msg, ApplicationDbContext dbContext, Data.Entities.User user)
    {
        switch (msg.Text)
        {
            case "Главное меню":
                await StartCommand(msg, dbContext, user, false);
                break;
            case not null when user.Status == Status.WritingPriceRange:
                await PriceRangeSetValue(msg, dbContext, user);
                break;
            case not null when user.Status == Status.WritingProfitPercent:
                await ProfitSetCustomValue(msg, dbContext, user);
                break;
        }
    }

    private async Task OnCommand(string command, string args, Message msg, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        switch (command)
        {
            case "/start":
                await StartCommand(msg, dbContext, user);
                break;
        }
    }

    private async Task StartCommand(Message msg, ApplicationDbContext dbContext, Data.Entities.User user,
        bool sendMenu = true)
    {
        user.Status = Status.None;
        await dbContext.SaveChangesAsync();
        if (sendMenu)
            await _botClient.SendDice(msg.From!.Id, replyMarkup: new ReplyKeyboardMarkup([
                [new KeyboardButton("Главное меню")]
            ]) { ResizeKeyboard = true });

        var licenseActive = DateTimeOffset.UtcNow < user.License;
        var buttons = new List<List<InlineKeyboardButton>>
        {
            new() { InlineKeyboardButton.WithCallbackData("Продлить", "renew_license") }
        };
        if (licenseActive)
            buttons.Add([
                InlineKeyboardButton.WithCallbackData("Запуск и остановка", "start_stop"),
                InlineKeyboardButton.WithCallbackData("Настройка фильтров", "filters")
            ]);
        var keyboard = new InlineKeyboardMarkup(buttons);
        await _botClient.SendMessage(msg.From!.Id, $"""
                                                    Приветствие
                                                    {(licenseActive ? $"Лицензия: {user.License:yyyy-MM-dd HH:mm} UTC" : "Нет активной лицензии")}
                                                    """, replyMarkup: keyboard);
    }

    private async Task OnUpdate(Update update)
    {
        switch (update)
        {
            case { CallbackQuery: { } callbackQuery }: await OnCallbackQuery(callbackQuery); break;
            default: Logger.Info($"Received unhandled update {update.Type}"); break;
        }
    }

    private async Task OnCallbackQuery(CallbackQuery callbackQuery)
    {
        await using var dbContext = new ApplicationDbContext();
        var user = await dbContext.AddUserAsync(callbackQuery.From?.Id ?? throw new Exception("UserId is null"));
        if (callbackQuery?.Message?.Chat.Type != ChatType.Private)
            return;
        switch (callbackQuery.Data)
        {
            case "renew_license":
                await RenewLicenseCallbackQuery(callbackQuery, dbContext, user);
                break;
            case "filters":
                await FiltersCallbackQuery(callbackQuery, dbContext, user);
                break;
            case "price_range":
                await PriceRangeCallbackQuery(callbackQuery, dbContext, user);
                break;
            case "profit":
                await ProfitCallbackQuery(callbackQuery, dbContext, user);
                break;
            case "profit_set_10":
            case "profit_set_20":
            case "profit_set_30":
                await ProfitSetCallbackQuery(callbackQuery, dbContext, user);
                break;
            case "profit_set_custom":
                await ProfitSetCustomCallbackQuery(callbackQuery, dbContext, user);
                break;
            case "criteria":
                await CriteriaCallbackQuery(callbackQuery, dbContext, user);
                break;
            case "criteria_peak":
            case "criteria_percentile75":
            case "criteria_second_floor":
                await CriteriaSetCallbackQuery(callbackQuery, dbContext, user);
                break;
            case "start_stop":
                await StartStopCallbackQuery(callbackQuery, dbContext, user);
                break;
            case "start":
                await StartCallbackQuery(callbackQuery, dbContext, user);
                break;
            case "stop":
                await StopCallbackQuery(callbackQuery, dbContext, user);
                break;
        }

        await _botClient.AnswerCallbackQuery(callbackQuery.Id);
    }

    private async Task RenewLicenseCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        user.License = DateTimeOffset.UtcNow.AddDays(30);
        await dbContext.SaveChangesAsync();
        await _botClient.SendMessage(callbackQuery.From.Id, "Лицензия успешно продлена на 30 дней.");
    }

    private async Task<bool> CheckLicense(Data.Entities.User user)
    {
        if (user.License >= DateTimeOffset.UtcNow) return false;
        await _botClient.SendMessage(user.Id,
            "У вас нет активной лицензии для использования фильтров.");
        return true;
    }

    private async Task FiltersCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;

        var keyboard = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData("Диапазон цен", "price_range")
            ],
            [
                InlineKeyboardButton.WithCallbackData("Выгода", "profit")
            ],
            [
                InlineKeyboardButton.WithCallbackData("Критерии", "criteria")
            ]
        ]);
        await _botClient.SendMessage(callbackQuery.From.Id, $"""
                                                             Текущие фильтры:
                                                             Диапазон цен: {user.PriceMin:0.00} - {user.PriceMax:0.00}
                                                             Выгода: {user.ProfitPercent}%
                                                             Критерии: {user.Criteria switch {
                                                                 Criteria.Peak => "сравнение с самой высокой цене за 2 недели",
                                                                 Criteria.Percentile75 => "75-й процентиль",
                                                                 Criteria.SecondFloor => "Разница с 2ым флором",
                                                                 _ => string.Empty }}
                                                             Выберите действие:
                                                             """, replyMarkup: keyboard);
    }

    private async Task PriceRangeCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        user.Status = Status.WritingPriceRange;
        await dbContext.SaveChangesAsync();
        await _botClient.SendMessage(callbackQuery.From.Id,
            """
            Введите диапазон цен в формате: <минимальная цена> <максимальная цена>.
            Например: 5 15
            Например: 5,50 15,11
            Для отмены введите '/start'.
            """);
    }

    private async Task PriceRangeSetValue(Message msg, ApplicationDbContext dbContext, Data.Entities.User user)
    {
        var parts = msg.Text?.Replace('.', ',').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: 2 })
        {
            await _botClient.SendMessage(msg.From!.Id,
                "Неверный формат. Введите диапазон цен в формате: <минимальная цена> <максимальная цена>.");
            return;
        }

        if (!double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var min) ||
            !double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var max))
        {
            await _botClient.SendMessage(msg.From!.Id,
                "Неверный формат. Введите диапазон цен в формате: <минимальная цена> <максимальная цена>.");
        }
        else if (min < 0 || max < 0 || min >= max)
        {
            await _botClient.SendMessage(msg.From!.Id,
                "Неверный диапазон цен. Минимальная цена должна быть меньше максимальной и больше или равна нулю.");
        }
        else
        {
            user.PriceMin = min;
            user.PriceMax = max;
            user.Status = Status.None;
            await dbContext.SaveChangesAsync();
            await _botClient.SendMessage(msg.From!.Id,
                $"Диапазон цен успешно установлен: {min} - {max}.");
        }
    }

    private async Task ProfitCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        var keyboard = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData("10%", "profit_set_10"),
                InlineKeyboardButton.WithCallbackData("20%", "profit_set_20"),
                InlineKeyboardButton.WithCallbackData("30%", "profit_set_30")
            ],
            [
                InlineKeyboardButton.WithCallbackData("Ввести свой процент", "profit_set_custom")
            ]
        ]);
        await _botClient.SendMessage(callbackQuery.From.Id, $"""
                                                             Текущий процент прибыли: {user.ProfitPercent}%
                                                             Выберите действие:
                                                             """, replyMarkup: keyboard);
    }

    private async Task ProfitSetCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var percent = callbackQuery.Data switch
        {
            "profit_set_10" => 10,
            "profit_set_20" => 20,
            "profit_set_30" => 30,
            _ => 0
        };
        user.ProfitPercent = percent;
        await dbContext.SaveChangesAsync();
        await _botClient.SendMessage(callbackQuery.From.Id,
            $"Процент прибыли успешно установлен: {percent}%.");
    }

    private async Task ProfitSetCustomCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        user.Status = Status.WritingProfitPercent;
        await dbContext.SaveChangesAsync();
        await _botClient.SendMessage(callbackQuery.From.Id,
            """
            Введите процент прибыли в формате: <процент>.
            Например: 10
            Для отмены введите '/start'.
            """);
    }

    private async Task ProfitSetCustomValue(Message msg, ApplicationDbContext dbContext, Data.Entities.User user)
    {
        if (!int.TryParse(msg.Text, out var percent) || percent < 0)
        {
            await _botClient.SendMessage(msg.From!.Id,
                "Неверный формат. Введите процент прибыли в формате: <процент>.");
        }
        else
        {
            user.ProfitPercent = percent;
            user.Status = Status.None;
            await dbContext.SaveChangesAsync();
            await _botClient.SendMessage(msg.From!.Id,
                $"Процент прибыли успешно установлен: {percent}%.");
        }
    }

    private async Task CriteriaCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;

        var keyboard = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData("Сравнение с самой высокой цене за 2 недели", "criteria_peak"),
                InlineKeyboardButton.WithCallbackData("75-й процентиль", "criteria_percentile75")
            ],
            [
                InlineKeyboardButton.WithCallbackData("Разница с 2ым флором", "criteria_second_floor")
            ]
        ]);
        await _botClient.SendMessage(callbackQuery.From.Id, $"""
                                                             Текущие критерии: {user.Criteria switch {
                                                                 Criteria.Peak => "сравнение с самой высокой цене за 2 недели",
                                                                 Criteria.Percentile75 => "75-й процентиль",
                                                                 Criteria.SecondFloor => "Разница с 2ым флором",
                                                                 _ => string.Empty }}
                                                             Выберите действие:
                                                             """, replyMarkup: keyboard);
    }

    private async Task CriteriaSetCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var criteria = callbackQuery.Data switch
        {
            "criteria_peak" => Criteria.Peak,
            "criteria_percentile75" => Criteria.Percentile75,
            "criteria_second_floor" => Criteria.SecondFloor,
            _ => user.Criteria
        };
        user.Criteria = criteria;
        await dbContext.SaveChangesAsync();
        await _botClient.SendMessage(callbackQuery.From.Id,
            $"Критерии успешно установлены: {criteria}.");
    }

    private async Task StartStopCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        var keyboard = new InlineKeyboardMarkup([
            [
                user.IsStarted
                    ? InlineKeyboardButton.WithCallbackData("Остановить", "stop")
                    : InlineKeyboardButton.WithCallbackData("Запустить", "start")
            ]
        ]);
        await _botClient.SendMessage(callbackQuery.From.Id,
            $"Текущее состояние: {(user.IsStarted ? "Запущено" : "Остановлено")}", replyMarkup: keyboard);
    }

    private async Task StartCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        user.IsStarted = true;
        await dbContext.SaveChangesAsync();
        await StartStopCallbackQuery(callbackQuery, dbContext, user);
    }

    private async Task StopCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        user.IsStarted = false;
        await dbContext.SaveChangesAsync();
        await StartStopCallbackQuery(callbackQuery, dbContext, user);
    }

    public async Task SendSignal(string name, string model, double price, double percentDiff, bool isSold,
        Activity activity,
        string tgUrl, string botUrl,
        string? siteUrl, string botName, Criteria criteria)
    {
        await using var dbContext = new ApplicationDbContext();
        var users = await dbContext.Users
            .AsNoTracking()
            .Where(x => x.IsStarted && x.License >= DateTimeOffset.UtcNow && x.Criteria == criteria &&
                        x.PriceMin <= price && x.PriceMax >= price && x.ProfitPercent <= percentDiff)
            .ToArrayAsync();
        var msg = $"""
                   [🎁]({tgUrl})  *{name} | {model}* 🎨

                   🔀{botName.ToUpper()}

                   ━━━━━━━━━━━━━━━━━━━ 
                   💲 *Цена*: {price:F2} TON
                   💹 *Перспектива*: +{percentDiff:F2}%
                   ✅ *Состояние*: {(isSold ? "Грязный" : "Чистый")}  
                   🔥 *Активность*: {activity switch
                   {
                       Activity.Low => "Низкая",
                       Activity.Medium => "Средняя",
                       _ => "Высокая"
                   }}  
                   ━━━━━━━━━━━━━━━━━━━ 
                   """;
        var buttons = new List<List<InlineKeyboardButton>>([
            [
                InlineKeyboardButton.WithUrl("Подарок", tgUrl),
                InlineKeyboardButton.WithUrl(botName.ToUpper(), botUrl)
            ]
        ]);
        if (siteUrl != null)
            buttons.Add([InlineKeyboardButton.WithUrl("Сайт", siteUrl)]);
        var keyboard = new InlineKeyboardMarkup(buttons);
        foreach (var user in users)
            try
            {
                await _botClient.SendMessage(user.Id, msg, replyMarkup: keyboard,
                    parseMode: ParseMode.Markdown);
            }
            catch
            {
                // ignored
            }
    }
}