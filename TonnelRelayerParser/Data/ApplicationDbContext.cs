using Microsoft.EntityFrameworkCore;
using Moahk.Data.Entities;
using Moahk.Data.Enums;

namespace Moahk.Data;

public class ApplicationDbContext : DbContext
{
    public DbSet<User> Users { get; set; }
    public DbSet<CrystalpayInvoice> CrystalpayInvoices { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
            optionsBuilder.UseNpgsql(ConfigurationManager.GetConnectionString());
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PriceMax).HasDefaultValue(10000);
            entity.Property(x => x.ProfitPercent).HasDefaultValue(10);
            entity.Property(x => x.Criteria).HasDefaultValue(Criteria.SecondFloor);
            entity.Property(x => x.Status).HasDefaultValue(Status.None);
            entity.Property(x => x.ReferralPercent).HasDefaultValue(25);
            entity.Property(x => x.ModelPercentMax).HasDefaultValue(100);
        });
        modelBuilder.Entity<CrystalpayInvoice>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasOne(x => x.User);
        });
    }

    public async Task<(User user, bool isNew)> AddUserAsync(long id)
    {
        var user = await Users.FirstOrDefaultAsync(x => x.Id == id);
        if (user != null) return (user, false);
        user = new User { Id = id };
        await Users.AddAsync(user);
        await SaveChangesAsync();
        return (user, true);
    }

    // public async Task<User> GetUserAsync(long id)
    // {
    //     return await Users.FirstAsync(x => x.Id == id);
    // }
}