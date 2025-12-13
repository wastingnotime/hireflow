package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"os/signal"
	"sync/atomic"
	"syscall"
	"time"

	"github.com/rabbitmq/amqp091-go"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/codes"
	"go.opentelemetry.io/otel/propagation"
	"go.opentelemetry.io/otel/sdk/resource"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	"go.opentelemetry.io/otel/trace"

	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracegrpc"
	semconv "go.opentelemetry.io/otel/semconv/v1.27.0"
)

type SendEmailCommand struct {
	Type          string `json:"type"`
	To            string `json:"to"`
	Subject       string `json:"subject"`
	Body          string `json:"body"`
	ApplicationID string `json:"applicationId"`
	InterviewID   string `json:"interviewId"`
	JobID         int64  `json:"jobId"`
}

// newWorkerID generates a short ID so you can distinguish worker instances
func newWorkerID() string {
	b := make([]byte, 4)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

// ---- OpenTelemetry ----

func initTracer(ctx context.Context, workerID, podName, podNS, nodeName string) (func(context.Context) error, error) {
	// golang otel driver uses env only for defaults, if you need to configure some details you break the defauls
	// so you need to configure the entire config group
	// OTEL_SERVICE_NAME=notifications-worker
	// OTEL_EXPORTER_OTLP_ENDPOINT=otel-collector.observability.svc.cluster.local:4317
	svcName := getenv("OTEL_SERVICE_NAME", "notifications-worker")
	endpoint := getenv("OTEL_EXPORTER_OTLP_ENDPOINT", "jaeger-collector.observability.svc.cluster.local:4317")

	exp, err := otlptracegrpc.New(ctx,
		otlptracegrpc.WithEndpoint(endpoint),
		otlptracegrpc.WithInsecure(), // <-- force plaintext, no TLS
	)

	if err != nil {
		return nil, err
	}

	// richer if compared to the default
	res, err := resource.New(ctx,
		resource.WithAttributes(
			semconv.ServiceName(svcName),
			attribute.String("hireflow.worker_id", workerID),
			attribute.String("k8s.pod.name", podName),
			attribute.String("k8s.namespace.name", podNS),
			attribute.String("k8s.node.name", nodeName),
		),
	)
	if err != nil {
		return nil, err
	}

	tp := sdktrace.NewTracerProvider(
		sdktrace.WithResource(res),
		sdktrace.WithBatcher(exp),
		//sdktrace.WithSampler(sdktrace.ParentBased(sdktrace.AlwaysSample())), // demo-friendly (default)
	)

	otel.SetTracerProvider(tp)
	otel.SetTextMapPropagator(propagation.NewCompositeTextMapPropagator(
		propagation.TraceContext{},
		propagation.Baggage{},
	))

	return tp.Shutdown, nil
}

// AMQP headers carrier for trace context extraction
type amqpHeadersCarrier map[string]interface{}

func (c amqpHeadersCarrier) Get(key string) string {
	v, ok := c[key]
	if !ok || v == nil {
		return ""
	}
	switch t := v.(type) {
	case string:
		return t
	case []byte:
		return string(t)
	default:
		return ""
	}
}

func (c amqpHeadersCarrier) Set(key, value string) { c[key] = value }

func (c amqpHeadersCarrier) Keys() []string {
	keys := make([]string, 0, len(c))
	for k := range c {
		keys = append(keys, k)
	}
	return keys
}

func main() {
	workerID := newWorkerID()
	podName := os.Getenv("POD_NAME")
	podNS := os.Getenv("POD_NAMESPACE")
	nodeName := os.Getenv("NODE_NAME")

	rabbitURL := os.Getenv("RABBITMQ_CONNECTION_STRING")
	if rabbitURL == "" {
		rabbitURL = "amqp://guest:guest@localhost:5672/"
	}

	queueName := os.Getenv("WORKER_QUEUE")
	if queueName == "" {
		queueName = "notifications.commands"
	}

	// Root context cancelled on SIGTERM/SIGINT (KEDA scale-down / rollout)
	rootCtx, stop := signal.NotifyContext(context.Background(), syscall.SIGTERM, syscall.SIGINT)
	defer stop()

	log.Printf(
		"[workerID=%s pod=%s ns=%s node=%s] starting notifications worker (queue=%s, rabbit=%s)",
		workerID, podName, podNS, nodeName, queueName, rabbitURL,
	)

	// OTel init (best-effort; if it fails, we still run)
	var shutdownTracer func(context.Context) error
	{
		ctx, cancel := context.WithTimeout(rootCtx, 5*time.Second)
		defer cancel()
		sd, err := initTracer(ctx, workerID, podName, podNS, nodeName)
		if err != nil {
			log.Printf("[workerID=%s pod=%s] otel init failed (continuing without tracing): %v", workerID, podName, err)
		} else {
			shutdownTracer = sd
		}
	}
	if shutdownTracer != nil {
		defer func() {
			ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			defer cancel()
			if err := shutdownTracer(ctx); err != nil {
				log.Printf("[workerID=%s pod=%s] otel shutdown error: %v", workerID, podName, err)
			}
		}()
	}

	tracer := otel.Tracer("hireflow/notifications-worker")

	conn, err := amqp091.Dial(rabbitURL)
	if err != nil {
		log.Fatalf("[workerID=%s pod=%s] failed to connect to RabbitMQ: %v", workerID, podName, err)
	}
	defer func() {
		_ = conn.Close()
		log.Printf("[workerID=%s pod=%s] RabbitMQ connection closed", workerID, podName)
	}()

	ch, err := conn.Channel()
	if err != nil {
		log.Fatalf("[workerID=%s pod=%s] failed to open channel: %v", workerID, podName, err)
	}
	defer func() {
		_ = ch.Close()
		log.Printf("[workerID=%s pod=%s] RabbitMQ channel closed", workerID, podName)
	}()

	// publisher is the real owner, but to avoid create a more complex control lets do an idempotent declare
	_, err = ch.QueueDeclare(
		queueName,
		true,  // durable
		false, // autoDelete
		false, // exclusive
		false, // noWait
		amqp091.Table{
			"x-dead-letter-exchange":    "hireflow.dlx",
			"x-dead-letter-routing-key": queueName + ".dlq",
		},
	)
	if err != nil {
		log.Fatalf("[workerID=%s pod=%s] failed to declare queue %q: %v", workerID, podName, queueName, err)
	}

	// process one message at a time per worker
	if err := ch.Qos(1, 0, false); err != nil {
		log.Printf("[workerID=%s pod=%s] failed to set QoS: %v", workerID, podName, err)
	}

	consumerTag := fmt.Sprintf("notifications-%s", workerID)

	msgs, err := ch.Consume(
		queueName,
		consumerTag, // consumer tag (important: allows cancel)
		false,       // autoAck
		false,       // exclusive
		false,       // noLocal
		false,       // noWait
		nil,         // args
	)
	if err != nil {
		log.Fatalf("[workerID=%s pod=%s] failed to start consumer: %v", workerID, podName, err)
	}

	log.Printf("[workerID=%s pod=%s] worker up; waiting for messages…", workerID, podName)

	done := make(chan struct{}, 1)

	// consumer loop
	go func() {
		for m := range msgs {
			processDeliveryWithRetry(rootCtx, tracer, workerID, podName, queueName, &m)
		}
		log.Printf("[workerID=%s pod=%s] messages channel closed (connection/channel ended)", workerID, podName)
		done <- struct{}{}
	}()

	// Shutdown path (KEDA scale-down / rollout)
	select {
	case <-rootCtx.Done():
		log.Printf("[workerID=%s pod=%s] received signal: %v (scale-down or rollout), cancelling consumer…", workerID, podName, rootCtx.Err())

		// Ask broker to stop deliveries. In-flight message can still be ack/nack.
		if err := ch.Cancel(consumerTag, false); err != nil {
			log.Printf("[workerID=%s pod=%s] failed to cancel consumer: %v", workerID, podName, err)
		}

		// Wait for consumer loop to end (bounded). Kubernetes terminationGracePeriodSeconds matters here.
		select {
		case <-done:
			log.Printf("[workerID=%s pod=%s] consumer loop finished; shutdown…", workerID, podName)
		case <-time.After(10 * time.Second):
			log.Printf("[workerID=%s pod=%s] shutdown timeout waiting consumer loop; exiting", workerID, podName)
		}

	case <-done:
		log.Printf("[workerID=%s pod=%s] consumer loop finished; shutting down…", workerID, podName)
	}

	log.Printf("[workerID=%s pod=%s] notifications worker shutdown complete.", workerID, podName)
}

// processDeliveryWithRetry parses the message and retries the handler with backoff
func processDeliveryWithRetry(
	rootCtx context.Context,
	tracer trace.Tracer,
	workerID, podName, queueName string,
	m *amqp091.Delivery,
) {
	// Extract trace context from headers if publisher injected it.
	if m.Headers == nil {
		m.Headers = amqp091.Table{}
	}
	parentCtx := otel.GetTextMapPropagator().Extract(rootCtx, amqpHeadersCarrier(m.Headers))

	// One span per message handling
	ctx, span := tracer.Start(parentCtx, "notifications.handle",
		trace.WithSpanKind(trace.SpanKindConsumer),
	)
	span.SetAttributes(
		attribute.String("messaging.system", "rabbitmq"),
		attribute.String("messaging.destination", queueName),
		attribute.String("messaging.operation", "process"),
		attribute.String("amqp.exchange", m.Exchange),
		attribute.String("amqp.routing_key", m.RoutingKey),
		attribute.String("amqp.consumer_tag", m.ConsumerTag),
		attribute.String("hireflow.worker_id", workerID),
	)
	if m.MessageId != "" {
		span.SetAttributes(attribute.String("messaging.message_id", m.MessageId))
	}
	defer span.End()

	// parse json – if this fails, message is "poison" -> immediate dlq
	var cmd SendEmailCommand
	if err := json.Unmarshal(m.Body, &cmd); err != nil {
		span.RecordError(err)
		span.SetStatus(codes.Error, "invalid json")

		log.Printf(
			"[workerID=%s pod=%s] invalid message, sending to DLQ (json error: %v): %s",
			workerID, podName, err, string(m.Body),
		)
		_ = m.Nack(false, false) // requeue=false -> dlx -> dlq
		return
	}

	span.SetAttributes(
		attribute.String("hireflow.application_id", cmd.ApplicationID),
		attribute.Int64("hireflow.job_id", cmd.JobID),
		attribute.String("notification.type", cmd.Type),
	)

	// retry envelope around the actual handler
	const maxAttempts = 3
	backoff := []time.Duration{1 * time.Second, 3 * time.Second, 10 * time.Second}

	for attempt := 1; attempt <= maxAttempts; attempt++ {
		// Span per attempt (helps you see retries in the trace)
		_, attemptSpan := tracer.Start(ctx, "notifications.attempt",
			trace.WithSpanKind(trace.SpanKindInternal),
		)
		attemptSpan.SetAttributes(attribute.Int("attempt", attempt))

		err := handleSendEmailCommand(workerID, podName, &cmd)
		if err == nil {
			if ackErr := m.Ack(false); ackErr != nil {
				attemptSpan.RecordError(ackErr)
				attemptSpan.SetStatus(codes.Error, "ack failed")
				log.Printf("[workerID=%s pod=%s] failed to ack message: %v", workerID, podName, ackErr)
			} else {
				attemptSpan.SetStatus(codes.Ok, "ok")
			}
			attemptSpan.End()
			span.SetStatus(codes.Ok, "processed")
			return
		}

		attemptSpan.RecordError(err)
		attemptSpan.SetStatus(codes.Error, "handler failed")
		attemptSpan.End()

		log.Printf(
			"[workerID=%s pod=%s] handler failed (attempt %d/%d) for applicationId=%s: %v",
			workerID, podName, attempt, maxAttempts, cmd.ApplicationID, err,
		)

		if attempt < maxAttempts {
			sleep := backoff[attempt-1]
			log.Printf(
				"[workerID=%s pod=%s] backing off for %s before retrying applicationId=%s",
				workerID, podName, sleep, cmd.ApplicationID,
			)

			select {
			case <-time.After(sleep):
			case <-rootCtx.Done():
				// If we're being terminated, requeue the message so another worker can pick it up.
				span.SetStatus(codes.Error, "terminated during backoff; requeue")
				_ = m.Nack(false, true)
				return
			}

			continue
		}

		// all attempts failed -> dlq
		span.SetStatus(codes.Error, "max attempts reached; dlq")
		log.Printf(
			"[workerID=%s pod=%s] max attempts reached for applicationId=%s, sending to DLQ",
			workerID, podName, cmd.ApplicationID,
		)
		_ = m.Nack(false, false)
		return
	}
}

var counter int32

// handleSendEmailCommand is where you'd actually call an email provider.
// for M2, we just simulate success/failure pattern.
func handleSendEmailCommand(workerID, podName string, cmd *SendEmailCommand) error {
	// faking issues
	n := atomic.AddInt32(&counter, 1)
	if n <= 2 {
		return fmt.Errorf("simulated transient failure")
	}
	log.Printf("Now it works on attempt %d", n)

	atomic.StoreInt32(&counter, 0)

	log.Printf(
		"[workerID=%s pod=%s] NOTIFY SendEmail to=%s subject=%q applicationId=%s",
		workerID, podName, cmd.To, cmd.Subject, cmd.ApplicationID,
	)

	return nil
}

func getenv(k, def string) string {
	v := os.Getenv(k)
	if v == "" {
		return def
	}
	return v
}
