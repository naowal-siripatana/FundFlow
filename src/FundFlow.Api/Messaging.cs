using System.Text;
using FundFlow.Core;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace FundFlow.Api;

/// <summary>One long-lived connection + channel, created lazily and recreated if it drops.</summary>
public sealed class RabbitMqPublisher(IConfiguration config) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(string payload, Guid messageId, CancellationToken ct)
    {
        var channel = await GetChannelAsync(ct);
        var props = new BasicProperties
        {
            Persistent = true,                       // survive a broker restart
            MessageId = messageId.ToString(),        // lets consumers de-duplicate
            ContentType = "application/json"
        };
        await channel.BasicPublishAsync(
            exchange: "",
            routingKey: Queues.OrderPlaced,
            mandatory: false,
            basicProperties: props,
            body: Encoding.UTF8.GetBytes(payload),
            cancellationToken: ct);
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true }) return _channel;

        await _lock.WaitAsync(ct);
        try
        {
            if (_channel is { IsOpen: true }) return _channel;

            var factory = new ConnectionFactory { Uri = new Uri(config.GetConnectionString("RabbitMq")!) };
            _connection = await factory.CreateConnectionAsync(ct);
            _channel = await _connection.CreateChannelAsync(cancellationToken: ct);
            await _channel.QueueDeclareAsync(Queues.OrderPlaced, durable: true, exclusive: false,
                autoDelete: false, arguments: null, cancellationToken: ct);
            return _channel;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }
}

/// <summary>
/// Polls the outbox and publishes pending events. Delivery is at-least-once:
/// if the process dies between publish and SaveChanges, the message is sent again,
/// which is why the worker must be idempotent.
/// </summary>
public sealed class OutboxPublisher(
    IServiceScopeFactory scopes,
    RabbitMqPublisher publisher,
    ILogger<OutboxPublisher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Outbox publish failed; will retry");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task PublishBatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FundFlowDb>();

        var batch = await db.OutboxMessages
            .Where(m => m.PublishedAt == null)
            .OrderBy(m => m.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        foreach (var message in batch)
        {
            await publisher.PublishAsync(message.Payload, message.Id, ct);
            message.PublishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            log.LogInformation("Published {Type} {Id}", message.Type, message.Id);
        }
    }
}
