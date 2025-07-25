using System.Globalization;
using System.Net.Http.Json;
using System.Text;
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
using MessageType = Moahk.Data.Enums.MessageType;
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
        _botClient.OnError += (sender, _) =>
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
            case "⚙️ Настройки":
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
            case "📊 Мой профиль":
                await StatusTextCommand(msg, dbContext, user);
                break;
            case not null when user.Status == Status.WritingPriceRange:
                await PriceRangeSetValue(msg, dbContext, user);
                break;
            case not null when user.Status == Status.WritingProfitPercent:
                await ProfitSetCustomValue(msg, dbContext, user);
                break;
            case not null when user.Status == Status.WritingModelPercent:
                await ModelPercentSetValue(msg, dbContext, user);
                break;
            case not null when user.Status == Status.WritingPromoCode:
                await PromoCodeSetValue(msg, dbContext, user);
                break;
        }
    }

    private async Task OnCommand(string command, string args, Message msg, ApplicationDbContext dbContext,
        Data.Entities.User user, bool isNew)
    {
        switch (command)
        {
            case "/start":
                await StartCommand(msg, args, dbContext, user, isNew);
                break;
            case "/find" when Admins.Contains(msg.From!.Id):
                await AdminFindCommand(msg, args, dbContext, user);
                break;
            case "/set_time" when Admins.Contains(msg.From!.Id):
                await AdminSetTimeCommand(msg, args, dbContext, user);
                break;
            case "/set_referral_balance" when Admins.Contains(msg.From!.Id):
                await AdminSetReferralBalanceCommand(msg, args, dbContext, user);
                break;
            case "/set_referral_percent" when Admins.Contains(msg.From!.Id):
                await AdminSetReferralPercentCommand(msg, args, dbContext, user);
                break;
            case "/set_promo" when Admins.Contains(msg.From!.Id):
                await AdminSetPromoCodeCommand(msg, args, dbContext, user);
                break;
            case "/find_promo" when Admins.Contains(msg.From!.Id):
                await FindPromoCodeCommand(msg, args, dbContext, user);
                break;
            case "/delete_promo" when Admins.Contains(msg.From!.Id):
                await DeletePromoCodeCommand(msg, args, dbContext, user);
                break;
        }
    }

    private async Task StartCommand(Message msg, string args, ApplicationDbContext dbContext, Data.Entities.User user,
        bool isNew = false)
    {
        if (isNew && long.TryParse(args, out var referrerId)) user.ReferrerId = referrerId;
        user.Status = Status.None;
        await dbContext.SaveChangesAsync();
        var (keyboard, msgText) =
            GetMainMenuMessage(user, isNew || user.License is null || user.License < DateTimeOffset.UtcNow);
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
                    "⚙️ Настройки"
                ],
                [
                    "❓ FAQ",
                    "📊 Мой профиль"
                ]
            ];
            msgText = """
                      🎁 Добро пожаловать в Gift_ flipper_Bot!

                      Я помогу вам находить самые выгодные подарки в Telegram маркетплейсах Tonnel и Portals по вашим критериям.

                      🔍 Что я умею:
                      - Постоянно сканирую маркеты и ищу выгодные для покупки предложения
                      - Понимаю "грязный" подарок или нет
                      - Анализирую активность и цены продаж каждого конкретного вида подарка
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
                    "⚙️ Настройки",
                    "💳 Продлить подписку"
                ],
                [
                    "❓ FAQ",
                    "📊 Мой профиль"
                ]
            ];
            msgText = $"""
                       🎯 Главное меню

                       Статус подписки: {(user.License is not null && user.License >= DateTimeOffset.UtcNow ? $"✅ Активна до {user.License:yyyy-MM-dd HH:mm} UTC" : "❌ Подписка неактивна")}
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
                                                    Лицензия: {(foundUser.License is not null ? $"{foundUser.License:yyyy-MM-dd HH:mm} UTC" : "Не покупал лицензию")}
                                                    Диапазон цен: {foundUser.PriceMin.ToString("0.##", CultureInfo.InvariantCulture)} - {foundUser.PriceMax.ToString("0.##", CultureInfo.InvariantCulture)}
                                                    Процент прибыли: {foundUser.ProfitPercent}%
                                                    Критерии: {CriteriaToString(foundUser.Criteria)}
                                                    Запущено: {foundUser.IsStarted}
                                                    Баланс рефералов: {foundUser.ReferralBalance.ToString("0.##", CultureInfo.InvariantCulture)} USDT
                                                    Количество рефералов: {await dbContext.Users.CountAsync(x => x.ReferrerId == foundUser.Id)}
                                                    """);
    }

    private async Task AdminSetTimeCommand(Message msg, string args, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
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
        await _botClient.SendMessage(msg.From!.Id,
            $"Лицензия пользователя {foundUser.Id} успешно изменена на {newTime:yyyy-MM-dd HH:mm} UTC.");
    }

    private async Task AdminSetReferralBalanceCommand(Message msg, string args, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var userId) ||
            !double.TryParse(parts[1].Replace(',', '.'), CultureInfo.InvariantCulture, out var newBalance))
        {
            await _botClient.SendMessage(msg.From!.Id,
                "Неверный формат. Используйте: /set_referral_balance <user_id> <new_balance>");
            return;
        }

        var foundUser = await dbContext.Users.FirstOrDefaultAsync(x => x.Id == userId);
        if (foundUser is null)
        {
            await _botClient.SendMessage(msg.From!.Id, "Пользователь не найден.");
            return;
        }

        foundUser.ReferralBalance = newBalance;
        await dbContext.SaveChangesAsync();
        await _botClient.SendMessage(msg.From!.Id,
            $"Баланс рефералов пользователя {foundUser.Id} успешно изменен на {newBalance} USDT.");
    }

    private async Task AdminSetReferralPercentCommand(Message msg, string args, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var userId) ||
            !double.TryParse(parts[1].Replace(',', '.'), CultureInfo.InvariantCulture, out var newPercent))
        {
            await _botClient.SendMessage(msg.From!.Id,
                "Неверный формат. Используйте: /set_referral_percent <user_id> <new_percent>");
            return;
        }

        var foundUser = await dbContext.Users.FirstOrDefaultAsync(x => x.Id == userId);
        if (foundUser is null)
        {
            await _botClient.SendMessage(msg.From!.Id, "Пользователь не найден.");
            return;
        }

        foundUser.ReferralPercent = newPercent;
        await dbContext.SaveChangesAsync();
        await _botClient.SendMessage(msg.From!.Id,
            $"Процент рефералов пользователя {foundUser.Id} успешно изменен на {newPercent}%.");
    }

    private async Task AdminSetPromoCodeCommand(Message msg, string args, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        /*
         * /setpromo <КОД> <%скидки> <макс_использований> <дата_окончания>
         * макс_использований может быть -1 или пустым для неограниченного количества т.е null
         * дата_окончания может быть пустой для неограниченного срока действия
         */
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Length > 4 ||
            !double.TryParse(parts[1].Replace(',', '.'), CultureInfo.InvariantCulture, out var percent) ||
            percent < 0 || percent > 100)
        {
            await _botClient.SendMessage(msg.From!.Id,
                "Неверный формат. Используйте: /setpromo <КОД> <%скидки> [<макс_использований>] [<дата_окончания>]");
            return;
        }

        var code = parts[0];
        var maxUses = parts.Length > 2 && int.TryParse(parts[2], out var uses) ? (int?)uses : null;
        DateTimeOffset date = default;
        DateTimeOffset? expirationDate = null;
        switch (parts.Length)
        {
            case 4 when !DateTimeOffset.TryParse(parts[3], null,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date):
                await _botClient.SendMessage(msg.From!.Id,
                    "Неверный формат даты. Используйте: /setpromo <КОД> <%скидки> [<макс_использований>] <дата_окончания>");
                return;
            case 4:
                expirationDate = date;
                break;
        }

        var existingCode = await dbContext.PromoCodes.FirstOrDefaultAsync(x => x.Code == code);
        if (existingCode is not null)
        {
            await _botClient.SendMessage(msg.From!.Id,
                $"Промокод {code} уже существует. Используйте другой код или удалите существующий.");
            return;
        }

        var promoCode = new PromoCode
        {
            Code = code,
            Percent = percent,
            MaxUses = maxUses is not null && maxUses != -1 ? maxUses : null,
            DateExpiration = expirationDate
        };
        await dbContext.PromoCodes.AddAsync(promoCode);
        await dbContext.SaveChangesAsync();
        await _botClient.SendMessage(msg.From!.Id,
            $"""
             Промокод {code} успешно создан с {percent}% скидкой. 
             {(promoCode.MaxUses.HasValue ? $"Максимальное количество использований: {promoCode.MaxUses.Value}. " : string.Empty)}
             {(expirationDate.HasValue ? $"Дата окончания: {expirationDate.Value:yyyy-MM-dd HH:mm} UTC." : "Без срока действия.")}
             """);
    }

    private async Task FindPromoCodeCommand(Message msg, string args, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await _botClient.SendMessage(msg.From!.Id, "Пожалуйста, введите код промокода.");
            return;
        }

        var promoCode = await dbContext.PromoCodes.FirstOrDefaultAsync(x => x.Code == args);
        if (promoCode is null)
        {
            await _botClient.SendMessage(msg.From!.Id, "Промокод не найден.");
            return;
        }

        var response = $"""
                        Промокод найден:
                        Код: {promoCode.Code}
                        Скидка: {promoCode.Percent}%
                        {(promoCode.MaxUses.HasValue ? $"Макс. использования: {promoCode.MaxUses.Value}" : "Без ограничений")}
                        Использовано: {promoCode.UsedUsersIds.Count}
                        {(promoCode.DateExpiration.HasValue ? $"Дата окончания: {promoCode.DateExpiration.Value:yyyy-MM-dd HH:mm} UTC" : "Без срока действия")}
                        """;
        await _botClient.SendMessage(msg.From!.Id, response);
    }

    private async Task DeletePromoCodeCommand(Message msg, string args, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await _botClient.SendMessage(msg.From!.Id, "Пожалуйста, введите код промокода для удаления.");
            return;
        }

        var promoCode = await dbContext.PromoCodes.FirstOrDefaultAsync(x => x.Code == args);
        if (promoCode is null)
        {
            await _botClient.SendMessage(msg.From!.Id, "Промокод не найден.");
            return;
        }

        await dbContext.Users
            .Where(u => u.PromoCode != null && u.PromoCode.Code == promoCode.Code)
            .ForEachAsync(u => u.PromoCode = null);
        dbContext.PromoCodes.Remove(promoCode);
        await dbContext.SaveChangesAsync();
        await _botClient.SendMessage(msg.From!.Id, $"Промокод {promoCode.Code} успешно удален.");
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
        if (callbackQuery.Message?.Chat.Type != ChatType.Private)
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
            case "model_percent":
                await ModelPercentCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            // case "renew_license_1":
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
            case "status_back":
                await StatusBackCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case "promo_code_input":
                await PromoCodeInputCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case "signal_types":
                await SignalTypesCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case { } data when data.StartsWith("signal_type_"):
                await SignalTypeSetCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case "activities":
                await ActivitiesCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case { } data when data.StartsWith("activity_"):
                await ActivitySetCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case "gift_sale_statuses":
                await GiftSaleStatusesCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case { } data when data.StartsWith("gift_sale_status_"):
                await GiftSaleStatusSetCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case "message_type":
                await MessageTypeCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case "message_type_full":
            case "message_type_compact":
                await MessageTypeSetCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case "percentile":
                await PercentileCallbackQuery(callbackQuery, dbContext, user.user);
                break;
            case "percentile_25":
            case "percentile_75":
            case "percentile_none":
                await PercentileSetCallbackQuery(callbackQuery, dbContext, user.user);
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
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            $"Вы выбрали продление лицензии на {days} {DaysFormat(days)}. Выберите способ оплаты:",
            replyMarkup: keyboard);
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
        double price = days switch
        {
            // 1 => 2,
            30 => 40,
            _ => throw new Exception("Неверное количество дней для продления лицензии.")
        };
        if (user.PromoCode is not null) price -= price * user.PromoCode.Percent / 100;
        using var r = await _httpClient.PostAsJsonAsync("https://api.crystalpay.io/v3/invoice/create/", new
        {
            auth_login = _crystalpayLogin,
            auth_secret = _crystalpaySecret,
            amount = price,
            amount_currency = "USDT",
            type = "purchase",
            lifetime = 1000,
            redirect_url = $"https://t.me/{_me.Username}"
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
            Days = days,
            Amount = price
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

        if (user.ReferrerId is not null && user.License is null)
        {
            var referrer = await dbContext.Users.FirstOrDefaultAsync(x => x.Id == user.ReferrerId);
            if (referrer is not null)
            {
                var referralProfit = crystalpayInvoice.Amount * referrer.ReferralPercent / 100;
                referrer.ReferralBalance += referralProfit;
                try
                {
                    await _botClient.SendMessage(user.ReferrerId.Value,
                        $"""
                         🎉 Ваш реферал оплатил подписку на {crystalpayInvoice.Days} {DaysFormat(crystalpayInvoice.Days)}.
                         Вы получили {referralProfit} USDT на свой баланс.
                         """);
                }
                catch
                {
                    // ignored
                }
            }
        }

        user.License = user.License is null || user.License < DateTimeOffset.Now
            ? DateTimeOffset.UtcNow.AddDays(crystalpayInvoice.Days)
            : user.License!.Value.AddDays(crystalpayInvoice.Days);
        if (user.PromoCode is not null)
        {
            user.PromoCode.UsedUsersIds.Add(user.Id);
            user.PromoCode = null;
        }

        crystalpayInvoice.IsPaid = true;
        await dbContext.SaveChangesAsync();
        var (keyboard, _) = GetMainMenuMessage(user, false);
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            """
            Гайд - https://teletype.in/@retrowaiver/ConvyrGiftFlipper

            Приватка -  https://t.me/+CcNTT5q3T7U1ZTIy
            """, replyMarkup: InlineKeyboardMarkup.Empty());
        await _botClient.SendMessage(callbackQuery.From.Id,
            $"Лицензия успешно продлена на {crystalpayInvoice.Days} {DaysFormat(crystalpayInvoice.Days)}",
            replyMarkup: keyboard);
    }

    private string DaysFormat(int days)
    {
        return days switch
        {
            1 => "день",
            2 or 3 or 4 => "дня",
            _ => "дней"
        };
    }

    private async Task<bool> CheckLicense(Data.Entities.User user)
    {
        if (user.License is not null && user.License >= DateTimeOffset.UtcNow) return false;
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

    private string SignalTypeToString(SignalType signalType)
    {
        return signalType switch
        {
            SignalType.TonnelTonnel => "TONNEL → TONNEL",
            SignalType.TonnelPortals => "TONNEL → PORTALS",
            SignalType.PortalsPortals => "PORTALS → PORTALS",
            SignalType.PortalsTonnel => "PORTALS → TONNEL",
            _ => throw new ArgumentOutOfRangeException(nameof(signalType), signalType, null)
        };
    }

    private string GiftSaleStatusToString(GiftSignatureStatus status)
    {
        return status switch
        {
            GiftSignatureStatus.Clean => "Чистый",
            GiftSignatureStatus.Dirty => "Грязный",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };
    }

    private string ActivityToString(Activity activity)
    {
        return activity switch
        {
            Activity.Low => "Низкая",
            Activity.Medium => "Средняя",
            Activity.High => "Высокая",
            _ => throw new ArgumentOutOfRangeException(nameof(activity), activity, null)
        };
    }

    private string PercentileToString(Percentile percentile)
    {
        return percentile switch
        {
            Percentile.Percentile25 => "25-й процентиль",
            Percentile.Percentile75 => "75-й процентиль",
            Percentile.None => "Отключен",
            _ => throw new ArgumentOutOfRangeException(nameof(percentile), percentile, null)
        };
    }

    private (InlineKeyboardMarkup keyboard, string msgText) GetFiltersMessage(Data.Entities.User user)
    {
        var buttons = new List<List<InlineKeyboardButton>>([
            [
                InlineKeyboardButton.WithCallbackData("💰 Диапазон цен", "price_range"),
                InlineKeyboardButton.WithCallbackData("📈 Выгода", "profit"),
                InlineKeyboardButton.WithCallbackData("🔍 Оценка", "criteria")
            ],
            [
                InlineKeyboardButton.WithCallbackData("🎯 Редкость", "model_percent"),
                InlineKeyboardButton.WithCallbackData("🔢 Процентель", "percentile")
            ],
            [
                InlineKeyboardButton.WithCallbackData("🔗 Связки", "signal_types"),
                InlineKeyboardButton.WithCallbackData("✒️ Подписи", "gift_sale_statuses")
            ],
            [
                InlineKeyboardButton.WithCallbackData("🔥 Активность", "activities"),
                InlineKeyboardButton.WithCallbackData("📜 Формат сообщений", "message_type")
            ]
        ]);
        var msgText = $"""
                       ⚙️ Текущие фильтры:

                       💰 Диапазон цен: {user.PriceMin.ToString("0.##", CultureInfo.InvariantCulture)} - {user.PriceMax.ToString("0.##", CultureInfo.InvariantCulture)} TON
                       📈 Минимальная выгода: {user.ProfitPercent.ToString("0.##", CultureInfo.InvariantCulture)}%
                       🔍 Оценка: {CriteriaToString(user.Criteria)}
                       🎯 Процент редкости: {user.ModelPercentMin.ToString("0.##", CultureInfo.InvariantCulture)}% - {user.ModelPercentMax.ToString("0.##", CultureInfo.InvariantCulture)}%
                       🔢 Процентель: {PercentileToString(user.Percentile)}
                       🔗 Связки: {(user.SignalTypes.Count != 0 ? user.SignalTypes.Count == Enum.GetValues<SignalType>().Length ? "Любые" : string.Join(", ", user.SignalTypes.Select(SignalTypeToString)) : "Не выбраны")}
                       ✒️ Подписи: {(user.GiftSignatureStatus.Count != 0 ? user.GiftSignatureStatus.Count == Enum.GetValues<GiftSignatureStatus>().Length ? "Любые" : string.Join(", ", user.GiftSignatureStatus.Select(GiftSaleStatusToString)) : "Не выбраны")}
                       🔥 Активность: {(user.Activities.Count != 0 ? user.Activities.Count == Enum.GetValues<Activity>().Length ? "Любая" : string.Join(", ", user.Activities.Select(ActivityToString)) : "Не выбрана")}
                       📜 Формат сообщений: {MessageTypeToString(user.MessageType)}

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

    private async Task PercentileCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        var buttons = new List<List<InlineKeyboardButton>>([
            [
                InlineKeyboardButton.WithCallbackData(PercentileToString(Percentile.Percentile25), "percentile_25"),
                InlineKeyboardButton.WithCallbackData(PercentileToString(Percentile.Percentile75), "percentile_75")
            ],
            [
                InlineKeyboardButton.WithCallbackData(PercentileToString(Percentile.None), "percentile_none")
            ],
            [
                InlineKeyboardButton.WithCallbackData("◀️ Назад к фильтрам", "filters_back")
            ]
        ]);
        var keyboard = new InlineKeyboardMarkup(buttons);
        var msgText = $"""
                       📊 Процентель:

                       Если цена подарка сравнивается с  25-м процентилем, это означает, что примерно 25% всех проданных за 3 дня подобных подарков имеют цену ниже или равную этой цене, а остальные 75% — дороже.

                       Если цена подарка сравнивается с  75-м процентилем, значит, что 75% товаров проданных за 3 дня стоят ниже этой цены, и только 25% дороже.

                       Текущий процентель: {PercentileToString(user.Percentile)}

                       Выберите процентель, который вы хотите использовать:
                       """;
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            msgText, replyMarkup: keyboard);
    }

    private async Task PercentileSetCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        var percentile = callbackQuery.Data switch
        {
            "percentile_25" => Percentile.Percentile25,
            "percentile_75" => Percentile.Percentile75,
            "percentile_none" => Percentile.None,
            _ => throw new Exception("Неверный процентель.")
        };
        user.Percentile = percentile;
        await dbContext.SaveChangesAsync();
        await FiltersBackCallbackQuery(callbackQuery, dbContext, user);
    }

    private async Task MessageTypeCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        var buttons = new List<List<InlineKeyboardButton>>([
            [
                InlineKeyboardButton.WithCallbackData(MessageTypeToString(MessageType.Full), "message_type_full"),
                InlineKeyboardButton.WithCallbackData(MessageTypeToString(MessageType.Compact), "message_type_compact")
            ],
            [
                InlineKeyboardButton.WithCallbackData("◀️ Назад к фильтрам", "filters_back")
            ]
        ]);
        var keyboard = new InlineKeyboardMarkup(buttons);
        var msgText = $"""
                       📜 Формат сообщений:

                       Текущий формат: {MessageTypeToString(user.MessageType)}

                       Выберите формат сообщений, который вы хотите использовать:
                       """;
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            msgText, replyMarkup: keyboard);
    }

    private static string MessageTypeToString(MessageType messageType)
    {
        return messageType switch
        {
            MessageType.Full => "Полный",
            MessageType.Compact => "Компактный",
            _ => "Неизвестный"
        };
    }

    private async Task MessageTypeSetCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        var messageType = callbackQuery.Data switch
        {
            "message_type_full" => MessageType.Full,
            "message_type_compact" => MessageType.Compact,
            _ => throw new Exception("Неверный формат сообщений.")
        };
        user.MessageType = messageType;
        await dbContext.SaveChangesAsync();
        await FiltersBackCallbackQuery(callbackQuery, dbContext, user);
    }

    private async Task SignalTypesCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        var buttons = Enum.GetValues<SignalType>().Select(x =>
                InlineKeyboardButton.WithCallbackData(
                    (user.SignalTypes.Contains(x) ? "✅" : "❌") + ' ' + SignalTypeToString(x), $"signal_type_{x}"))
            .Chunk(2).Select(x => x.ToList()).ToList();
        buttons.Add([InlineKeyboardButton.WithCallbackData("◀️ Назад к фильтрам", "filters_back")]);
        var keyboard = new InlineKeyboardMarkup(buttons);
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            "Выбери нужные связки (можно несколько)", replyMarkup: keyboard);
    }

    private async Task SignalTypeSetCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        var signalTypeString = callbackQuery.Data?.Replace("signal_type_", string.Empty);
        if (signalTypeString is null || !Enum.TryParse<SignalType>(signalTypeString, out var signalType))
        {
            await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Неверный тип сигнала.");
            return;
        }

        if (!user.SignalTypes.Remove(signalType)) user.SignalTypes.Add(signalType);
        user.SignalTypes = user.SignalTypes.OrderBy(x => x).ToList();
        await dbContext.SaveChangesAsync();
        await SignalTypesCallbackQuery(callbackQuery, dbContext, user);
    }

    private async Task GiftSaleStatusesCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        var buttons = Enum.GetValues<GiftSignatureStatus>().Select(x =>
                InlineKeyboardButton.WithCallbackData(
                    (user.GiftSignatureStatus.Contains(x) ? "✅" : "❌") + ' ' + GiftSaleStatusToString(x),
                    $"gift_sale_status_{x}"))
            .Chunk(2).Select(x => x.ToList()).ToList();
        buttons.Add([InlineKeyboardButton.WithCallbackData("◀️ Назад к фильтрам", "filters_back")]);
        var keyboard = new InlineKeyboardMarkup(buttons);
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            "Выбери нужные подписи (можно несколько)", replyMarkup: keyboard);
    }

    private async Task GiftSaleStatusSetCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        var giftSaleStatusString = callbackQuery.Data?.Replace("gift_sale_status_", string.Empty);
        if (giftSaleStatusString is null ||
            !Enum.TryParse<GiftSignatureStatus>(giftSaleStatusString, out var giftSaleStatus))
        {
            await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Неверный статус продажи подарка.");
            return;
        }

        if (!user.GiftSignatureStatus.Remove(giftSaleStatus)) user.GiftSignatureStatus.Add(giftSaleStatus);
        user.GiftSignatureStatus = user.GiftSignatureStatus.OrderBy(x => x).ToList();
        await dbContext.SaveChangesAsync();
        await GiftSaleStatusesCallbackQuery(callbackQuery, dbContext, user);
    }

    private async Task ActivitiesCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        var buttons = Enum.GetValues<Activity>().Select(x =>
                InlineKeyboardButton.WithCallbackData(
                    (user.Activities.Contains(x) ? "✅" : "❌") + ' ' + ActivityToString(x), $"activity_{x}"))
            .Chunk(2).Select(x => x.ToList()).ToList();
        buttons.Add([InlineKeyboardButton.WithCallbackData("◀️ Назад к фильтрам", "filters_back")]);
        var keyboard = new InlineKeyboardMarkup(buttons);
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            "Выбери нужную активность (можно несколько)", replyMarkup: keyboard);
    }

    private async Task ActivitySetCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        var activityString = callbackQuery.Data?.Replace("activity_", string.Empty);
        if (activityString is null || !Enum.TryParse<Activity>(activityString, out var activity))
        {
            await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Неверная активность.");
            return;
        }

        if (!user.Activities.Remove(activity)) user.Activities.Add(activity);
        user.Activities = user.Activities.OrderBy(x => x).ToList();
        await dbContext.SaveChangesAsync();
        await ActivitiesCallbackQuery(callbackQuery, dbContext, user);
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

    private async Task ModelPercentCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        if (await CheckLicense(user))
            return;
        user.Status = Status.WritingModelPercent;
        await dbContext.SaveChangesAsync();
        var msgText = $"""
                       🎯 Настройка процента редкости

                       Текущий диапазон: {user.ModelPercentMin}% - {user.ModelPercentMax}%

                       Введите новый диапазон в формате:
                       минимальный_процент максимальный_процент
                       Примеры:
                       - 1 5
                       - 0.5 10
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

    private async Task ModelPercentSetValue(Message msg, ApplicationDbContext dbContext, Data.Entities.User user)
    {
        var parts = msg.Text?.Replace(',', '.').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: 2 } ||
            !double.TryParse(parts[0], CultureInfo.InvariantCulture, out var min) ||
            !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var max) || min < 0 ||
            max < 0 || min >= max)
        {
            await _botClient.SendMessage(msg.From!.Id, """
                                                       ❌ Неверный формат

                                                       Пожалуйста, введите проценты в правильном формате:
                                                       минимальный_процент максимальный_процент
                                                       """);
            return;
        }

        user.ModelPercentMin = min;
        user.ModelPercentMax = max;
        user.Status = Status.None;
        await dbContext.SaveChangesAsync();
        await FiltersTextCommand(msg, dbContext, user);
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

    private async Task PromoCodeInputCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var keyboard = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData("◀️ Назад к профилю", "status_back")
            ]
        ]);
        user.Status = Status.WritingPromoCode;
        await dbContext.SaveChangesAsync();
        var msgText = $"""
                       📢 Введите промокод

                       Текущий промокод: {user.PromoCode?.Code ?? "Не установлен"}

                       Введите новый промокод в формате:
                       промокод
                       Примеры:
                       - MYPROMO123
                       """;
        // await _botClient.SendMessage(callbackQuery.From.Id, msgText, replyMarkup: keyboard);
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            msgText, replyMarkup: keyboard);
    }

    private async Task PromoCodeSetValue(Message msg, ApplicationDbContext dbContext, Data.Entities.User user)
    {
        var promoCodeString = msg.Text?.Trim();
        if (string.IsNullOrWhiteSpace(promoCodeString))
        {
            await _botClient.SendMessage(msg.From!.Id,
                """
                ❌ Неверный формат
                """);
            return;
        }

        var promoCode = await dbContext.PromoCodes
            .FirstOrDefaultAsync(x => x.Code == promoCodeString);
        if (promoCode is null)
        {
            await _botClient.SendMessage(msg.From!.Id,
                """
                ❌ Упс! Промокод неправильный.
                """);
            return;
        }

        if (promoCode.MaxUses is not null && promoCode.MaxUses <= promoCode.UsedUsersIds.Count)
        {
            await _botClient.SendMessage(msg.From!.Id,
                """
                ⚠️ Упс! Промокод исчерпан.
                """);
            return;
        }

        if (promoCode.DateExpiration is not null && promoCode.DateExpiration < DateTimeOffset.UtcNow)
        {
            await _botClient.SendMessage(msg.From!.Id,
                """
                ⚠️ Упс! Промокод просрочен.
                """);
            return;
        }

        if (promoCode.UsedUsersIds.Any(x => x == user.Id))
        {
            await _botClient.SendMessage(msg.From!.Id,
                """
                🔒 Упс! Вы уже использовали этот промокод.
                """);
            return;
        }

        user.Status = Status.None;
        user.PromoCode = promoCode;
        await dbContext.SaveChangesAsync();
        await StatusTextCommand(msg, dbContext, user);
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
                       💰 Диапазон цен: {user.PriceMin.ToString("0.##", CultureInfo.InvariantCulture)} - {user.PriceMax.ToString("0.##", CultureInfo.InvariantCulture)} TON
                       📈 Минимальная выгода: {user.ProfitPercent.ToString("0.##", CultureInfo.InvariantCulture)}%
                       🔍 Оценка: {CriteriaToString(user.Criteria)}
                       🎯 Процент редкости: {user.ModelPercentMin.ToString("0.##", CultureInfo.InvariantCulture)}% - {user.ModelPercentMax.ToString("0.##", CultureInfo.InvariantCulture)}%
                       🔢 Процентель: {PercentileToString(user.Percentile)}
                       🔗 Связки: {(user.SignalTypes.Count != 0 ? user.SignalTypes.Count == Enum.GetValues<SignalType>().Length ? "Любые" : string.Join(", ", user.SignalTypes.Select(SignalTypeToString)) : "Не выбраны")}
                       ✒️ Подписи: {(user.GiftSignatureStatus.Count != 0 ? user.GiftSignatureStatus.Count == Enum.GetValues<GiftSignatureStatus>().Length ? "Любые" : string.Join(", ", user.GiftSignatureStatus.Select(GiftSaleStatusToString)) : "Не выбраны")}
                       🔥 Активность: {(user.Activities.Count != 0 ? user.Activities.Count == Enum.GetValues<Activity>().Length ? "Любая" : string.Join(", ", user.Activities.Select(ActivityToString)) : "Не выбрана")}
                       📜 Формат сообщений: {MessageTypeToString(user.MessageType)}
                       """;
        var (keyboard, _) = GetMainMenuMessage(user, user.License is null || user.License < DateTimeOffset.UtcNow);
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
        var (keyboard, _) = GetMainMenuMessage(user, user.License is null || user.License < DateTimeOffset.UtcNow);
        await _botClient.SendMessage(msg.From!.Id, msgText, replyMarkup: keyboard);
    }

    private async Task FaqTextCommand(Message msg, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var msgText = $"""
                       🔍 *Как работает бот?*
                       Бот постоянно сканирует маркетплейсы Tonnel и Portals, анализирует цены, активность продаж, находит и помечает "грязные" подарки ( подарки с подписью)  и находит самые выгодные связки для арбитража.

                       🔄 *TONNEL → PORTALS*
                       Это направление арбитража предлагаемое ботом:

                       - Подарок найден дешевле на *TONNEL*, а выгоднее продать бот предлагает — на *PORTALS*.

                       💹 *Перспектива: +18.46% (+17.54% с комиссией)*
                       Это потенциальная выгода при перепродаже:

                       - *Первая цифра —* разница между найденным подарком на одном маркетплейсе и вторым (или на другом маркетплейсе).
                           
                       - *Вторая цифра (в скобках) —* та же разница, но уже с учётом комиссии между маркетами

                       - ✅ *Чистый / Грязный —* есть ли подпись. Чистый = без подписи. С подписью обычно ценятся процентов на 20 ниже.
                           
                       - 💲 *Цена —* текущая цена подарка.
                           
                       - 🔥 *Активность —* насколько часто происходят сделки с этим типом подарка:
                           
                           - *Высокая —* 10+ продаж за 3 дня
                               
                           - *Средняя —* 5-10 продаж за 3 дня
                               
                           - *Низкая —* 0-5 продаж за 3 дня
                             
                       - 📉 *( 25% ) —* нижний уровень цен из истории (низкий квартиль).
                           
                       - 📈 *( 75% ) —* верхний уровень цен из истории (высокий квартиль).  

                           Эти цифры помогают понять  диапазон цен по истории продаж за последние 3 дня.
                           
                       📆 *Изменения рынка за 1 и 7 дней:*

                       Общее изменение рынка на этот подарок за указанную дату ( парсится из бота gift bubbles)


                       📊 *История продаж*
                        Показывает последние 10 сделок на обоих маркетах 
                       Это важно: История помогает понять реальную рыночную цену подарка. 

                       ⚙️ *Объяснение фильтров*

                       💰 *Диапазон цен:*
                       Подарки, цена которых попадает в этот диапазон.

                       📈 *Минимальная выгода:*
                       Минимальный процент перспективы, чтобы бот показал подарок. Например, если стоит 10% — всё ниже бот отфильтрует.

                       🔍 *Оценка:*
                       Выбираем - бот будет искать  подарки с учетом фона или без учета фона.

                       🎯 *Процент редкости:*
                       Выбор окна процента редкости модели подарка. 

                       🔗 *Связки:*
                       Можно фильтровать по конкретным направлениям : Portals - Tonnel | Tonnel - Portals | Portals - Portals | Tonnel - Tonnel  а так же выбирать один или несколько вариантов.

                       ✒️ *Подписи:*

                       - *Чистый —* без подписи
                           
                       - *Грязный —* с подписью, чаще всего менее ликвиден и ценится меньше в среднем на 20%

                       🔥 *Активность:*
                       Настройка фильтрации подарков по активности продаж за последние 3 дня.

                       📜 *Формат сообщений:*

                       - *Компактный —* кратко, емко и можно удобно сравнить глазами все характеристики.
                           
                       - *Подробный —* с полными пояснениями.
                       \_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_

                       [Контакты для связи](https://t.me/retrowaiver){(user.License is not null && user.License > DateTimeOffset.UtcNow ? " | [Гайд](https://teletype.in/@retrowaiver/ConvyrGiftFlipper) | [Приватка](https://t.me/+CcNTT5q3T7U1ZTIy)" : string.Empty)} | [Канал](https://t.me/ConvyrTech)| [Чат](https://t.me/Convyr_chat) | [Youtube](https://www.youtube.com/@Convyr)
                       \_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_
                       """;
        await _botClient.SendMessage(msg.From!.Id, msgText, ParseMode.Markdown);
    }

    private async Task<(InlineKeyboardMarkup keyboard, string msgText)> GetStatusMessage(ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var hoursDiff = user.License is not null ? (user.License - DateTimeOffset.UtcNow).Value.TotalHours : -1;
        var msgText = $"""
                       💎 *Подписка:* {(hoursDiff > 0 ? $"✅ Активна до {user.License:yyyy-MM-dd HH:mm} UTC" : "❌ Неактивна")}
                       🔍 *Поиск:* {(user.IsStarted ? "▶️ Запущен" : "⏹️ Остановлен")}
                       {(user.PromoCode is not null ? $"🔖 *Промокод:* {EscapeMarkdown(user.PromoCode.Code)} на {user.PromoCode.Percent.ToString("0.##", CultureInfo.InvariantCulture)}%{(user.PromoCode.DateExpiration is not null ? $" активен до {user.PromoCode.DateExpiration:yyyy-MM-dd HH:mm} UTC" : string.Empty)}" : string.Empty)}

                       ---💰РЕФЕРАЛЬНАЯ ПРОГРАММА--- 
                       📊 *Процент:* {user.ReferralPercent.ToString("0.##", CultureInfo.InvariantCulture)}%
                       👥 *Приглашено:* {await dbContext.Users.CountAsync(x => x.ReferrerId == user.Id)}
                       💵 *Заработано:* {user.ReferralBalance.ToString("0.##", CultureInfo.InvariantCulture)} USDT

                       {(hoursDiff is <= 24 and > 0 ? "⚠️ Подписка скоро истечёт! Продлите её, чтобы не пропустить выгодные предложения." : string.Empty)}
                       """;
        var buttons = new List<List<InlineKeyboardButton>>([
            [
                InlineKeyboardButton.WithCallbackData("💳 Продлить подписку", "renew_license")
            ],
            [
                InlineKeyboardButton.WithCallbackData("🧨 Ввести промокод", "promo_code_input")
            ],
            [
                InlineKeyboardButton.WithCopyText("🔗 Реферальная ссылка",
                    $"https://t.me/{_me.Username}?start={user.Id}")
            ]
        ]);
        var keyboard = new InlineKeyboardMarkup(buttons);
        return (keyboard, msgText);
    }

    private async Task StatusTextCommand(Message msg, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var (keyboard, msgText) = await GetStatusMessage(dbContext, user);
        await _botClient.SendMessage(msg.From!.Id, msgText, replyMarkup: keyboard, parseMode: ParseMode.Markdown);
    }

    private async Task StatusBackCallbackQuery(CallbackQuery callbackQuery, ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        user.Status = Status.None;
        await dbContext.SaveChangesAsync();
        var (keyboard, msgText) = await GetStatusMessage(dbContext, user);
        // await _botClient.SendMessage(callbackQuery.From.Id, msgText, replyMarkup: keyboard, parseMode: ParseMode.Markdown);
        await _botClient.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
            msgText, replyMarkup: keyboard, parseMode: ParseMode.Markdown);
    }

    private static string EscapeMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var charsToEscape = new[]
            { '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };
        return charsToEscape.Aggregate(text, (current, c) => current.Replace(c.ToString(), "\\" + c));
    }

    #region SendSignal

    public async Task SendSignal(Gift gift, Criteria criteria)
    {
        await using var dbContext = new ApplicationDbContext();
        (GiftBase gift, Activity secondFloorActivity) baseGift = (gift.Type switch
        {
            SignalType.TonnelTonnel => (gift.TonnelGift, gift.TonnelGift!.Activity),
            SignalType.TonnelPortals => (gift.TonnelGift, gift.PortalsGift!.Activity),
            SignalType.PortalsTonnel => (gift.PortalsGift!, gift.TonnelGift!.Activity),
            SignalType.PortalsPortals => (gift.PortalsGift!, gift.PortalsGift!.Activity),
            _ => throw new Exception("Unknown gift type.")
        })!;
        var giftSaleStatus =
            baseGift.gift.TelegramGiftInfo.Signature ? GiftSignatureStatus.Dirty : GiftSignatureStatus.Clean;
        var users = await dbContext.Users
            .AsNoTracking()
            .Where(x => x.IsStarted && x.License >= DateTimeOffset.UtcNow && x.Criteria == criteria &&
                        x.PriceMin <= baseGift.gift.Price && x.PriceMax >= baseGift.gift.Price &&
                        x.ProfitPercent <= gift.PercentDiff &&
                        x.ModelPercentMin <= baseGift.gift.TelegramGiftInfo.Model.Item2 &&
                        x.ModelPercentMax >= baseGift.gift.TelegramGiftInfo.Model.Item2 &&
                        x.SignalTypes.Contains((SignalType)gift.Type) &&
                        x.GiftSignatureStatus.Contains(giftSaleStatus) &&
                        x.Activities.Contains(baseGift.secondFloorActivity) &&
                        (x.Percentile == Percentile.None
                         || (x.Percentile == Percentile.Percentile25 &&
                             baseGift.gift.Price <= baseGift.gift.Percentile25)
                         || (x.Percentile == Percentile.Percentile75 &&
                             baseGift.gift.Price <= baseGift.gift.Percentile75)))
            .OrderBy(x => Guid.NewGuid())
            .ToArrayAsync();
        if (users.Length == 0) return;

        var table = BuildSignalTable(gift);
        var (fullKeyboard, fullMessage) = BuildFullSignalMessage(gift, table);
        var compactTable = BuildCompactSignalTable(gift);
        var (compactKeyboard, compactMessage) = BuildCompactSignalMessage(gift, compactTable);
        foreach (var groupUsers in new[]
                 {
                     (users.Where(x => x.MessageType == MessageType.Full), MessageType.Full),
                     (users.Where(x => x.MessageType == MessageType.Compact), MessageType.Compact)
                 })
        {
            InlineKeyboardMarkup keyboard;
            string message;
            if (groupUsers.Item2 == MessageType.Full)
            {
                keyboard = fullKeyboard;
                message = fullMessage;
            }
            else
            {
                keyboard = compactKeyboard;
                message = compactMessage;
            }

            foreach (var user in groupUsers.Item1)
                try
                {
                    await _botClient.SendMessage(user.Id, message, replyMarkup: keyboard,
                        parseMode: ParseMode.Markdown);
                }
                catch
                {
                    // ignored
                }
        }
    }

    private (InlineKeyboardMarkup keyboard, string message) BuildFullSignalMessage(Gift gift, string table)
    {
        switch (gift.Type)
        {
            case SignalType.TonnelTonnel:
            {
                return (new InlineKeyboardMarkup([
                    gift.PortalsGift is not null
                        ?
                        [
                            InlineKeyboardButton.WithUrl("TONNEL", gift.TonnelGift!.BotUrl),
                            InlineKeyboardButton.WithUrl("PORTALS", gift.PortalsGift.BotUrl)
                        ]
                        :
                        [
                            InlineKeyboardButton.WithUrl("TONNEL", gift.TonnelGift!.BotUrl)
                        ],
                    [InlineKeyboardButton.WithUrl("Сайт", gift.TonnelGift.SiteUrl)]
                ]), $"""
                     [🎁](https://t.me/nft/{gift.TonnelGift!.TelegramGiftId}) *{gift.TonnelGift.Name} | {gift.TonnelGift.TelegramGiftInfo.Model.Item1} ({gift.TonnelGift.TelegramGiftInfo.Model.Item2.ToString("0.#", CultureInfo.InvariantCulture)}%) | {gift.TonnelGift.TelegramGiftInfo.Backdrop.Item1} ({gift.TonnelGift.TelegramGiftInfo.Backdrop.Item2.ToString("0.#", CultureInfo.InvariantCulture)}%)*
                     🔄 *{SignalTypeToString((SignalType)gift.Type)}*

                     💹 *Перспектива:* {gift.PercentDiff.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture)}%

                     --- TONNEL --- 
                     {(gift.TonnelGift.TelegramGiftInfo.Signature ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}
                     💲 *Текущая цена:* {gift.TonnelGift.Price.ToString("0.##", CultureInfo.InvariantCulture)} TON
                     🔥 *Активность:* {ActivityToString(gift.TonnelGift.Activity)}
                     📉 *Нижний уровень цен (25%):* {(gift.TonnelGift.Percentile25 is not null ? $"{gift.TonnelGift.Percentile25.Value.ToString("0.##", CultureInfo.InvariantCulture)} TON" : "Недостаточно данных")}
                     📈 *Высокий уровень цен (75%):* {(gift.TonnelGift.Percentile75 is not null ? $"{gift.TonnelGift.Percentile75.Value.ToString("0.##", CultureInfo.InvariantCulture)} TON" : "Недостаточно данных")}

                     --- [TONNEL (2 флор)]({gift.TonnelGift.SecondFloorGift!.BotUrl}) --- 
                     {(gift.TonnelGift.SecondFloorGift!.TelegramGiftInfo.Signature ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}
                     💲 *Текущая цена:* {gift.TonnelGift.SecondFloorGift!.Price.ToString("0.##", CultureInfo.InvariantCulture)} TON

                     Изменения рынка:
                     📆 1 день: {(gift.TonnelGift.BubblesDataGift is not null ? gift.TonnelGift.BubblesDataGift.Change!.Value.ToString("+0.##;-0.##;0") + '%' : "Недостаточно данных")}
                     📆 7 дней: {(gift.TonnelGift.BubblesDataGift is not null ? gift.TonnelGift.BubblesDataGift.Change7d!.Value.ToString("+0.##;-0.##;0") + '%' : "Недостаточно данных")}

                     📊История продаж:
                     ```
                     {table}
                     ```
                     """);
            }
            case SignalType.TonnelPortals:
            {
                return (new InlineKeyboardMarkup([
                        [
                            InlineKeyboardButton.WithUrl("TONNEL", gift.TonnelGift!.BotUrl),
                            InlineKeyboardButton.WithUrl("PORTALS", gift.PortalsGift!.BotUrl)
                        ],
                        [InlineKeyboardButton.WithUrl("Сайт", gift.TonnelGift.SiteUrl)]
                    ]),
                    $"""
                     [🎁](https://t.me/nft/{gift.TonnelGift!.TelegramGiftId}) *{gift.TonnelGift.Name} | {gift.TonnelGift.TelegramGiftInfo.Model.Item1} ({gift.TonnelGift.TelegramGiftInfo.Model.Item2.ToString("0.#", CultureInfo.InvariantCulture)}%) | {gift.TonnelGift.TelegramGiftInfo.Backdrop.Item1} ({gift.TonnelGift.TelegramGiftInfo.Backdrop.Item2.ToString("0.#", CultureInfo.InvariantCulture)}%)*
                     🔄 *{SignalTypeToString((SignalType)gift.Type)}*

                     💹 *Перспектива:* {gift.PercentDiff.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture)}%
                     💹 *Перспектива с комиссией:* {gift.PercentDiffWithCommission?.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture)}%

                     --- TONNEL --- 
                     {(gift.TonnelGift.TelegramGiftInfo.Signature ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}
                     💲 *Текущая цена:* {gift.TonnelGift.Price.ToString("0.##", CultureInfo.InvariantCulture)} TON
                     🔥 *Активность:* {ActivityToString(gift.TonnelGift.Activity)}
                     📉 *Нижний уровень цен (25%):* {(gift.TonnelGift.Percentile25 is not null ? $"{gift.TonnelGift.Percentile25.Value.ToString("0.##", CultureInfo.InvariantCulture)} TON" : "Недостаточно данных")}
                     📈 *Высокий уровень цен (75%):* {(gift.TonnelGift.Percentile75 is not null ? $"{gift.TonnelGift.Percentile75.Value.ToString("0.##", CultureInfo.InvariantCulture)} TON" : "Недостаточно данных")}

                     --- [PORTALS (2 флор)]({gift.PortalsGift!.BotUrl}) --- 
                     {(gift.PortalsGift.TelegramGiftInfo.Signature ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}
                     💲 *Текущая цена:* {gift.PortalsGift.Price.ToString("0.##", CultureInfo.InvariantCulture)} TON
                     🔥 *Активность:* {ActivityToString(gift.PortalsGift.Activity)}
                     📉 *Нижний уровень цен (25%):* {(gift.PortalsGift.Percentile25 is not null ? $"{gift.PortalsGift.Percentile25.Value.ToString("0.##", CultureInfo.InvariantCulture)} TON" : "Недостаточно данных")}
                     📈 *Высокий уровень цен (75%):* {(gift.PortalsGift.Percentile75 is not null ? $"{gift.PortalsGift.Percentile75.Value.ToString("0.##", CultureInfo.InvariantCulture)} TON" : "Недостаточно данных")}

                     Изменения рынка:
                     📆 1 день: {(gift.TonnelGift.BubblesDataGift is not null ? gift.TonnelGift.BubblesDataGift.Change!.Value.ToString("+0.##;-0.##;0") + '%' : "Недостаточно данных")}
                     📆 7 дней: {(gift.TonnelGift.BubblesDataGift is not null ? gift.TonnelGift.BubblesDataGift.Change7d!.Value.ToString("+0.##;-0.##;0") + '%' : "Недостаточно данных")}

                     📊История продаж:
                     ```
                     {table}
                     ```
                     """);
            }
            case SignalType.PortalsPortals:
            {
                return (new InlineKeyboardMarkup([
                    gift.TonnelGift is not null
                        ?
                        [
                            InlineKeyboardButton.WithUrl("PORTALS", gift.PortalsGift!.BotUrl),
                            InlineKeyboardButton.WithUrl("TONNEL", gift.TonnelGift.BotUrl)
                        ]
                        :
                        [
                            InlineKeyboardButton.WithUrl("PORTALS", gift.PortalsGift!.BotUrl)
                        ]
                ]), $"""
                     [🎁](https://t.me/nft/{gift.PortalsGift!.TelegramGiftId}) *{gift.PortalsGift.Name} | {gift.PortalsGift.TelegramGiftInfo.Model.Item1} ({gift.PortalsGift.TelegramGiftInfo.Model.Item2.ToString("0.#", CultureInfo.InvariantCulture)}%) | {gift.PortalsGift.TelegramGiftInfo.Backdrop.Item1} ({gift.PortalsGift.TelegramGiftInfo.Backdrop.Item2.ToString("0.#", CultureInfo.InvariantCulture)}%)*
                     🔄 *{SignalTypeToString((SignalType)gift.Type)}*

                     💹 *Перспектива:* {gift.PercentDiff.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture)}%

                     --- PORTALS --- 
                     {(gift.PortalsGift.TelegramGiftInfo.Signature ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}
                     💲 *Текущая цена:* {gift.PortalsGift.Price.ToString("0.##", CultureInfo.InvariantCulture)} TON
                     🔥 *Активность:* {ActivityToString(gift.PortalsGift.Activity)}
                     📉 *Нижний уровень цен (25%):* {(gift.PortalsGift.Percentile25 is not null ? $"{gift.PortalsGift.Percentile25.Value.ToString("0.##", CultureInfo.InvariantCulture)} TON" : "Недостаточно данных")}
                     📈 *Высокий уровень цен (75%):* {(gift.PortalsGift.Percentile75 is not null ? $"{gift.PortalsGift.Percentile75.Value.ToString("0.##", CultureInfo.InvariantCulture)} TON" : "Недостаточно данных")}

                     --- [PORTALS (2 флор)]({gift.PortalsGift.SecondFloorGift!.BotUrl}) --- 
                     {(gift.PortalsGift.SecondFloorGift!.TelegramGiftInfo.Signature ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}
                     💲 *Текущая цена:* {gift.PortalsGift.SecondFloorGift!.Price.ToString("0.##", CultureInfo.InvariantCulture)} TON

                     Изменения рынка:
                     📆 1 день: {(gift.PortalsGift.BubblesDataGift is not null ? gift.PortalsGift.BubblesDataGift.Change!.Value.ToString("+0.##;-0.##;0") + '%' : "Недостаточно данных")}
                     📆 7 дней: {(gift.PortalsGift.BubblesDataGift is not null ? gift.PortalsGift.BubblesDataGift.Change7d!.Value.ToString("+0.##;-0.##;0") + '%' : "Недостаточно данных")}

                     📊История продаж:
                     ```
                     {table}
                     ```
                     """);
            }
            case SignalType.PortalsTonnel:
            {
                return (new InlineKeyboardMarkup([
                    [
                        InlineKeyboardButton.WithUrl("PORTALS", gift.PortalsGift!.BotUrl),
                        InlineKeyboardButton.WithUrl("TONNEL", gift.TonnelGift!.BotUrl)
                    ]
                ]), $"""
                     [🎁](https://t.me/nft/{gift.PortalsGift!.TelegramGiftId}) *{gift.PortalsGift.Name} | {gift.PortalsGift.TelegramGiftInfo.Model.Item1} ({gift.PortalsGift.TelegramGiftInfo.Model.Item2.ToString("0.#", CultureInfo.InvariantCulture)}%) | {gift.PortalsGift.TelegramGiftInfo.Backdrop.Item1} ({gift.PortalsGift.TelegramGiftInfo.Backdrop.Item2.ToString("0.#", CultureInfo.InvariantCulture)}%)*
                     🔄 *{SignalTypeToString((SignalType)gift.Type)}*

                     💹 *Перспектива:* {gift.PercentDiff.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture)}%
                     💹 *Перспектива с комиссией:* {gift.PercentDiffWithCommission?.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture)}%

                     --- PORTALS --- 
                     {(gift.PortalsGift.TelegramGiftInfo.Signature ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}
                     💲 *Текущая цена:* {gift.PortalsGift.Price.ToString("0.##", CultureInfo.InvariantCulture)} TON
                     🔥 *Активность:* {ActivityToString(gift.PortalsGift.Activity)}
                     📉 *Нижний уровень цен (25%):* {(gift.PortalsGift.Percentile25 is not null ? $"{gift.PortalsGift.Percentile25.Value.ToString("0.##", CultureInfo.InvariantCulture)} TON" : "Недостаточно данных")}
                     📈 *Высокий уровень цен (75%):* {(gift.PortalsGift.Percentile75 is not null ? $"{gift.PortalsGift.Percentile75.Value.ToString("0.##", CultureInfo.InvariantCulture)} TON" : "Недостаточно данных")}

                     --- [TONNEL (2 флор)]({gift.TonnelGift!.BotUrl}) --- 
                     {(gift.TonnelGift.TelegramGiftInfo.Signature ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}
                     💲 *Текущая цена:* {gift.TonnelGift.Price.ToString("0.##", CultureInfo.InvariantCulture)} TON
                     🔥 *Активность:* {ActivityToString(gift.TonnelGift.Activity)}
                     📉 *Нижний уровень цен (25%):* {(gift.TonnelGift.Percentile25 is not null ? $"{gift.TonnelGift.Percentile25.Value.ToString("0.##", CultureInfo.InvariantCulture)} TON" : "Недостаточно данных")}
                     📈 *Высокий уровень цен (75%):* {(gift.TonnelGift.Percentile75 is not null ? $"{gift.TonnelGift.Percentile75.Value.ToString("0.##", CultureInfo.InvariantCulture)} TON" : "Недостаточно данных")}

                     Изменения рынка:
                     📆 1 день: {(gift.PortalsGift.BubblesDataGift is not null ? gift.PortalsGift.BubblesDataGift.Change!.Value.ToString("+0.##;-0.##;0") + '%' : "Недостаточно данных")}
                     📆 7 дней: {(gift.PortalsGift.BubblesDataGift is not null ? gift.PortalsGift.BubblesDataGift.Change7d!.Value.ToString("+0.##;-0.##;0") + '%' : "Недостаточно данных")}

                     📊История продаж:
                     ```
                     {table}
                     ```
                     """);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(gift.Type), gift.Type, "Unknown gift type.");
        }
    }

    private (InlineKeyboardMarkup keyboard, string message) BuildCompactSignalMessage(Gift gift, string? table)
    {
        switch (gift.Type)
        {
            case SignalType.TonnelTonnel:
                return (new InlineKeyboardMarkup([
                    gift.PortalsGift is not null
                        ?
                        [
                            InlineKeyboardButton.WithUrl("TONNEL", gift.TonnelGift!.BotUrl),
                            InlineKeyboardButton.WithUrl("PORTALS", gift.PortalsGift.BotUrl)
                        ]
                        :
                        [
                            InlineKeyboardButton.WithUrl("TONNEL", gift.TonnelGift!.BotUrl)
                        ],
                    [InlineKeyboardButton.WithUrl("Сайт", gift.TonnelGift.SiteUrl)]
                ]), $"""
                     [🎁](https://t.me/nft/{gift.TonnelGift!.TelegramGiftId}) *{gift.TonnelGift.Name} | {gift.TonnelGift.TelegramGiftInfo.Model.Item1} ({gift.TonnelGift.TelegramGiftInfo.Model.Item2.ToString("0.#", CultureInfo.InvariantCulture)}%) | {gift.TonnelGift.TelegramGiftInfo.Backdrop.Item1} ({gift.TonnelGift.TelegramGiftInfo.Backdrop.Item2.ToString("0.#", CultureInfo.InvariantCulture)}%)*
                     🔄 *{SignalTypeToString((SignalType)gift.Type)}*
                     💹 *Перспектива:* +{gift.PercentDiff.ToString("0.##", CultureInfo.InvariantCulture)}%

                     TONNEL
                     {(gift.TonnelGift.TelegramGiftInfo.Signature ? "❌ Грязный" : "✅ Чистый")} | 💲 {gift.TonnelGift.Price.ToString("0.##", CultureInfo.InvariantCulture)} TON | 🔥 {ActivityToString(gift.TonnelGift.Activity)}{(gift.TonnelGift.Percentile25 is null ? string.Empty : "\n📉 " + gift.TonnelGift.Percentile25?.ToString("0.##", CultureInfo.InvariantCulture))}{(gift.TonnelGift.Percentile75 is null ? string.Empty : " | 📈 " + gift.TonnelGift.Percentile75.Value.ToString("0.##", CultureInfo.InvariantCulture))}

                     [Второй флор]({gift.TonnelGift.SecondFloorGift!.BotUrl})
                     {(gift.TonnelGift.SecondFloorGift!.TelegramGiftInfo.Signature ? "❌ Грязный" : "✅ Чистый")} | 💲 {gift.TonnelGift.SecondFloorGift!.Price.ToString("0.##", CultureInfo.InvariantCulture)} TON
                     {(gift.TonnelGift.BubblesDataGift is null ? string.Empty : $"""

                          Изменения рынка:
                          📆 1 день: {gift.TonnelGift.BubblesDataGift.Change!.Value.ToString("+0.##;-0.##;0") + '%'}
                          📆 7 дней: {gift.TonnelGift.BubblesDataGift.Change7d!.Value.ToString("+0.##;-0.##;0") + '%'}
                          """)}
                     {(table is null ? string.Empty : $"""

                                                       📊История продаж:
                                                       ```
                                                       {table}
                                                       ```
                                                       """)}
                     """);

            case SignalType.TonnelPortals:
                return (new InlineKeyboardMarkup([
                        [
                            InlineKeyboardButton.WithUrl("TONNEL", gift.TonnelGift!.BotUrl),
                            InlineKeyboardButton.WithUrl("PORTALS", gift.PortalsGift!.BotUrl)
                        ],
                        [InlineKeyboardButton.WithUrl("Сайт", gift.TonnelGift.SiteUrl)]
                    ]),
                    $"""
                     [🎁](https://t.me/nft/{gift.TonnelGift!.TelegramGiftId}) *{gift.TonnelGift.Name} | {gift.TonnelGift.TelegramGiftInfo.Model.Item1} ({gift.TonnelGift.TelegramGiftInfo.Model.Item2.ToString("0.#", CultureInfo.InvariantCulture)}%) | {gift.TonnelGift.TelegramGiftInfo.Backdrop.Item1} ({gift.TonnelGift.TelegramGiftInfo.Backdrop.Item2.ToString("0.#", CultureInfo.InvariantCulture)}%)*
                     🔄 *{SignalTypeToString((SignalType)gift.Type)}*
                     💹 *Перспектива:* +{gift.PercentDiff.ToString("0.##", CultureInfo.InvariantCulture)}% ({(gift.PercentDiffWithCommission > 0 ? $"+{gift.PercentDiffWithCommission.Value.ToString("0.##", CultureInfo.InvariantCulture)}" : $"{gift.PercentDiffWithCommission!.Value.ToString("0.##", CultureInfo.InvariantCulture)}")}% с комиссией)

                     TONNEL
                     {(gift.TonnelGift.TelegramGiftInfo.Signature ? "❌ Грязный" : "✅ Чистый")} | 💲 {gift.TonnelGift.Price.ToString("0.##", CultureInfo.InvariantCulture)} TON | 🔥 {ActivityToString(gift.TonnelGift.Activity)}{(gift.TonnelGift.Percentile25 is null ? string.Empty : "\n📉 " + gift.TonnelGift.Percentile25?.ToString("0.##", CultureInfo.InvariantCulture))}{(gift.TonnelGift.Percentile75 is null ? string.Empty : " | 📈 " + gift.TonnelGift.Percentile75.Value.ToString("0.##", CultureInfo.InvariantCulture))}

                     [PORTALS (Второй флор)]({gift.PortalsGift!.BotUrl})
                     {(gift.PortalsGift!.TelegramGiftInfo.Signature ? "❌ Грязный" : "✅ Чистый")} | 💲 {gift.PortalsGift.Price.ToString("0.##", CultureInfo.InvariantCulture)} TON | 🔥 {ActivityToString(gift.PortalsGift.Activity)}{(gift.PortalsGift.Percentile25 is null ? string.Empty : "\n📉 " + gift.PortalsGift.Percentile25?.ToString("0.##", CultureInfo.InvariantCulture))}{(gift.PortalsGift.Percentile75 is null ? string.Empty : " | 📈 " + gift.PortalsGift.Percentile75.Value.ToString("0.##", CultureInfo.InvariantCulture))}
                     {(gift.TonnelGift.BubblesDataGift is null ? string.Empty : $"""

                          Изменения рынка:
                          📆 1 день: {gift.TonnelGift.BubblesDataGift.Change!.Value.ToString("+0.##;-0.##;0") + '%'}
                          📆 7 дней: {gift.TonnelGift.BubblesDataGift.Change7d!.Value.ToString("+0.##;-0.##;0") + '%'}
                          """)}
                     {(table is null ? string.Empty : $"""

                                                       📊История продаж:
                                                       ```
                                                       {table}
                                                       ```
                                                       """)}
                     """);
            case SignalType.PortalsPortals:
                return (new InlineKeyboardMarkup([
                    gift.TonnelGift is not null
                        ?
                        [
                            InlineKeyboardButton.WithUrl("PORTALS", gift.PortalsGift!.BotUrl),
                            InlineKeyboardButton.WithUrl("TONNEL", gift.TonnelGift.BotUrl)
                        ]
                        :
                        [
                            InlineKeyboardButton.WithUrl("PORTALS", gift.PortalsGift!.BotUrl)
                        ]
                ]), $"""
                     [🎁](https://t.me/nft/{gift.PortalsGift!.TelegramGiftId}) *{gift.PortalsGift.Name} | {gift.PortalsGift.TelegramGiftInfo.Model.Item1} ({gift.PortalsGift.TelegramGiftInfo.Model.Item2.ToString("0.#", CultureInfo.InvariantCulture)}%) | {gift.PortalsGift.TelegramGiftInfo.Backdrop.Item1} ({gift.PortalsGift.TelegramGiftInfo.Backdrop.Item2.ToString("0.#", CultureInfo.InvariantCulture)}%)*
                     🔄 *{SignalTypeToString((SignalType)gift.Type)}*
                     💹 *Перспектива:* +{gift.PercentDiff.ToString("0.##", CultureInfo.InvariantCulture)}%

                     PORTALS
                     {(gift.PortalsGift.TelegramGiftInfo.Signature ? "❌ Грязный" : "✅ Чистый")} | 💲 {gift.PortalsGift.Price.ToString("0.##", CultureInfo.InvariantCulture)} TON | 🔥 {ActivityToString(gift.PortalsGift.Activity)}{(gift.PortalsGift.Percentile25 is null ? string.Empty : "\n📉 " + gift.PortalsGift.Percentile25?.ToString("0.##", CultureInfo.InvariantCulture))}{(gift.PortalsGift.Percentile75 is null ? string.Empty : " | 📈 " + gift.PortalsGift.Percentile75.Value.ToString("0.##", CultureInfo.InvariantCulture))}

                     [Второй флор]({gift.PortalsGift.SecondFloorGift!.BotUrl})
                     {(gift.PortalsGift.SecondFloorGift!.TelegramGiftInfo.Signature ? "❌ Грязный" : "✅ Чистый")} | 💲 {gift.PortalsGift.SecondFloorGift!.Price.ToString("0.##", CultureInfo.InvariantCulture)} TON
                     {(gift.PortalsGift.BubblesDataGift is null ? string.Empty : $"""

                          Изменения рынка:
                          📆 1 день: {gift.PortalsGift.BubblesDataGift.Change!.Value.ToString("+0.##;-0.##;0") + '%'}
                          📆 7 дней: {gift.PortalsGift.BubblesDataGift.Change7d!.Value.ToString("+0.##;-0.##;0") + '%'}
                          """)}
                     {(table is null ? string.Empty : $"""

                                                       📊История продаж:
                                                       ```
                                                       {table}
                                                       ```
                                                       """)}
                     """);
            case SignalType.PortalsTonnel:
                return (new InlineKeyboardMarkup([
                    [
                        InlineKeyboardButton.WithUrl("PORTALS", gift.PortalsGift!.BotUrl),
                        InlineKeyboardButton.WithUrl("TONNEL", gift.TonnelGift!.BotUrl)
                    ]
                ]), $"""
                     [🎁](https://t.me/nft/{gift.PortalsGift!.TelegramGiftId}) *{gift.PortalsGift.Name} | {gift.PortalsGift.TelegramGiftInfo.Model.Item1} ({gift.PortalsGift.TelegramGiftInfo.Model.Item2.ToString("0.#", CultureInfo.InvariantCulture)}%) | {gift.PortalsGift.TelegramGiftInfo.Backdrop.Item1} ({gift.PortalsGift.TelegramGiftInfo.Backdrop.Item2.ToString("0.#", CultureInfo.InvariantCulture)}%)*
                     🔄 *{SignalTypeToString((SignalType)gift.Type)}*
                     💹 *Перспектива:* +{gift.PercentDiff.ToString("0.##", CultureInfo.InvariantCulture)}% ({(gift.PercentDiffWithCommission > 0 ? $"+{gift.PercentDiffWithCommission.Value.ToString("0.##", CultureInfo.InvariantCulture)}" : $"{gift.PercentDiffWithCommission!.Value.ToString("0.##", CultureInfo.InvariantCulture)}")}% с комиссией)

                     PORTALS
                     {(gift.PortalsGift.TelegramGiftInfo.Signature ? "❌ Грязный" : "✅ Чистый")} | 💲 {gift.PortalsGift.Price.ToString("0.##", CultureInfo.InvariantCulture)} TON | 🔥 {ActivityToString(gift.PortalsGift.Activity)}{(gift.PortalsGift.Percentile25 is null ? string.Empty : "\n📉 " + gift.PortalsGift.Percentile25?.ToString("0.##", CultureInfo.InvariantCulture))}{(gift.PortalsGift.Percentile75 is null ? string.Empty : " | 📈 " + gift.PortalsGift.Percentile75.Value.ToString("0.##", CultureInfo.InvariantCulture))}

                     [TONNEL (Второй флор)]({gift.TonnelGift!.BotUrl})
                     {(gift.TonnelGift!.TelegramGiftInfo.Signature ? "❌ Грязный" : "✅ Чистый")} | 💲 {gift.TonnelGift.Price.ToString("0.##", CultureInfo.InvariantCulture)} TON | 🔥 {ActivityToString(gift.TonnelGift.Activity)}{(gift.TonnelGift.Percentile25 is null ? string.Empty : "\n📉 " + gift.TonnelGift.Percentile25?.ToString("0.##", CultureInfo.InvariantCulture))}{(gift.TonnelGift.Percentile75 is null ? string.Empty : " | 📈 " + gift.TonnelGift.Percentile75.Value.ToString("0.##", CultureInfo.InvariantCulture))}
                     {(gift.PortalsGift.BubblesDataGift is null ? string.Empty : $"""

                          Изменения рынка:
                          📆 1 день: {gift.PortalsGift.BubblesDataGift.Change!.Value.ToString("+0.##;-0.##;0") + '%'}
                          📆 7 дней: {gift.PortalsGift.BubblesDataGift.Change7d!.Value.ToString("+0.##;-0.##;0") + '%'}
                          """)}
                     {(table is null ? string.Empty : $"""

                                                       📊История продаж:
                                                       ```
                                                       {table}
                                                       ```
                                                       """)}
                     """);
            default:
                throw new ArgumentOutOfRangeException(nameof(gift.Type), gift.Type, "Unknown gift type.");
        }
    }

    private string BuildSignalTable(Gift gift)
    {
        var tableBuilder = new StringBuilder();
        tableBuilder.AppendLine("TONNEL           PORTALS");
        tableBuilder.AppendLine("Цена   Дата      Цена   Дата");

        for (var i = 0; i < 10; i++)
        {
            var tonnelAction = gift.TonnelGift?.ActivityHistoryAll is not null &&
                               gift.TonnelGift.ActivityHistoryAll.Length > i
                ? gift.TonnelGift.ActivityHistoryAll[i]
                : null;
            var portalsAction = gift.PortalsGift?.ActivityHistoryAll is not null &&
                                gift.PortalsGift.ActivityHistoryAll.Length > i
                ? gift.PortalsGift.ActivityHistoryAll[i]
                : null;

            tableBuilder.AppendLine(
                $"{(tonnelAction?.Price is { } price1 ? price1.ToString("0.##", CultureInfo.InvariantCulture) : "N/A"),-7}{(tonnelAction?.CreatedAt is { } date1 ? date1.ToString("dd.MM") : "N/A"),-10}{(portalsAction?.Price is { } price2 ? price2.ToString("0.##", CultureInfo.InvariantCulture) : "N/A"),-7}{(portalsAction?.CreatedAt is { } date2 ? date2.ToString("dd.MM") : "N/A"),-10}"
            );
        }

        var table = tableBuilder.ToString();
        return table;
    }

    private string? BuildCompactSignalTable(Gift gift)
    {
        if ((gift.TonnelGift?.ActivityHistoryAll is null && gift.PortalsGift?.ActivityHistoryAll is null) ||
            (gift.TonnelGift?.ActivityHistoryAll?.Length == 0 && gift.PortalsGift?.ActivityHistoryAll?.Length == 0))
            return null;
        var tableBuilder = new StringBuilder();
        tableBuilder.AppendLine("TONNEL           PORTALS");
        tableBuilder.AppendLine("Цена   Дата      Цена   Дата");

        for (var i = 0; i < 10; i++)
        {
            var tonnelAction = gift.TonnelGift?.ActivityHistoryAll is not null &&
                               gift.TonnelGift.ActivityHistoryAll.Length > i
                ? gift.TonnelGift.ActivityHistoryAll[i]
                : null;
            var portalsAction = gift.PortalsGift?.ActivityHistoryAll is not null &&
                                gift.PortalsGift.ActivityHistoryAll.Length > i
                ? gift.PortalsGift.ActivityHistoryAll[i]
                : null;
            if (tonnelAction is null && portalsAction is null)
                break;
            tableBuilder.AppendLine(
                $"{(tonnelAction?.Price is { } price1 ? price1.ToString("0.##", CultureInfo.InvariantCulture) : "-"),-7}{(tonnelAction?.CreatedAt is { } date1 ? date1.ToString("dd.MM") : "-"),-10}{(portalsAction?.Price is { } price2 ? price2.ToString("0.##", CultureInfo.InvariantCulture) : "-"),-7}{(portalsAction?.CreatedAt is { } date2 ? date2.ToString("dd.MM") : "-"),-10}"
            );
        }

        var table = tableBuilder.ToString();
        return table;
    }

    #endregion
}