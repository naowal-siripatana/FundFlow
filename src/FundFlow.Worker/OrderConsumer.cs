using System.Text.Json;
using Dapper;
using FundFlow.Core;
using Microsoft.Data.SqlClient;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FundFlow.Worker;

/// <summary>
/// Consumes OrderPlaced events and allots units at the fund's NAV.
/// Idempotent: a redelivered message for an order that is no longer Pending is acked and skipped.
/// </summary>
public sealed class OrderConsumer(IConfiguration config, ILogger<OrderConsumer> log) : BackgroundService
{
    private IConnection? _connection;
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory { Uri = new Uri(config.GetConnectionString("RabbitMq")!) };
        _connection = await ConnectWithRetryAsync(factory, stoppingToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await _channel.QueueDeclareAsync(Queues.OrderPlaced, durable: true, exclusive: false,
            autoDelete: false, arguments: null, cancellationToken: stoppingToken);
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 10, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                var message = JsonSerializer.Deserialize<OrderPlaced>(ea.Body.Span)
                    ?? throw new InvalidOperationException("Empty message body");
                await ProcessAsync(message, stoppingToken);
                await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                // requeue:false avoids an infinite poison-message loop.
                // Next step: route these to a dead-letter queue instead of dropping them.
                log.LogError(ex, "Failed to process message {MessageId}", ea.BasicProperties.MessageId);
                await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
            }
        };

        await _channel.BasicConsumeAsync(Queues.OrderPlaced, autoAck: false, consumer: consumer,
            cancellationToken: stoppingToken);
        log.LogInformation("Listening on queue {Queue}", Queues.OrderPlaced);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task ProcessAsync(OrderPlaced message, CancellationToken ct)
    {
        await using var conn = new SqlConnection(config.GetConnectionString("Sql"));
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // UPDLOCK: two workers holding the same redelivered message cannot both process it.
        var status = await conn.ExecuteScalarAsync<string?>(
            "SELECT Status FROM Orders WITH (UPDLOCK, ROWLOCK) WHERE Id = @Id",
            new { Id = message.OrderId }, tx);

        if (status != nameof(OrderStatus.Pending))
        {
            log.LogInformation("Order {Id} is {Status}; skipping duplicate delivery", message.OrderId, status ?? "missing");
            await tx.CommitAsync(ct);
            return;
        }

        var nav = await conn.ExecuteScalarAsync<decimal?>(
            "SELECT Nav FROM Funds WHERE Code = @Code", new { Code = message.FundCode }, tx);

        if (nav is null)
        {
            await conn.ExecuteAsync(
                "UPDATE Orders SET Status = @Status, RejectReason = @Reason, ProcessedAt = SYSUTCDATETIME() WHERE Id = @Id",
                new { Status = nameof(OrderStatus.Rejected), Reason = "Fund has no NAV", Id = message.OrderId }, tx);
            await tx.CommitAsync(ct);
            return;
        }

        decimal amount, units;
        if (message.Type == OrderType.Subscription)
        {
            amount = message.Amount!.Value;
            units = UnitCalculator.UnitsForAmount(amount, nav.Value);
        }
        else
        {
            units = message.Units!.Value;
            amount = UnitCalculator.AmountForUnits(units, nav.Value);
        }

        await conn.ExecuteAsync(
            """
            UPDATE Orders
            SET Status = @Status, Amount = @Amount, Units = @Units, NavUsed = @Nav, ProcessedAt = SYSUTCDATETIME()
            WHERE Id = @Id AND Status = @Pending
            """,
            new
            {
                Status = nameof(OrderStatus.Completed),
                Amount = amount,
                Units = units,
                Nav = nav.Value,
                Id = message.OrderId,
                Pending = nameof(OrderStatus.Pending)
            }, tx);

        await tx.CommitAsync(ct);
        log.LogInformation("Order {Id} completed: {Units} units @ NAV {Nav}", message.OrderId, units, nav);
    }

    private async Task<IConnection> ConnectWithRetryAsync(ConnectionFactory factory, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await factory.CreateConnectionAsync(ct);
            }
            catch (Exception ex) when (attempt < 15 && ex is not OperationCanceledException)
            {
                log.LogWarning("RabbitMQ not ready (attempt {Attempt}): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }
}
