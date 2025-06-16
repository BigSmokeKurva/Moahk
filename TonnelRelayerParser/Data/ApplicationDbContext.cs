using Microsoft.EntityFrameworkCore;
using Moahk.Data.Entities;

namespace Moahk.Data;

public class ApplicationDbContext : DbContext
{
    public DbSet<User> Users { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
            optionsBuilder.UseNpgsql(ConfigurationManager.GetConnectionString());
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity => { entity.HasKey(x => x.Id); });
    }

    public async Task<User> AddUserAsync(long id)
    {
        var user = await Users.FirstOrDefaultAsync(x => x.Id == id);
        if (user != null) return user;
        user = new User { Id = id };
        await Users.AddAsync(user);
        await SaveChangesAsync();
        return user;
    }

    // public async Task<User> GetUserAsync(long id)
    // {
    //     return await Users.FirstAsync(x => x.Id == id);
    // }
}