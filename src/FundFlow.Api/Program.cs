using System.Text.Json;
using System.Text.Json.Serialization;
using FundFlow.Api;
using FundFlow.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddDbContext<FundFlowDb>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Sql")));

builder.Services.AddStackExchangeRedisCache(o =>
    o.Configuration = builder.Configuration.GetConnectionString("Redis"));

builder.Services.AddSingleton<RabbitMqPublisher>();
builder.Services.AddHostedService<OutboxPublisher>();

var app = builder.Build();

await DbInitializer.InitializeAsync(app.Services);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Cache-aside: read Redis first, fall back to SQL, then populate Redis with a TTL.
app.MapGet("/funds/{code}/nav", async (string code, FundFlowDb db, IDistributedCache cache) =>
{
    var fundCode = code.ToUpperInvariant();
    var cacheKey = $"nav:{fundCode}";

    var cached = await cache.GetStringAsync(cacheKey);
    if (cached is not null)
        return Results.Ok(JsonSerializer.Deserialize<NavResponse>(cached)! with { FromCache = true });

    var fund = await db.Funds.AsNoTracking().FirstOrDefaultAsync(f => f.Code == fundCode);
    if (fund is null) return Results.NotFound();

    var response = new NavResponse(fund.Code, fund.Nav, fund.NavDate, FromCache: false);
    await cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(response),
        new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });

    return Results.Ok(response);
});

app.MapPost("/orders", async (PlaceOrderRequest req, HttpRequest http, FundFlowDb db) =>
{
    var key = http.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
        return Results.BadRequest(new { error = "Idempotency-Key header (max 100 chars) is required" });

    // A retry of a request we already accepted returns the original result.
    var existing = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.IdempotencyKey == key);
    if (existing is not null) return Results.Ok(OrderResponse.From(existing));

    var error = OrderValidator.Validate(req.Type, req.Amount, req.Units);
    if (error is not null) return Results.BadRequest(new { error });

    var fundCode = req.FundCode.ToUpperInvariant();
    if (!await db.Funds.AnyAsync(f => f.Code == fundCode))
        return Results.BadRequest(new { error = $"Unknown fund {fundCode}" });

    var order = new Order
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = key,
        FundCode = fundCode,
        Type = req.Type,
        Status = OrderStatus.Pending,
        Amount = req.Amount,
        Units = req.Units,
        CreatedAt = DateTime.UtcNow
    };
    var evt = new OrderPlaced(order.Id, order.FundCode, order.Type, order.Amount, order.Units);

    db.Orders.Add(order);
    db.OutboxMessages.Add(new OutboxMessage
    {
        Id = Guid.NewGuid(),
        Type = nameof(OrderPlaced),
        Payload = JsonSerializer.Serialize(evt),
        CreatedAt = DateTime.UtcNow
    });

    try
    {
        await db.SaveChangesAsync();   // order + outbox row commit together, or not at all
    }
    catch (DbUpdateException)
    {
        // Two concurrent requests with the same key: the unique index lets only one win.
        var winner = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.IdempotencyKey == key);
        if (winner is null) throw;
        return Results.Ok(OrderResponse.From(winner));
    }

    return Results.Accepted($"/orders/{order.Id}", OrderResponse.From(order));
});

app.MapGet("/orders/{id:guid}", async (Guid id, FundFlowDb db) =>
{
    var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
    return order is null ? Results.NotFound() : Results.Ok(OrderResponse.From(order));
});

app.Run();

public record PlaceOrderRequest(string FundCode, OrderType Type, decimal? Amount, decimal? Units);

public record NavResponse(string Code, decimal Nav, DateOnly NavDate, bool FromCache);

public record OrderResponse(
    Guid Id, string FundCode, OrderType Type, OrderStatus Status,
    decimal? Amount, decimal? Units, decimal? NavUsed, string? RejectReason)
{
    public static OrderResponse From(Order o) =>
        new(o.Id, o.FundCode, o.Type, o.Status, o.Amount, o.Units, o.NavUsed, o.RejectReason);
}
