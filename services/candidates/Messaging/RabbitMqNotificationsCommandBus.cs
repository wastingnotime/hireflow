using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace WastingNoTime.HireFlow.Candidates.Api.Messaging;

public sealed class RabbitMqNotificationsCommandBus : INotificationsCommandBus
{
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private readonly string _queueName = "notifications.commands";

    public RabbitMqNotificationsCommandBus(string connectionString)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(connectionString)
        };

        _connection = factory.CreateConnection("candidates-notifications-publisher");
        _channel = _connection.CreateModel();

        // ensure queue exists (durable, not auto-deleted)
        _channel.QueueDeclare(
            queue: _queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);
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
            exchange: "",
            routingKey: _queueName,
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
