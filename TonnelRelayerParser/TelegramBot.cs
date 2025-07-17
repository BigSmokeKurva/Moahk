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
                    "⚙️ Настройки фильтров"
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
                                                    Диапазон цен: {foundUser.PriceMin:F2} - {foundUser.PriceMax:F2}
                                                    Процент прибыли: {foundUser.ProfitPercent}%
                                                    Критерии: {CriteriaToString(foundUser.Criteria)}
                                                    Запущено: {foundUser.IsStarted}
                                                    Баланс рефералов: {foundUser.ReferralBalance:F2} USDT
                                                    Количество рефералов: {await dbContext.Users.CountAsync(x => x.ReferrerId == foundUser.Id)}
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

    private (InlineKeyboardMarkup keyboard, string msgText) GetFiltersMessage(Data.Entities.User user)
    {
        var buttons = new List<List<InlineKeyboardButton>>([
            [
                InlineKeyboardButton.WithCallbackData("💰 Диапазон цен", "price_range"),
                InlineKeyboardButton.WithCallbackData("📈 Процент выгоды", "profit")
            ],
            [
                InlineKeyboardButton.WithCallbackData("📊 Критерии оценки", "criteria"),
                InlineKeyboardButton.WithCallbackData("🎯 Процент редкости", "model_percent")
            ]
        ]);
        var msgText = $"""
                       ⚙️ Текущие фильтры:

                       💰 Диапазон цен: {user.PriceMin} - {user.PriceMax} TON
                       📈 Минимальная выгода: {user.ProfitPercent}%
                       📊 Критерии оценки: {CriteriaToString(user.Criteria)}
                       🎯 Процент редкости: {user.ModelPercentMin}% - {user.ModelPercentMax}%

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

    public async Task ModelPercentSetValue(Message msg, ApplicationDbContext dbContext, Data.Entities.User user)
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
                       💰 Цена: {user.PriceMin} - {user.PriceMax} TON
                       📈 Выгода: от {user.ProfitPercent}%
                       📊 Критерий: {CriteriaToString(user.Criteria)}
                       🎯 Процент редкости: {user.ModelPercentMin}% - {user.ModelPercentMax}%
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

                       {(user.License is not null && user.License > DateTimeOffset.UtcNow ? """
                           Гайд - https://teletype.in/@retrowaiver/ConvyrGiftFlipper

                           Приватка -  https://t.me/+CcNTT5q3T7U1ZTIy
                           """ : string.Empty)}
                       """;
        await _botClient.SendMessage(msg.From!.Id, msgText);
    }

    private async Task<(InlineKeyboardMarkup keyboard, string msgText)> GetStatusMessage(ApplicationDbContext dbContext,
        Data.Entities.User user)
    {
        var hoursDiff = user.License is not null ? (user.License - DateTimeOffset.UtcNow).Value.TotalHours : -1;
        var msgText = $"""
                       💎 *Подписка:* {(hoursDiff > 0 ? $"✅ Активна до {user.License:yyyy-MM-dd HH:mm} UTC" : "❌ Неактивна")}
                       🔍 *Поиск:* {(user.IsStarted ? "▶️ Запущен" : "⏹️ Остановлен")}
                       {(user.PromoCode is not null ? $"🔖 *Промокод:* {EscapeMarkdown(user.PromoCode.Code)} на {user.PromoCode.Percent:F2}%{(user.PromoCode.DateExpiration is not null ? $" активен до {user.PromoCode.DateExpiration:yyyy-MM-dd HH:mm} UTC" : string.Empty)}" : string.Empty)}

                       ---💰РЕФЕРАЛЬНАЯ ПРОГРАММА--- 
                       📊 *Процент:* {user.ReferralPercent:F2}%
                       👥 *Приглашено:* {await dbContext.Users.CountAsync(x => x.ReferrerId == user.Id)}
                       💵 *Заработано:* {user.ReferralBalance:F2} USDT

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

    public async Task SendSignal(Gift gift, Criteria criteria)

    {
        await using var dbContext = new ApplicationDbContext();
        Data.Entities.User[] users;
        switch (gift.Type)
        {
            case SignalType.TonnelTonnel or SignalType.TonnelPortals:
                users = await dbContext.Users
                    .AsNoTracking()
                    .Where(x => x.IsStarted && x.License >= DateTimeOffset.UtcNow && x.Criteria == criteria &&
                                x.PriceMin <= gift.TonnelGift!.Price && x.PriceMax >= gift.TonnelGift.Price &&
                                x.ProfitPercent <= gift.PercentDiff &&
                                x.ModelPercentMin <= gift.TonnelGift.TelegramGiftInfo.Model.Item2 &&
                                x.ModelPercentMax >= gift.TonnelGift.TelegramGiftInfo.Model.Item2)
                    .ToArrayAsync();
                break;
            case SignalType.PortalsPortals or SignalType.PortalsTonnel:
                users = await dbContext.Users
                    .AsNoTracking()
                    .Where(x => x.IsStarted && x.License >= DateTimeOffset.UtcNow && x.Criteria == criteria &&
                                x.PriceMin <= gift.PortalsGift!.Price && x.PriceMax >= gift.PortalsGift.Price &&
                                x.ProfitPercent <= gift.PercentDiff &&
                                x.ModelPercentMin <= gift.PortalsGift.TelegramGiftInfo.Model.Item2 &&
                                x.ModelPercentMax >= gift.PortalsGift.TelegramGiftInfo.Model.Item2)
                    .ToArrayAsync();
                break;
            default:
                return;
        }

        string msg;
        InlineKeyboardButton[][] buttons;
        switch (gift.Type)
        {
            case SignalType.TonnelTonnel:
            {
                var telegramUrl = $"https://t.me/nft/{gift.TonnelGift!.TelegramGiftId}";
                buttons =
                [
                    [
                        InlineKeyboardButton.WithUrl("Подарок", telegramUrl),
                        InlineKeyboardButton.WithUrl("TONNEL", gift.TonnelGift.BotUrl)
                    ],
                    [InlineKeyboardButton.WithUrl("Сайт", gift.TonnelGift.SiteUrl)]
                ];
                msg = $"""
                       [🎁]({telegramUrl}) *{gift.TonnelGift.Name} | {gift.TonnelGift.TelegramGiftInfo.Model.Item1} ({gift.TonnelGift.TelegramGiftInfo.Model.Item2:F1}%) | {gift.TonnelGift.TelegramGiftInfo.Backdrop.Item1} ({gift.TonnelGift.TelegramGiftInfo.Backdrop.Item2:F1}%)*
                       🔄 *TONNEL → TONNEL*

                       💹 *Перспектива:* +{gift.PercentDiff:F2}%

                       --- TONNEL --- 
                       {(gift.TonnelGift.TelegramGiftInfo.IsSold ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}
                       💲 *Текущая цена:* {gift.TonnelGift.Price:F2} TON
                       🔥 *Активность:* {gift.TonnelGift.Activity switch {
                           Activity.Low => "Низкая",
                           Activity.Medium => "Средняя",
                           _ => "Высокая"
                       }}
                       ⏱️ *Последняя сделка:* {(gift.TonnelGift.ActivityLastSell is not null ? $"{gift.TonnelGift.ActivityLastSell.Price:F2} TON ({gift.TonnelGift.ActivityLastSell.Time:MM.dd hh:mm} UTC)" : "Недостаточно данных")}
                       📉 *Нижний уровень цен (25%):* {(gift.TonnelGift.Percentile25 is not null ? $"{gift.TonnelGift.Percentile25:F2} TON" : "Недостаточно данных")}
                       📈 *Высокий уровень цен (75%):* {(gift.TonnelGift.Percentile75 is not null ? $"{gift.TonnelGift.Percentile75:F2} TON" : "Недостаточно данных")}
                       🚀 *Максимальная цена (за 7д.):* {(gift.TonnelGift.ActivityMaxPrice is not null ? $"{gift.TonnelGift.ActivityMaxPrice:F2} TON" : "Недостаточно данных")}

                       --- [TONNEL (2 флор)]({gift.TonnelGift.SecondFloorGift!.BotUrl}) --- 
                       {(gift.TonnelGift.SecondFloorGift!.TelegramGiftInfo.IsSold ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}
                       💲 *Текущая цена:* {gift.TonnelGift.SecondFloorGift!.Price:F2} TON
                       """;
                break;
            }
            case SignalType.TonnelPortals:
            {
                var telegramUrl = $"https://t.me/nft/{gift.TonnelGift!.TelegramGiftId}";
                buttons =
                [
                    [
                        InlineKeyboardButton.WithUrl("Подарок", telegramUrl),
                        InlineKeyboardButton.WithUrl("TONNEL", gift.TonnelGift.BotUrl)
                    ],
                    [InlineKeyboardButton.WithUrl("Сайт", gift.TonnelGift.SiteUrl)]
                ];
                msg = $"""
                       [🎁]({telegramUrl}) *{gift.TonnelGift.Name} | {gift.TonnelGift.TelegramGiftInfo.Model.Item1} ({gift.TonnelGift.TelegramGiftInfo.Model.Item2:F1}%) | {gift.TonnelGift.TelegramGiftInfo.Backdrop.Item1} ({gift.TonnelGift.TelegramGiftInfo.Backdrop.Item2:F1}%)*
                       🔄 *TONNEL → PORTALS*

                       💹 *Перспектива:* +{gift.PercentDiff:F2}%

                       --- TONNEL --- 
                       {(gift.TonnelGift.TelegramGiftInfo.IsSold ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}
                       💲 *Текущая цена:* {gift.TonnelGift.Price:F2} TON
                       🔥 *Активность:* {gift.TonnelGift.Activity switch {
                           Activity.Low => "Низкая",
                           Activity.Medium => "Средняя",
                           _ => "Высокая"
                       }}
                       ⏱️ *Последняя сделка:* {(gift.TonnelGift.ActivityLastSell is not null ? $"{gift.TonnelGift.ActivityLastSell.Price:F2} TON ({gift.TonnelGift.ActivityLastSell.Time:MM.dd hh:mm} UTC)" : "Недостаточно данных")}
                       📉 *Нижний уровень цен (25%):* {(gift.TonnelGift.Percentile25 is not null ? $"{gift.TonnelGift.Percentile25:F2} TON" : "Недостаточно данных")}
                       📈 *Высокий уровень цен (75%):* {(gift.TonnelGift.Percentile75 is not null ? $"{gift.TonnelGift.Percentile75:F2} TON" : "Недостаточно данных")}
                       🚀 *Максимальная цена (за 7д.):* {(gift.TonnelGift.ActivityMaxPrice is not null ? $"{gift.TonnelGift.ActivityMaxPrice:F2} TON" : "Недостаточно данных")}

                       --- [PORTALS]({gift.PortalsGift!.BotUrl}) --- 
                       {(gift.PortalsGift.TelegramGiftInfo.IsSold ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}
                       💲 *Текущая цена:* {gift.PortalsGift.Price:F2} TON
                       🔥 *Активность:* {gift.PortalsGift.Activity switch {
                           Activity.Low => "Низкая",
                           Activity.Medium => "Средняя",
                           _ => "Высокая"
                       }}
                       ⏱️ *Последняя сделка:* {(gift.PortalsGift.ActivityLastSell is not null ? $"{gift.PortalsGift.ActivityLastSell.Price:F2} TON ({gift.PortalsGift.ActivityLastSell.Time:MM.dd hh:mm} UTC)" : "Недостаточно данных")}
                       📉 *Нижний уровень цен (25%):* {(gift.PortalsGift.Percentile25 is not null ? $"{gift.PortalsGift.Percentile25:F2} TON" : "Недостаточно данных")}
                       📈 *Высокий уровень цен (75%):* {(gift.PortalsGift.Percentile75 is not null ? $"{gift.PortalsGift.Percentile75:F2} TON" : "Недостаточно данных")}
                       🚀 *Максимальная цена (за 7д.):* {(gift.PortalsGift.ActivityMaxPrice is not null ? $"{gift.PortalsGift.ActivityMaxPrice:F2} TON" : "Недостаточно данных")}
                       """;
                break;
            }
            case SignalType.PortalsPortals:
            {
                var telegramUrl = $"https://t.me/nft/{gift.PortalsGift!.TelegramGiftId}";
                buttons =
                [
                    [
                        InlineKeyboardButton.WithUrl("Подарок", telegramUrl),
                        InlineKeyboardButton.WithUrl("PORTALS", gift.PortalsGift.BotUrl)
                    ]
                ];
                msg = $"""
                       [🎁]({telegramUrl}) *{gift.PortalsGift.Name} | {gift.PortalsGift.TelegramGiftInfo.Model.Item1} ({gift.PortalsGift.TelegramGiftInfo.Model.Item2:F1}%) | {gift.PortalsGift.TelegramGiftInfo.Backdrop.Item1} ({gift.PortalsGift.TelegramGiftInfo.Backdrop.Item2:F1}%)*
                       🔄 *PORTALS → PORTALS*

                       💹 *Перспектива:* +{gift.PercentDiff:F2}%

                       --- PORTALS --- 
                       {(gift.PortalsGift.TelegramGiftInfo.IsSold ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}
                       💲 *Текущая цена:* {gift.PortalsGift.Price:F2} TON
                       🔥 *Активность:* {gift.PortalsGift.Activity switch {
                           Activity.Low => "Низкая",
                           Activity.Medium => "Средняя",
                           _ => "Высокая"
                       }}
                       ⏱️ *Последняя сделка:* {(gift.PortalsGift.ActivityLastSell is not null ? $"{gift.PortalsGift.ActivityLastSell.Price:F2} TON ({gift.PortalsGift.ActivityLastSell.Time:MM.dd hh:mm} UTC)" : "Недостаточно данных")}
                       📉 *Нижний уровень цен (25%):* {(gift.PortalsGift.Percentile25 is not null ? $"{gift.PortalsGift.Percentile25:F2} TON" : "Недостаточно данных")}
                       📈 *Высокий уровень цен (75%):* {(gift.PortalsGift.Percentile75 is not null ? $"{gift.PortalsGift.Percentile75:F2} TON" : "Недостаточно данных")}
                       🚀 *Максимальная цена (за 7д.):* {(gift.PortalsGift.ActivityMaxPrice is not null ? $"{gift.PortalsGift.ActivityMaxPrice:F2} TON" : "Недостаточно данных")}

                       --- [PORTALS (2 флор)]({gift.PortalsGift.SecondFloorGift!.BotUrl}) --- 
                       {(gift.PortalsGift.SecondFloorGift!.TelegramGiftInfo.IsSold ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}
                       💲 *Текущая цена:* {gift.PortalsGift.SecondFloorGift!.Price:F2} TON
                       """;
                break;
            }
            case SignalType.PortalsTonnel:
            {
                var telegramUrl = $"https://t.me/nft/{gift.PortalsGift!.TelegramGiftId}";
                buttons =
                [
                    [
                        InlineKeyboardButton.WithUrl("Подарок", telegramUrl),
                        InlineKeyboardButton.WithUrl("PORTALS", gift.PortalsGift.BotUrl)
                    ]
                ];
                msg = $"""
                       [🎁]({telegramUrl}) *{gift.PortalsGift.Name} | {gift.PortalsGift.TelegramGiftInfo.Model.Item1} ({gift.PortalsGift.TelegramGiftInfo.Model.Item2:F1}%) | {gift.PortalsGift.TelegramGiftInfo.Backdrop.Item1} ({gift.PortalsGift.TelegramGiftInfo.Backdrop.Item2:F1}%)*
                       🔄 *PORTALS → TONNEL*

                       💹 *Перспектива:* +{gift.PercentDiff:F2}%

                       --- PORTALS --- 
                       {(gift.PortalsGift.TelegramGiftInfo.IsSold ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}
                       💲 *Текущая цена:* {gift.PortalsGift.Price:F2} TON
                       🔥 *Активность:* {gift.PortalsGift.Activity switch {
                           Activity.Low => "Низкая",
                           Activity.Medium => "Средняя",
                           _ => "Высокая"
                       }}
                       ⏱️ *Последняя сделка:* ${(gift.PortalsGift.ActivityLastSell is not null ? $"{gift.PortalsGift.ActivityLastSell.Price:F2} TON ({gift.PortalsGift.ActivityLastSell.Time:MM.dd hh:mm} UTC)" : "Недостаточно данных")}
                       📉 *Нижний уровень цен (25%):* {(gift.PortalsGift.Percentile25 is not null ? $"{gift.PortalsGift.Percentile25:F2} TON" : "Недостаточно данных")}
                       📈 *Высокий уровень цен (75%):* {(gift.PortalsGift.Percentile75 is not null ? $"{gift.PortalsGift.Percentile75:F2} TON" : "Недостаточно данных")}
                       🚀 *Максимальная цена (за 7д.):* {(gift.PortalsGift.ActivityMaxPrice is not null ? $"{gift.PortalsGift.ActivityMaxPrice:F2} TON" : "Недостаточно данных")}

                       --- [TONNEL]({gift.TonnelGift!.BotUrl}) --- 
                       {(gift.TonnelGift.TelegramGiftInfo.IsSold ? "❌ *Состояние:* Грязный" : "✅ *Состояние:* Чистый")}
                       💲 *Текущая цена:* {gift.TonnelGift.Price:F2} TON
                       🔥 *Активность:* {gift.TonnelGift.Activity switch {
                           Activity.Low => "Низкая",
                           Activity.Medium => "Средняя",
                           _ => "Высокая"
                       }}
                       ⏱️ *Последняя сделка:* ${(gift.TonnelGift.ActivityLastSell is not null ? $"{gift.TonnelGift.ActivityLastSell.Price:F2} TON ({gift.TonnelGift.ActivityLastSell.Time:MM.dd hh:mm} UTC)" : "Недостаточно данных")}
                       📉 *Нижний уровень цен (25%):* {(gift.TonnelGift.Percentile25 is not null ? $"{gift.TonnelGift.Percentile25:F2} TON" : "Недостаточно данных")}
                       📈 *Высокий уровень цен (75%):* {(gift.TonnelGift.Percentile75 is not null ? $"{gift.TonnelGift.Percentile75:F2} TON" : "Недостаточно данных")}
                       🚀 *Максимальная цена (за 7д.):* {(gift.TonnelGift.ActivityMaxPrice is not null ? $"{gift.TonnelGift.ActivityMaxPrice:F2} TON" : "Недостаточно данных")}
                       """;
                break;
            }
            default:
                return;
        }

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