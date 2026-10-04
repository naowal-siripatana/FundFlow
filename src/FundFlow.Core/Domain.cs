namespace FundFlow.Core;

public enum OrderType { Subscription, Redemption }

public enum OrderStatus { Pending, Completed, Rejected }

public class Fund
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Nav { get; set; }
    public DateOnly NavDate { get; set; }
}

public class Order
{
    public Guid Id { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string FundCode { get; set; } = "";
    public OrderType Type { get; set; }
    public OrderStatus Status { get; set; }
    public decimal? Amount { get; set; }
    public decimal? Units { get; set; }
    public decimal? NavUsed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? RejectReason { get; set; }
}

/// <summary>
/// Transactional outbox row. Written in the same DB transaction as the Order,
/// published to RabbitMQ afterwards, so an order is never saved without its event.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
}

public record OrderPlaced(Guid OrderId, string FundCode, OrderType Type, decimal? Amount, decimal? Units);

public static class Queues
{
    public const string OrderPlaced = "order-placed";
}
