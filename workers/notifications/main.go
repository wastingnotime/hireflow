package main

import (
	"crypto/rand"
	"encoding/hex"
	"log"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/rabbitmq/amqp091-go"
)

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

	// ensure queue exists
	_, err = ch.QueueDeclare(
		queueName,
		true,  // durable
		false, // autoDelete
		false, // exclusive
		false, // noWait
		nil,   // args
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
			log.Printf("[workerID=%s pod=%s] NOTIFY ← %s", workerID, podName, string(m.Body))
			if err := m.Ack(false); err != nil {
				log.Printf("[workerID=%s pod=%s] failed to ack message: %v", workerID, podName, err)
			}
			// simulating some delay
			time.Sleep(500 * time.Millisecond)
		}
		log.Printf("[workerID=%s pod=%s] messages channel closed (connection/channel ended)", workerID, podName)
		done <- struct{}{}
	}()

	// handle signals - KEDA scale-down
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGTERM, syscall.SIGINT)

	select {
	case sig := <-sigCh:
		log.Printf("[workerID=%s pod=%s] received signal: %s (likely scale-down or rollout), shutting down…", workerID, podName, sig)
	case <-done:
		log.Printf("[workerID=%s pod=%s] consumer loop finished; shutting down…", workerID, podName)
	}

	log.Printf("[workerID=%s pod=%s] notifications worker shutdown complete.", workerID, podName)
}
