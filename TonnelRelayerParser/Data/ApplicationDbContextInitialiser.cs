using Microsoft.EntityFrameworkCore;
using NLog;

namespace Moahk.Data;

public class ApplicationDbContextInitialiser
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static async Task InitialiseAsync()
    {
        await using var context = new ApplicationDbContext();
        try
        {
            await context.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "An error occurred while migrating the database.");
            throw;
        }

        try
        {
            await TrySeedAsync(context);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "An error occurred while seeding the database.");
            throw;
        }
    }

    private static async Task TrySeedAsync(ApplicationDbContext context)
    {
        await context.SaveChangesAsync();
    }
}