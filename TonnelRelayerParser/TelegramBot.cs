using System.Globalization;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Moahk.Data;
using Moahk.Data.Entities;
using Moahk.Data.Enums;
using Moahk.Parser;
using Moahk.Parser.ResponseModels;
using NLog;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using User = Telegram.Bot.Types.User;

namespace Moahk;

public class TelegramBot : IDisposable
{
    private static readonly long[] Admins = ConfigurationManager.GetLongArray("Admins");
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    private readonly TelegramBotClient _botClient =
        new(ConfigurationManager.GetString("BotToken") ?? throw new InvalidOperationException());

    private readonly string _crystalpayLogin =
        ConfigurationManager.GetString("CrystalpayLogin") ?? throw new InvalidOperationException();

    private readonly string _crystalpaySecret =
        ConfigurationManager.GetString("CrystalpaySecret") ?? throw new InvalidOperationException();

    private readonly HttpClient _httpClient = new();
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

    public void Dispose()
    {
        _httpClient.Dispose();
        _botClient.Close();
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
            case "/find" when Admins.Contains(msg.From!.Id):
                await AdminFindCommand(msg, args, dbContext, user);
                break;
            case "/set_time" when Admins.Contains(msg.From!.Id):
                await AdminSetTimeCommand(msg, args, dbContext, user);
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

    private async Task AdminFindCommand(Message msg, string args, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (!long.TryParse(args, out var id))
        {
            await _botClient.SendMessage(msg.From!.Id,
                "Неверный формат. Используйте: /find <user_id>");
            return;
        }

        var foundUser = await dbContext.Users.FirstOrDefaultAsync(x => x.Id == id);
        if (foundUser is null)
        {
            await _botClient.SendMessage(msg.From!.Id, "Пользователь не найден.");
            return;
        }

        await _botClient.SendMessage(msg.From!.Id, $"""
                                                    Пользователь найден:
                                                    ID: {foundUser.Id}
                                                    Лицензия: {foundUser.License:yyyy-MM-dd HH:mm} UTC
                                                    Диапазон цен: {foundUser.PriceMin:0.00} - {foundUser.PriceMax:0.00}
                                                    Процент прибыли: {foundUser.ProfitPercent}%
                                                    Критерии: {foundUser.Criteria switch {
                                                        Criteria.Peak => "сравнение с самой высокой цене за 2 недели",
                                                        Criteria.Percentile75 => "75-й процентиль",
                                                        Criteria.SecondFloor => "Разница с 2ым флором",
                                                        _ => string.Empty }}
                                                    Статус: {foundUser.Status}
                                                    Запущено: {foundUser.IsStarted}
                                                    """);
    }

    private async Task AdminSetTimeCommand(Message msg, string args, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        // Пример: /set_time 676456478 2023-10-01T12:00:00
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var userId) ||
            !DateTimeOffset.TryParse(parts[1], null, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var newTime))
        {
            await _botClient.SendMessage(msg.From!.Id,
                "Неверный формат. Используйте: /set_time <user_id> <new_time>");
            return;
        }

        var foundUser = await dbContext.Users.FirstOrDefaultAsync(x => x.Id == userId);
        if (foundUser is null)
        {
            await _botClient.SendMessage(msg.From!.Id, "Пользователь не найден.");
            return;
        }

        foundUser.License = newTime;
        await dbContext.SaveChangesAsync();
        await _botClient.SendMessage(msg.From!.Id, $"""
                                                    Лицензия пользователя {foundUser.Id} успешно изменена на {newTime:yyyy-MM-dd HH:mm} UTC.
                                                    """);
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
            case "renew_license_1":
            case "renew_license_30":
                await RenewLicenseDaysCallbackQuery(callbackQuery, dbContext, user);
                break;
            case "renew_license_crystalpay_1":
            case "renew_license_crystalpay_30":
                await RenewLicenseCrystalpayCallbackQuery(callbackQuery, dbContext, user);
                break;
            case { } data when data.StartsWith("check_crystalpay_"):
                await CheckCrystalpayCallbackQuery(callbackQuery, dbContext, user);
                break;
        }

        try
        {
            await _botClient.AnswerCallbackQuery(callbackQuery.Id);
        }
        catch
        {
            // ignored
        }
    }

    private async Task RenewLicenseCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        // 1, 30
        var keyboard = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData("1 день", "renew_license_1"),
                InlineKeyboardButton.WithCallbackData("30 дней", "renew_license_30")
            ]
        ]);
        await _botClient.SendMessage(callbackQuery.From.Id, "Выберите количество дней для продления лицензии:",
            replyMarkup: keyboard);
    }

    private async Task RenewLicenseDaysCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var days = callbackQuery.Data switch
        {
            "renew_license_1" => 1,
            "renew_license_30" => 30,
            _ => throw new Exception("Неверное количество дней для продления лицензии.")
        };
        var keyboard = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData("Crystalpay", $"renew_license_crystalpay_{days}")
            ]
        ]);
        await _botClient.SendMessage(callbackQuery.From.Id,
            $"Вы выбрали продление лицензии на {days} день(я). Выберите способ оплаты:", replyMarkup: keyboard);
    }

    private async Task RenewLicenseCrystalpayCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var days = callbackQuery.Data switch
        {
            "renew_license_crystalpay_1" => 1,
            "renew_license_crystalpay_30" => 30,
            _ => throw new Exception("Неверное количество дней для продления лицензии.")
        };
        var price = days switch
        {
            1 => 2,
            30 => 12,
            _ => throw new Exception("Неверное количество дней для продления лицензии.")
        };
        using var r = await _httpClient.PostAsJsonAsync("https://api.crystalpay.io/v3/invoice/create/", new
        {
            auth_login = _crystalpayLogin,
            auth_secret = _crystalpaySecret,
            amount = price,
            amount_currency = "USDT",
            type = "purchase",
            lifetime = 1000
        });
        var invoice = await r.Content.ReadFromJsonAsync<CrystalpayInvoiceCreateResponse>();
        if (invoice is null || invoice.Error)
        {
            await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Ошибка при создании счета Crystalpay.");
            Logger.Error("Ошибка при создании счета Crystalpay");
            return;
        }

        dbContext.CrystalpayInvoices.Add(new CrystalpayInvoice
        {
            User = user,
            Id = invoice.Id ?? throw new InvalidOperationException(),
            Url = invoice.Url ?? throw new InvalidOperationException(),
            Days = days
        });
        await dbContext.SaveChangesAsync();
        var keyboard = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithUrl("Оплатить", invoice.Url),
                InlineKeyboardButton.WithCallbackData("Проверить оплату", $"check_crystalpay_{invoice.Id}")
            ]
        ]);
        await _botClient.SendMessage(callbackQuery.From.Id,
            $"Счет на оплату успешно создан. Сумма: {price} USDT.",
            replyMarkup: keyboard);
    }

    private async Task CheckCrystalpayCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var invoiceId = callbackQuery.Data?.Replace("check_crystalpay_", string.Empty);
        if (invoiceId is null)
        {
            await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Неверный идентификатор счета.");
            return;
        }

        var crystalpayInvoice = await dbContext.CrystalpayInvoices
            .FirstOrDefaultAsync(x => x.Id == invoiceId && x.User.Id == user.Id);
        if (crystalpayInvoice is null)
        {
            await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Счет не найден или не принадлежит вам.");
            return;
        }

        if (crystalpayInvoice.IsPaid)
        {
            await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Счет уже оплачен.");
            return;
        }

        using var r = await _httpClient.PostAsJsonAsync("https://api.crystalpay.io/v3/invoice/info/", new
        {
            auth_login = _crystalpayLogin,
            auth_secret = _crystalpaySecret,
            id = crystalpayInvoice.Id
        });
        var invoiceInfo = await r.Content.ReadFromJsonAsync<CrystalpayInvoiceInfoResponse>();
        if (invoiceInfo is null || invoiceInfo.Error)
        {
            await _botClient.AnswerCallbackQuery(callbackQuery.Id,
                "Ошибка при получении информации о счете Crystalpay.");
            Logger.Error("Ошибка при получении информации о счете Crystalpay");
            return;
        }

        if (invoiceInfo.State != "payed")
        {
            await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Счет не оплачен.");
            return;
        }

        user.License = user.License < DateTimeOffset.Now
            ? DateTimeOffset.UtcNow.AddDays(crystalpayInvoice.Days)
            : user.License.AddDays(crystalpayInvoice.Days);
        crystalpayInvoice.IsPaid = true;
        await dbContext.SaveChangesAsync();
        await _botClient.SendMessage(callbackQuery.From.Id,
            $"Лицензия успешно продлена на {crystalpayInvoice.Days} день(я).");
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