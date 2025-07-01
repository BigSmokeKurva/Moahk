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
using Gift = Moahk.Parser.Gift;
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
        var addUserResult = await dbContext.AddUserAsync(msg.From?.Id ?? throw new Exception("UserId is null"));
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
            await OnCommand(command, text[space..].TrimStart(), msg, dbContext, addUserResult.user,
                addUserResult.isNew);
        }
        else
        {
            await OnTextMessage(msg, dbContext, addUserResult.user);
        }
    }

    private async Task OnTextMessage(Message msg, ApplicationDbContext dbContext, Data.Entities.User user)
    {
        switch (msg.Text)
        {
            case "💳 Продлить подписку":
            case "💳 Выбрать тариф":
                await SelectTariffTextCommand(msg, dbContext, user);
                break;
            case "⚙️ Настройки фильтров":
                await FiltersTextCommand(msg, dbContext, user);
                break;
            case "▶️ Запустить поиск":
                await StartTextCommand(msg, dbContext, user);
                break;
            case "⏹️ Остановить поиск":
                await StopTextCommand(msg, dbContext, user);
                break;
            case "❓ FAQ":
                await FaqTextCommand(msg, dbContext, user);
                break;
            case "📊 Мой статус":
                await StatusTextCommand(msg, dbContext, user);
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
        Data.Entities.User user, bool isNew)
    {
        switch (command)
        {
            case "/start":
                await StartCommand(msg, dbContext, user, isNew = isNew);
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
        bool isNew = false)
    {
        user.Status = Status.None;
        await dbContext.SaveChangesAsync();
        var (keyboard, msgText) = GetMainMenuMessage(user, isNew || user.License < DateTimeOffset.UtcNow);
        await _botClient.SendMessage(msg.From!.Id, msgText, replyMarkup: keyboard);
    }

    private (ReplyKeyboardMarkup keyboard, string msgText) GetMainMenuMessage(Data.Entities.User user, bool isNew)
    {
        List<List<KeyboardButton>> buttons;
        string msgText;
        if (isNew)
        {
            buttons =
            [
                [
                    "💳 Выбрать тариф",
                    "⚙️ Настройки фильтров"
                ],
                [
                    "❓ FAQ",
                    "📊 Мой статус"
                ]
            ];
            msgText = """
                      🎁 Добро пожаловать в Gift_ flipper_Bot!

                      Я помогу вам находить самые выгодные подарки в Telegram маркетплейсах Tonnel и Portals по вашим критериям.

                      🔍 Что я умею:
                      - Постоянно сканирую маркеты и ищу выгодные для покупки предложения
                      - Понимаю "грязный" подарок или нет
                      - Анализирую активность и цены продаж каждого конкретного вида подарка за последние 2 недели
                      - Нахожу подарки с высокой перспективой для перепродажи
                      - Фильтрую по цене и проценту выгоды
                      - Отправляю только те предложения, которые соответствуют вашим настройкам

                      Для начала работы выберите тариф и настройте фильтры под себя!
                      """;
        }
        else
        {
            buttons =
            [
                [
                    user.IsStarted ? "⏹️ Остановить поиск" : "▶️ Запустить поиск"
                ],
                [
                    "⚙️ Настройки фильтров",
                    "💳 Продлить подписку"
                ],
                [
                    "❓ FAQ",
                    "📊 Мой статус"
                ]
            ];
            msgText = $"""
                       🎯 Главное меню

                       Статус подписки: {(user.License >= DateTimeOffset.UtcNow ? $"✅ Активна до {user.License:yyyy-MM-dd HH:mm} UTC" : "❌ Подписка неактивна")}
                       Поиск: {(user.IsStarted ? "▶️ Поиск запущен!" : "⏹️ Остановлен")}
                       """;
        }

        return (new ReplyKeyboardMarkup(buttons) { ResizeKeyboard = true, OneTimeKeyboard = false }, msgText);
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
                                                    Критерии: {CriteriaToString(foundUser.Criteria)}
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
                await RenewLicenseCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case "filters_back":
                await FiltersBackCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case "price_range":
                await PriceRangeCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case "profit":
                await ProfitCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case "profit_set_10":
            case "profit_set_20":
            case "profit_set_30":
                await ProfitSetCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case "profit_set_custom":
                await ProfitSetCustomCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case "criteria":
                await CriteriaCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case "criteria_second_floor":
            case "criteria_second_floor_without_backdrop":
                await CriteriaSetCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case "renew_license_1":
            case "renew_license_30":
                await RenewLicenseDaysCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            // case "renew_license_crystalpay_1":
            case "renew_license_crystalpay_30":
                await RenewLicenseCrystalpayCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case { } data when data.StartsWith("check_crystalpay_"):
                await CheckCrystalpayCallbackQuery(callbackQuery, dbContext, user.user);
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

    // private async Task MainMenuCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
    //     Data.Entities.User user)
    // {
    //     var (keyboard, msgText) = GetMainMenuMessage(user, false);
    //     await _botClient.SendMessage(callbackQuery.From.Id, msgText, replyMarkup: keyboard);
    // }

    private async Task SelectTariffTextCommand(Message msg, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var keyboard = new InlineKeyboardMarkup([
            [
                // InlineKeyboardButton.WithCallbackData("1 день", "renew_license_1"),
                InlineKeyboardButton.WithCallbackData("30 дней", "renew_license_30")
            ]
        ]);
        await _botClient.SendMessage(msg.From!.Id, "Выберите количество дней для продления лицензии:",
            replyMarkup: keyboard);
    }

    private async Task RenewLicenseCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var keyboard = new InlineKeyboardMarkup([
            [
                // InlineKeyboardButton.WithCallbackData("1 день", "renew_license_1"),
                InlineKeyboardButton.WithCallbackData("30 дней", "renew_license_30")
            ]
        ]);
        // await _botClient.SendMessage(callbackQuery.From.Id,
        //     "Выберите количество дней для продления лицензии:", replyMarkup: keyboard);
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            "Выберите количество дней для продления лицензии:", replyMarkup: keyboard);
    }

    private async Task RenewLicenseDaysCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var days = callbackQuery.Data switch
        {
            // "renew_license_1" => 1,
            "renew_license_30" => 30,
            _ => throw new Exception("Неверное количество дней для продления лицензии.")
        };
        var keyboard = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData("Crystalpay", $"renew_license_crystalpay_{days}")
            ]
        ]);
        // await _botClient.SendMessage(callbackQuery.From.Id,
        //     $"Вы выбрали продление лицензии на {days} день(я). Выберите способ оплаты:", replyMarkup: keyboard);
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            $"Вы выбрали продление лицензии на {days} день(я). Выберите способ оплаты:", replyMarkup: keyboard);
    }

    private async Task RenewLicenseCrystalpayCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var days = callbackQuery.Data switch
        {
            // "renew_license_crystalpay_1" => 1,
            "renew_license_crystalpay_30" => 30,
            _ => throw new Exception("Неверное количество дней для продления лицензии.")
        };
        var price = days switch
        {
            // 1 => 2,
            30 => 25,
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
        // await _botClient.SendMessage(callbackQuery.From.Id,
        //     $"Счет на оплату успешно создан. Сумма: {price} USDT.",
        //     replyMarkup: keyboard);
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            $"Счет на оплату успешно создан. Сумма: {price} USDT.", replyMarkup: keyboard);
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
        var (keyboard, _) = GetMainMenuMessage(user, false);
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            """
            Гайд - https://teletype.in/@retrowaiver/ConvyrGiftFlipper

            Приватка -  https://t.me/+CcNTT5q3T7U1ZTIy
            """, replyMarkup: InlineKeyboardMarkup.Empty());
        await _botClient.SendMessage(callbackQuery.From.Id,
            $"Лицензия успешно продлена на {crystalpayInvoice.Days} день(я).", replyMarkup: keyboard);
    }

    private async Task<bool> CheckLicense(Data.Entities.User user)
    {
        if (user.License >= DateTimeOffset.UtcNow) return false;
        await _botClient.SendMessage(user.Id,
            "У вас нет активной лицензии.");
        return true;
    }

    private string CriteriaToString(Criteria criteria)
    {
        return criteria switch
        {
            Criteria.SecondFloor => "Сравнение со вторым по дешевизне таким же подарком в продаже",
            Criteria.SecondFloorWithoutBackdrop =>
                "Сравнение со вторым по дешевизне таким же подарком в продаже без фона",
            _ => string.Empty
        };
    }

    private (InlineKeyboardMarkup keyboard, string msgText) GetFiltersMessage(Data.Entities.User user)
    {
        var buttons = new List<List<InlineKeyboardButton>>([
            [
                InlineKeyboardButton.WithCallbackData("💰 Диапазон цен", "price_range"),
                InlineKeyboardButton.WithCallbackData("📈 Процент выгоды", "profit")
            ],
            [
                InlineKeyboardButton.WithCallbackData("📊 Критерии оценки", "criteria")
            ]
        ]);
        var msgText = $"""
                       ⚙️ Текущие фильтры:

                       💰 Диапазон цен: {user.PriceMin} - {user.PriceMax} TON
                       📈 Минимальная выгода: {user.ProfitPercent}%
                       📊 Критерии оценки: {CriteriaToString(user.Criteria)}

                       Настройте фильтры под свои предпочтения:
                       """;
        return (new InlineKeyboardMarkup(buttons), msgText);
    }

    private async Task FiltersTextCommand(Message msg, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        var (keyboard, msgText) = GetFiltersMessage(user);
        await _botClient.SendMessage(msg.From!.Id, msgText, replyMarkup: keyboard);
    }

    private async Task FiltersBackCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        user.Status = Status.None;
        await dbContext.SaveChangesAsync();
        if (await CheckLicense(user))
            return;
        var (keyboard, msgText) = GetFiltersMessage(user);
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            msgText, replyMarkup: keyboard);
    }

    private async Task PriceRangeCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        user.Status = Status.WritingPriceRange;
        await dbContext.SaveChangesAsync();
        var msgText = $"""
                       💰 Настройка диапазона цен

                       Текущий диапазон: {user.PriceMin} - {user.PriceMax} TON

                       Введите новый диапазон в формате:
                       минимальная_цена максимальная_цена
                       Примеры:
                       - 5 15
                       - 0.5 100
                       - 10 50.5
                       """;
        var buttons = new List<List<InlineKeyboardButton>>([
            [
                InlineKeyboardButton.WithCallbackData("◀️ Назад к фильтрам", "filters_back")
            ]
        ]);
        var keyboard = new InlineKeyboardMarkup(buttons);
        // await _botClient.SendMessage(callbackQuery.From.Id, msgText, replyMarkup: keyboard);
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            msgText, replyMarkup: keyboard);
    }

    private async Task PriceRangeSetValue(Message msg, ApplicationDbContext dbContext, Data.Entities.User user)
    {
        var parts = msg.Text?.Replace(',', '.').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: 2 } ||
            !double.TryParse(parts[0], CultureInfo.InvariantCulture, out var min) ||
            !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var max) || min < 0 ||
            max < 0 || min >= max)
        {
            await _botClient.SendMessage(msg.From!.Id, """
                                                       ❌ Неверный формат

                                                       Пожалуйста, введите цены в правильном формате:
                                                       минимальная_цена максимальная_цена
                                                       """);
            return;
        }

        user.PriceMin = min;
        user.PriceMax = max;
        user.Status = Status.None;
        await dbContext.SaveChangesAsync();
        await FiltersTextCommand(msg, dbContext, user);
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
            ],
            [
                InlineKeyboardButton.WithCallbackData("◀️ Назад к фильтрам", "filters_back")
            ]
        ]);
        var msgText = $"""
                       📈 Настройка минимальной выгоды

                       Текущий процент: {user.ProfitPercent}%

                       Выберите минимальный процент выгоды для показа предложений:
                       """;
        // await _botClient.SendMessage(callbackQuery.From.Id, msgText, replyMarkup: keyboard);
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            msgText, replyMarkup: keyboard);
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
        await FiltersBackCallbackQuery(callbackQuery, dbContext, user);
    }

    private async Task ProfitSetCustomCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        user.Status = Status.WritingProfitPercent;
        await dbContext.SaveChangesAsync();
        var buttons = new List<List<InlineKeyboardButton>>([
            [
                InlineKeyboardButton.WithCallbackData("◀️ Назад к фильтрам", "filters_back")
            ]
        ]);
        var keyboard = new InlineKeyboardMarkup(buttons);
        var msgText = $"""
                       ⚡️Введите процент числом

                       Текущий процент: {user.ProfitPercent}

                       Введите новый процент в формате:
                       процент
                       Примеры:
                       - 55
                       """;
        // await _botClient.SendMessage(callbackQuery.From.Id, msgText, replyMarkup: keyboard);
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            msgText, replyMarkup: keyboard);
    }

    private async Task ProfitSetCustomValue(Message msg, ApplicationDbContext dbContext, Data.Entities.User user)
    {
        if (!int.TryParse(msg.Text, out var percent) || percent < 0)
        {
            await _botClient.SendMessage(msg.From!.Id,
                """
                ❌ Неверный формат

                Пожалуйста, введите процент в правильном формате:
                процент
                """);
            return;
        }

        user.ProfitPercent = percent;
        user.Status = Status.None;
        await dbContext.SaveChangesAsync();
        await FiltersTextCommand(msg, dbContext, user);
    }

    private async Task CriteriaCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;

        var keyboard = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData("🔄 2 флор", "criteria_second_floor"),
                InlineKeyboardButton.WithCallbackData("🔄 2 флор без фона", "criteria_second_floor_without_backdrop")
            ],
            [
                InlineKeyboardButton.WithCallbackData("◀️ Назад к фильтрам", "filters_back")
            ]
        ]);
        var msgText = $"""
                       📊 Критерии оценки выгоды

                       1 - Сравнение со вторым по дешевизне таким же подарком в продаже

                       2 - Сравнение со вторым по дешевизне таким же подарком в продаже без фона

                       Текущий критерий: {CriteriaToString(user.Criteria)}

                       Выберите метод расчёта перспективности:
                       """;
        // await _botClient.SendMessage(callbackQuery.From.Id, msgText, replyMarkup: keyboard);
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            msgText, replyMarkup: keyboard);
    }

    private async Task CriteriaSetCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var criteria = callbackQuery.Data switch
        {
            "criteria_second_floor" => Criteria.SecondFloor,
            "criteria_second_floor_without_backdrop" => Criteria.SecondFloorWithoutBackdrop,
            _ => user.Criteria
        };
        user.Criteria = criteria;
        await dbContext.SaveChangesAsync();
        await FiltersBackCallbackQuery(callbackQuery, dbContext, user);
    }

    private async Task StartTextCommand(Message msg, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        user.IsStarted = true;
        await dbContext.SaveChangesAsync();
        var msgText = $"""
                       ▶️ Поиск запущен!

                       Бот начал сканировать предложения по вашим фильтрам.
                       Как только найдёт подходящие варианты - сразу пришлёт уведомление.

                       Фильтры:
                       💰 Цена: {user.PriceMin} - {user.PriceMax} TON
                       📈 Выгода: от {user.ProfitPercent}%
                       📊 Критерий: {CriteriaToString(user.Criteria)}
                       """;
        var (keyboard, _) = GetMainMenuMessage(user, user.License < DateTimeOffset.UtcNow);
        await _botClient.SendMessage(msg.From!.Id, msgText, replyMarkup: keyboard);
    }

    private async Task StopTextCommand(Message msg, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        user.IsStarted = false;
        await dbContext.SaveChangesAsync();
        var msgText = """
                      ⏹️ Поиск остановлен

                      Бот прекратил сканирование предложений.
                      Для возобновления нажмите "Запустить".
                      """;
        var (keyboard, _) = GetMainMenuMessage(user, user.License < DateTimeOffset.UtcNow);
        await _botClient.SendMessage(msg.From!.Id, msgText, replyMarkup: keyboard);
    }

    private async Task FaqTextCommand(Message msg, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var msgText = $"""
                       🔍 Как работает бот?
                       Бот постоянно сканирует маркетплейсы Tonnel и Portals, анализирует цены, активность продаж, находит и помечает "грязные" подарки ( подарки с подписью)  и находит самые выгодные  подарки .


                       📊 Критерии оценки выгоды

                       1 - Сравнение со вторым по дешевизне таким же подарком в продаже

                       2 - Сравнение со вторым по дешевизне таким же подарком в продаже без фона


                       💰 Как рассчитывается выгода?
                       -Перспектива показывает процентную разницу между первым флором и вторым флором на маркетах. 
                       Проще говоря разницу в цене между самым дешевым подарком и вторым по дешевизне в продаже.

                       ⏰ Как часто приходят уведомления?
                       Уведомления приходят сразу при обнаружении подходящего предложения. 

                       🎁 Какие подарки ищет бот?
                       Все виды Telegram подарков, доступных на маркетплейсах Tonnel и Portals.

                       🩻 Что означает каждая строка в выводе?

                       Найдено на: TONNEL
                       💲 Текущая цена: 15,60 TON
                       💹 Перспектива: +50,00% (разница между 1 и 2м флором)
                       ❌ Состояние: Грязный  (с подписью или без )
                       🔥 Активность: Низкая (насколько часто торгуется подарок)

                       --- АНАЛИЗ РЫНКА (В продаже) ---
                       💰 Второй флор: 31,20 TON (второй самый дешевый подобный подарок)
                       💰 Самый дешевый на PORTALS: 25,00 TON (Флор на соседнем маркете)

                       --- АНАЛИЗ ИСТОРИИ ---
                       📉 Нижний уровень цен (25%): 11,99 TON ( 25ый процентиль из истории продаж)
                       📈 Высокий уровень цен (75%): 11,99 TON ( 75ый процентиль из истории продаж)
                       🚀 Максимальная цена (за 7д.): 11,99 TON ( максимальная цена проданного подарка за последние 7 дней)

                       Анализ истории высчитывается исходя из истории за 7 дней и может не отображать действительность из за быстрых изменений рынка

                       Контакты для связи - https://t.me/retrowaiver

                       {(user.License > DateTimeOffset.UtcNow ? """
                                                                Гайд - https://teletype.in/@retrowaiver/ConvyrGiftFlipper

                                                                Приватка -  https://t.me/+CcNTT5q3T7U1ZTIy
                                                                """ : string.Empty)}
                       """;
        await _botClient.SendMessage(msg.From!.Id, msgText);
    }

    private async Task StatusTextCommand(Message msg, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var hoursDiff = (user.License - DateTimeOffset.UtcNow).TotalHours;
        var msgText = $"""
                       💎 Подписка: {(hoursDiff > 0 ? $"✅ Активна до {user.License:yyyy-MM-dd HH:mm} UTC" : "❌ Неактивна")}
                       🔍 Поиск: {(user.IsStarted ? "▶️ Запущен" : "⏹️ Остановлен")}
                       {(hoursDiff <= 24 ? "⚠️ Подписка скоро истечёт! Продлите её, чтобы не пропустить выгодные предложения." : string.Empty)}
                       """;
        var buttons = new List<List<InlineKeyboardButton>>([
            [
                InlineKeyboardButton.WithCallbackData("💳 Продлить подписку", "renew_license")
            ]
        ]);
        var keyboard = new InlineKeyboardMarkup(buttons);
        await _botClient.SendMessage(msg.From!.Id, msgText, replyMarkup: keyboard);
    }

    public async Task SendSignal(Gift gift, double percentDiff, double secondFloorPrice, double? percentile25,
        double? percentile75, double? lastOneWeekMaxPrice, Criteria criteria)

    {
        await using var dbContext = new ApplicationDbContext();
        var users = await dbContext.Users
            .AsNoTracking()
            .Where(x => x.IsStarted && x.License >= DateTimeOffset.UtcNow && x.Criteria == criteria &&
                        x.PriceMin <= gift.Price && x.PriceMax >= gift.Price && x.ProfitPercent <= percentDiff)
            .ToArrayAsync();
//         var msg = $"""
//                    [🎁]({tgUrl})  *{name} | {model} | {backdrop}* 🎨
//
//                    🔀{botName.ToUpper()}
//
//                    ━━━━━━━━━━━━━━━━━━━ 
//                    💲 *Цена*: {price:F2} TON
//                    💹 *Перспектива*: +{percentDiff:F2}%
//                    {(isSold ? "❌ *Состояние*: Грязный" : "✅ *Состояние*: Чистый")}  
//                    🔥 *Активность*: {activity switch
//                    {
//                        Activity.Low => "Низкая",
//                        Activity.Medium => "Средняя",
//                        _ => "Высокая"
//                    }}
//                    📊 Цена последней сделки: {lastActivityPrice:F2} TON
//                    ━━━━━━━━━━━━━━━━━━━ 
//                    {(alternativePrice is not null ? $"""
//                                                      🔀{(botName switch {
//                                                          "portals" => "tonnel",
//                                                          "tonnel" => "portals",
//                                                          _ => throw new ArgumentOutOfRangeException(nameof(botName), botName, null) }).ToUpper()}
//
//                                                      ━━━━━━━━━━━━━━━━━━━ 
//                                                      💲 *Цена*: {alternativePrice:F2} TON
//                                                      ━━━━━━━━━━━━━━━━━━━ 
//                                                      """ : string.Empty)}
//                    """;
        var telegramUrl = $"https://t.me/nft/{gift.TelegramGiftId}";
        var bot = gift.Bot.ToString().ToUpper();
        var msg = $"""
                   [🎁]({telegramUrl}) *{gift.Name} | {gift.Model} | {gift.Backdrop}* 🎨

                   📍 *Найдено на:* {bot}
                   💲 *Текущая цена:* {gift.Price:F2} TON
                   💹 *Перспектива:* +{percentDiff:F2}%
                   {((bool)gift.IsSold! ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}  
                   🔥 *Активность:* {gift.Activity switch {
                       Activity.Low => "Низкая",
                       Activity.Medium => "Средняя",
                       _ => "Высокая"
                   }}

                   --- АНАЛИЗ РЫНКА (В продаже) ---
                   💰 *Второй флор:* {secondFloorPrice:F2} TON
                   {(gift.AlternativePrice is not null ? $"💰 *Самый дешевый на {gift.AlternativeBot.ToString().ToUpper()}:* {gift.AlternativePrice:F2} TON" : string.Empty)}

                   --- ПРОГНОЗ ПРОДАЖИ (История сделок) ---
                   📉 *Нижний уровень цен (25%):* {(percentile25 is not null ? $"{percentile25:F2} TON" : "Недостаточно данных")}
                   📈 *Высокий уровень цен (75%):* {(percentile75 is not null ? $"{percentile75:F2} TON" : "Недостаточно данных")}
                   🚀 *Максимальная цена (за 7д.):* {(lastOneWeekMaxPrice is not null ? $"{lastOneWeekMaxPrice:F2} TON" : "Недостаточно данных")}
                   """;
        var buttons = new List<List<InlineKeyboardButton>>([
            [
                InlineKeyboardButton.WithUrl("Подарок", telegramUrl),
                InlineKeyboardButton.WithUrl(bot, gift.BotUrl)
            ]
        ]);
        if (gift.SiteUrl != null)
            buttons.Add([InlineKeyboardButton.WithUrl("Сайт", gift.SiteUrl)]);
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