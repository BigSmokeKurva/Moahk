using Microsoft.EntityFrameworkCore;
using Moahk.Data.Entities;

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
        modelBuilder.Entity<User>(entity => { entity.HasKey(x => x.Id); });
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