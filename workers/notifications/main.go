package main

import (
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

	log.Printf(
		"[workerID=%s pod=%s ns=%s node=%s] starting notifications worker (queue=%s, rabbit=%s)",
		workerID, podName, podNS, nodeName, queueName, rabbitURL,
	)

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

	msgs, err := ch.Consume(
		queueName,
		"",    // consumer
		false, // autoAck
		false, // exclusive
		false, // noLocal
		false, // noWait
		nil,   // args
	)
	if err != nil {
		log.Fatalf("[workerID=%s pod=%s] failed to start consumer: %v", workerID, podName, err)
	}

	log.Printf("[workerID=%s pod=%s] worker up; waiting for messages…", workerID, podName)

	// channel to signal that the consumer loop stopped
	done := make(chan struct{}, 1)

	// consumer loop
	go func() {
		for m := range msgs {
			processDeliveryWithRetry(workerID, podName, &m)
		}
		log.Printf("[workerID=%s pod=%s] messages channel closed (connection/channel ended)", workerID, podName)
		done <- struct{}{}
	}()

	// handle signals - KEDA scale-down
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGTERM, syscall.SIGINT)

	select {
	case sig := <-sigCh:
		log.Printf("[workerID=%s pod=%s] received signal: %s (scale-down or rollout), shutting down…", workerID, podName, sig)
	case <-done:
		log.Printf("[workerID=%s pod=%s] consumer loop finished; shutting down…", workerID, podName)
	}

	log.Printf("[workerID=%s pod=%s] notifications worker shutdown complete.", workerID, podName)
}

// processDeliveryWithRetry parses the message and retries the handler with backoff
func processDeliveryWithRetry(workerID, podName string, m *amqp091.Delivery) {
	// parse json – if this fails, message is "poison" -> immediate dlq
	var cmd SendEmailCommand
	if err := json.Unmarshal(m.Body, &cmd); err != nil {
		log.Printf(
			"[workerID=%s pod=%s] invalid message, sending to DLQ (json error: %v): %s",
			workerID, podName, err, string(m.Body),
		)
		_ = m.Nack(false, false) // requeue=false -> dlx -> dlq
		return
	}

	// retry envelope around the actual handler
	const maxAttempts = 3
	backoff := []time.Duration{1 * time.Second, 3 * time.Second, 10 * time.Second}

	for attempt := 1; attempt <= maxAttempts; attempt++ {
		err := handleSendEmailCommand(workerID, podName, &cmd)
		if err == nil {
			// success
			if ackErr := m.Ack(false); ackErr != nil {
				log.Printf("[workerID=%s pod=%s] failed to ack message: %v", workerID, podName, ackErr)
			}
			return
		}

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
			time.Sleep(sleep)
			continue
		}

		// all attempts failed -> dlq
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

	// todo: plug your actual email sender here.
	// for now, just log and pretend it succeeded.
	log.Printf(
		"[workerID=%s pod=%s] NOTIFY SendEmail to=%s subject=%q applicationId=%s",
		workerID, podName, cmd.To, cmd.Subject, cmd.ApplicationID,
	)

	// to simulate transient failure for testing, you could temporarily:
	// return fmt.Errorf("simulated transient failure")

	return nil
}
