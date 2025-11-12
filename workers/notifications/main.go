package main

import (
  "log"
  "os"
  "github.com/rabbitmq/amqp091-go"
)

func main() {
  url := os.Getenv("RabbitMQ")
  if url == "" { url = "amqp://guest:guest@localhost:5672/" }
  conn, err := amqp091.Dial(url); if err != nil { log.Fatal(err) }
  defer conn.Close()
  ch, err := conn.Channel(); if err != nil { log.Fatal(err) }
  defer ch.Close()

  qName := "notifications.commands"
  _, _ = ch.QueueDeclare(qName, true, false, false, false, nil)
  msgs, err := ch.Consume(qName, "", false, false, false, false, nil); if err != nil { log.Fatal(err) }
  log.Println("Notifications worker up; waiting for messages…")
  for m := range msgs {
    log.Printf("NOTIFY ← %s", string(m.Body))
    m.Ack(false)
  }
}
