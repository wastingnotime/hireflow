using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace WastingNoTime.HireFlow.Candidates.Api.Messaging;

public sealed class RabbitMqNotificationsCommandBus : INotificationsCommandBus
{
    private readonly IConnection _connection;
    private readonly IModel _channel;

    private const string MainQueue = "notifications.commands";
    private const string DlxExchange = "hireflow.dlx";
    private const string DlqQueue = "notifications.commands.dlq";

    public RabbitMqNotificationsCommandBus(string connectionString)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(connectionString)
        };

        _connection = factory.CreateConnection("candidates-notifications-publisher");
        _channel = _connection.CreateModel();

        EnsureTopology(_channel);
    }

    private static void EnsureTopology(IModel channel)
    {
        // shared dlx for all dead-lettered messages
        channel.ExchangeDeclare(
            exchange: DlxExchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            arguments: null);

        // dlq bound to dlx
        channel.QueueDeclare(
            queue: DlqQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);

        channel.QueueBind(
            queue: DlqQueue,
            exchange: DlxExchange,
            routingKey: DlqQueue);

        // main queue with dlq config
        var mainQueueArgs = new Dictionary<string, object>
        {
            ["x-dead-letter-exchange"] = DlxExchange,
            ["x-dead-letter-routing-key"] = DlqQueue
        };

        channel.QueueDeclare(
            queue: MainQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: mainQueueArgs);
    }

    public Task PublishSendEmailAsync(
        string to,
        string subject,
        string body,
        string applicationId,
        string interviewId,
        long jobId,
        CancellationToken ct = default)
    {
        var payload = new
        {
            type = "SendEmail",
            to,
            subject,
            body,
            applicationId,
            interviewId,
            jobId
        };

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        var props = _channel.CreateBasicProperties();
        props.DeliveryMode = 2; // persistent

        _channel.BasicPublish(
            exchange: "",                 // default exchange
            routingKey: MainQueue,        // notifications.commands
            basicProperties: props,
            body: bytes);

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try { _channel?.Close(); } catch { /* ignore */ }
        try { _connection?.Close(); } catch { /* ignore */ }
        _channel?.Dispose();
        _connection?.Dispose();
        return ValueTask.CompletedTask;
    }
}
