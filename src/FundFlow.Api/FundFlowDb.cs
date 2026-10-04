using FundFlow.Core;
using Microsoft.EntityFrameworkCore;

namespace FundFlow.Api;

public class FundFlowDb(DbContextOptions<FundFlowDb> options) : DbContext(options)
{
    public DbSet<Fund> Funds => Set<Fund>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Fund>(e =>
        {
            e.HasKey(f => f.Code);
            e.Property(f => f.Code).HasMaxLength(20);
            e.Property(f => f.Name).HasMaxLength(200);
            e.Property(f => f.Nav).HasPrecision(18, 4);
        });

        b.Entity<Order>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.IdempotencyKey).HasMaxLength(100);
            e.HasIndex(o => o.IdempotencyKey).IsUnique();          // the DB, not the code, guarantees no duplicate orders
            e.Property(o => o.FundCode).HasMaxLength(20);
            e.Property(o => o.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(o => o.Amount).HasPrecision(18, 2);
            e.Property(o => o.Units).HasPrecision(18, 4);
            e.Property(o => o.NavUsed).HasPrecision(18, 4);
            e.Property(o => o.RejectReason).HasMaxLength(500);
            e.HasIndex(o => new { o.Status, o.CreatedAt });
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Type).HasMaxLength(100);
            // Filtered index: the publisher only ever scans unpublished rows.
            e.HasIndex(m => m.CreatedAt).HasFilter("[PublishedAt] IS NULL");
        });
    }
}

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FundFlowDb>();
        var log = scope.ServiceProvider.GetRequiredService<ILogger<FundFlowDb>>();

        // SQL Server in Docker takes a while to accept connections.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.EnsureCreatedAsync();   // swap for EF migrations once the schema settles
                break;
            }
            catch (Exception ex) when (attempt < 15)
            {
                log.LogWarning("Database not ready (attempt {Attempt}): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }

        if (!await db.Funds.AnyAsync())
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            db.Funds.AddRange(
                new Fund { Code = "FF-EQUITY", Name = "FundFlow Thai Equity Fund", Nav = 15.2345m, NavDate = today },
                new Fund { Code = "FF-BOND", Name = "FundFlow Fixed Income Fund", Nav = 10.8721m, NavDate = today },
                new Fund { Code = "FF-GOLD", Name = "FundFlow Gold Fund", Nav = 22.1050m, NavDate = today });
            await db.SaveChangesAsync();
        }
    }
}
