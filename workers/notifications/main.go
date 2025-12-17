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

func newWorkerID() string {
	b := make([]byte, 4)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

// ---- OTel ----

func initTracer(ctx context.Context, workerID, podName, podNS, nodeName string) (func(context.Context) error, error) {
	svcName := getenv("OTEL_SERVICE_NAME", "notifications-worker")
	endpoint := getenv("OTEL_EXPORTER_OTLP_ENDPOINT", "jaeger-collector.observability.svc.cluster.local:4317")

	exp, err := otlptracegrpc.New(ctx,
		otlptracegrpc.WithEndpoint(endpoint),
		otlptracegrpc.WithInsecure(), // plaintext gRPC (no TLS)
	)
	if err != nil {
		return nil, err
	}

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
		// if you want sampling control via env, don’t hardcode sampler here
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

// ---- logging helper: include trace/span ids ----

type LogBase struct {
	WorkerID      string
	PodName       string
	QueueName     string
	MessageID     string
	CorrelationID string
	ApplicationID string
}

func logWithTraceBase(ctx context.Context, b LogBase, format string, args ...any) {
	sc := trace.SpanContextFromContext(ctx)

	prefix := fmt.Sprintf(
		"worker_id=%s pod=%s queue=%s message_id=%s correlation_id=%s application_id=%s",
		b.WorkerID, b.PodName, b.QueueName, b.MessageID, b.CorrelationID, b.ApplicationID,
	)

	if sc.IsValid() {
		prefix = fmt.Sprintf("[trace_id=%s span_id=%s] %s", sc.TraceID().String(), sc.SpanID().String(), prefix)
	}

	log.Printf(prefix+" "+format, args...)
}

// todo: temp
func logWithTrace(ctx context.Context, format string, args ...any) {

	log.Printf(format, args...)
}

type MsgIDs struct {
	MessageID     string
	CorrelationID string
}

func headerString(h amqp091.Table, key string) (string, bool) {
	if h == nil {
		return "", false
	}
	v, ok := h[key]
	if !ok || v == nil {
		return "", false
	}
	switch t := v.(type) {
	case string:
		if t == "" {
			return "", false
		}
		return t, true
	case []byte:
		s := string(t)
		if s == "" {
			return "", false
		}
		return s, true
	default:
		return "", false
	}
}

func extractIDs(m *amqp091.Delivery, applicationID string) MsgIDs {
	// message_id: prefer AMQP property → header → generated
	msgID := m.MessageId
	if msgID == "" {
		if s, ok := headerString(m.Headers, "message_id"); ok {
			msgID = s
		}
	}
	if msgID == "" {
		msgID = newUUID()
	}

	// correlation_id: prefer AMQP property → header → application_id → fallback msgID
	corrID := m.CorrelationId
	if corrID == "" {
		if s, ok := headerString(m.Headers, "correlation_id"); ok {
			corrID = s
		}
	}
	if corrID == "" && applicationID != "" {
		corrID = applicationID
	}
	if corrID == "" {
		// last resort: keep something stable for this message
		corrID = msgID
	}

	return MsgIDs{MessageID: msgID, CorrelationID: corrID}
}

func newUUID() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:])
}

func main() {
	workerID := newWorkerID()
	podName := os.Getenv("POD_NAME")
	podNS := os.Getenv("POD_NAMESPACE")
	nodeName := os.Getenv("NODE_NAME")

	rabbitURL := getenv("RABBITMQ_CONNECTION_STRING", "amqp://guest:guest@localhost:5672/")
	queueName := getenv("WORKER_QUEUE", "notifications.commands")

	// Root context cancelled on SIGTERM/SIGINT (KEDA scale-down / rollout)
	rootCtx, stop := signal.NotifyContext(context.Background(), syscall.SIGTERM, syscall.SIGINT)
	defer stop()

	log.Printf(
		"starting notifications worker worker_id=%s pod=%s ns=%s node=%s queue=%s",
		workerID, podName, podNS, nodeName, queueName,
	)

	// OTel init (best-effort)
	var shutdownTracer func(context.Context) error
	{
		ctx, cancel := context.WithTimeout(rootCtx, 5*time.Second)
		defer cancel()
		sd, err := initTracer(ctx, workerID, podName, podNS, nodeName)
		if err != nil {
			log.Printf("otel init failed (continuing without tracing) worker_id=%s pod=%s err=%v", workerID, podName, err)
		} else {
			shutdownTracer = sd
		}
	}
	if shutdownTracer != nil {
		defer func() {
			ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			defer cancel()
			if err := shutdownTracer(ctx); err != nil {
				log.Printf("otel shutdown error worker_id=%s pod=%s err=%v", workerID, podName, err)
			}
		}()
	}

	tracer := otel.Tracer("hireflow/notifications-worker")

	conn, err := amqp091.Dial(rabbitURL)
	if err != nil {
		log.Fatalf("failed to connect to RabbitMQ worker_id=%s pod=%s err=%v", workerID, podName, err)
	}
	defer func() { _ = conn.Close() }()

	ch, err := conn.Channel()
	if err != nil {
		log.Fatalf("failed to open channel worker_id=%s pod=%s err=%v", workerID, podName, err)
	}
	defer func() { _ = ch.Close() }()

	// idempotent declare (must match publisher args exactly, or you'll get PRECONDITION_FAILED)
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
		log.Fatalf("failed to declare queue worker_id=%s pod=%s queue=%s err=%v", workerID, podName, queueName, err)
	}

	// one message at a time per worker instance
	_ = ch.Qos(1, 0, false)

	consumerTag := fmt.Sprintf("notifications-%s", workerID)
	msgs, err := ch.Consume(
		queueName,
		consumerTag,
		false, // autoAck
		false,
		false,
		false,
		nil,
	)
	if err != nil {
		log.Fatalf("failed to start consumer worker_id=%s pod=%s err=%v", workerID, podName, err)
	}

	log.Printf("worker up; waiting for messages… worker_id=%s pod=%s", workerID, podName)

	done := make(chan struct{}, 1)

	go func() {
		for m := range msgs {
			processDeliveryWithRetry(rootCtx, tracer, workerID, podName, queueName, &m)
		}
		done <- struct{}{}
	}()

	// Shutdown (KEDA scale-down / rollout)
	select {
	case <-rootCtx.Done():
		log.Printf("shutdown requested worker_id=%s pod=%s reason=%v", workerID, podName, rootCtx.Err())

		// Stop deliveries
		_ = ch.Cancel(consumerTag, false)

		// Wait bounded
		select {
		case <-done:
		case <-time.After(10 * time.Second):
		}

	case <-done:
	}

	log.Printf("notifications worker shutdown complete worker_id=%s pod=%s", workerID, podName)
}

func processDeliveryWithRetry(
	rootCtx context.Context,
	tracer trace.Tracer,
	workerID, podName, queueName string,
	m *amqp091.Delivery,
) {
	if m.Headers == nil {
		m.Headers = amqp091.Table{}
	}
	parentCtx := otel.GetTextMapPropagator().Extract(rootCtx, amqpHeadersCarrier(m.Headers))

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
	defer span.End()

	// Poison JSON -> DLQ
	var cmd SendEmailCommand
	if err := json.Unmarshal(m.Body, &cmd); err != nil {
		span.RecordError(err)
		span.SetStatus(codes.Error, "invalid json")
		base := LogBase{
			WorkerID:  workerID,
			PodName:   podName,
			QueueName: queueName,
			// message/correlation unknown (no JSON parsed) → prefer AMQP props if present
			MessageID: func() string {
				if m.MessageId != "" {
					return m.MessageId
				}
				return ""
			}(),
			CorrelationID: func() string {
				if m.CorrelationId != "" {
					return m.CorrelationId
				}
				return ""
			}(),
		}
		logWithTraceBase(ctx, base, "invalid json; dlq err=%v body=%q", err, string(m.Body))
		_ = m.Nack(false, false) // dlq
		return
	}

	//todo:temp
	logWithTrace(ctx, "decoded cmd applicationId=%q interviewId=%q jobId=%d type=%q",
		cmd.ApplicationID, cmd.InterviewID, cmd.JobID, cmd.Type)

	ids := extractIDs(m, cmd.ApplicationID)

	span.SetAttributes(
		attribute.String("messaging.message_id", ids.MessageID),
		attribute.String("messaging.correlation_id", ids.CorrelationID),
	)

	base := LogBase{
		WorkerID:      workerID,
		PodName:       podName,
		QueueName:     queueName,
		MessageID:     ids.MessageID,
		CorrelationID: ids.CorrelationID,
		ApplicationID: cmd.ApplicationID,
	}

	span.SetAttributes(
		attribute.String("hireflow.application_id", cmd.ApplicationID),
		attribute.Int64("hireflow.job_id", cmd.JobID),
		attribute.String("notification.type", cmd.Type),
	)

	const maxAttempts = 3
	backoff := []time.Duration{1 * time.Second, 3 * time.Second, 10 * time.Second}

	for attempt := 1; attempt <= maxAttempts; attempt++ {
		// If terminating, requeue immediately so another worker can process.
		select {
		case <-rootCtx.Done():
			span.SetStatus(codes.Error, "terminated before attempt; requeue")
			_ = m.Nack(false, true)
			return
		default:
		}

		attemptCtx, attemptSpan := tracer.Start(ctx, "notifications.attempt")
		attemptSpan.SetAttributes(
			attribute.Int("attempt", attempt),
			attribute.String("messaging.message_id", ids.MessageID),
			attribute.String("messaging.correlation_id", ids.CorrelationID),
			attribute.String("hireflow.application_id", cmd.ApplicationID),
		)

		err := handleSendEmailCommand(attemptCtx, workerID, podName, &cmd, attempt)
		if err == nil {
			logWithTraceBase(ctx, base, "NOTIFY SendEmail  (attempt %d/%d)", attempt, maxAttempts)
			if ackErr := m.Ack(false); ackErr != nil {
				attemptSpan.RecordError(ackErr)
				attemptSpan.SetStatus(codes.Error, "ack failed")
				logWithTraceBase(ctx, base, "ack failed (attempt %d/%d) err=%v", attempt, maxAttempts, ackErr)
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

		logWithTraceBase(ctx, base, "handler failed (attempt %d/%d) err=%v", attempt, maxAttempts, err)

		if attempt < maxAttempts {
			sleep := backoff[attempt-1]

			select {
			case <-time.After(sleep):
			case <-rootCtx.Done():
				span.SetStatus(codes.Error, "terminated during backoff; requeue")
				_ = m.Nack(false, true)
				return
			}
			continue
		}

		span.SetStatus(codes.Error, "max attempts reached; dlq")
		logWithTraceBase(ctx, base, "max attempts reached; dlq (attempt %d/%d) err=%v", attempt, maxAttempts, err)
		_ = m.Nack(false, false)
		return
	}
}

func handleSendEmailCommand(ctx context.Context, workerID, podName string, cmd *SendEmailCommand, attempt int) error {
	// demo: fail first 2 attempts, succeed on 3rd (per message)
	if attempt <= 2 {
		return fmt.Errorf("simulated transient failure")
	}

	return nil
}

func getenv(k, def string) string {
	v := os.Getenv(k)
	if v == "" {
		return def
	}
	return v
}
