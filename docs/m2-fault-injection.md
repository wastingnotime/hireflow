## Fault Injection Playbook — Hireflow

### Conventions

* Namespace: `hireflow`
* Observe live:

  * Gateway logs: `kubectl logs -n hireflow -l app.kubernetes.io/name=gateway -f --tail=100`
  * Worker logs: `kubectl logs -n hireflow -l app.kubernetes.io/name=notifications -f --tail=200`
  * Applications logs: `kubectl logs -n hireflow -l app.kubernetes.io/name=applications -f --tail=200`
  * Companies/Jobs logs: `kubectl logs -n hireflow -l app.kubernetes.io/name=company-jobs -f --tail=200`
  * RabbitMQ queue depth (optional): `kubectl get scaledobject -n hireflow`
  * Jaeger: confirm trace continuity for each scenario

---

## Base Host Resolution (portable)

All examples use a dynamic hostname derived from the local Minikube IP.

```bash
# Resolve Minikube IP
MINIIP=$(minikube ip)

# Generate nip.io host
HOST="hireflow.$(echo "$MINIIP" | tr . -).nip.io"

# Sanity check
curl -fsS "http://$HOST/healthz"
```

Use `$HOST` in all subsequent commands.


# A) Pod-level faults

## A1) Kill a single pod (crash simulation)

**Goal:** prove Kubernetes self-heals + gateway/clients behave predictably.

```bash
kubectl get pods -n hireflow
kubectl delete pod -n hireflow <pod-name>
```

**Expected**

* Deployment recreates pod
* Temporary errors:

  * gateway may return 502/503 depending on which service died + policy state
* Jaeger:

  * for gateway calls during downtime: gateway span shows forwarder error / circuit open
* Logs:

  * gateway emits forwarder warnings
  * service restarts cleanly

---

## A2) Scale a deployment to zero (hard outage)

**Goal:** prove circuit breaker + error mapping works.

```bash
kubectl scale deploy -n hireflow company-jobs --replicas=0
curl -i "http://$HOST/jobs/1"
```

**Expected**

* First calls: forwarder errors/timeouts → mapped by gateway to 502/504
* After threshold: **circuit-open** → mapped to 503 + header
* No downstream spans (because service is down)

Recover:

```bash
kubectl scale deploy -n hireflow company-jobs --replicas=1
```

---

## A3) Slow pod (latency injection)

**Goal:** gateway timeout cancels request; downstream honors abort.

In `company-jobs` endpoint, use:

```csharp
await Task.Delay(10_000, HttpContext.RequestAborted);
```

Then:

```bash
curl -i "http://$HOST/jobs/1"
```

**Expected**

* Gateway returns 504 (or your mapped timeout)
* Jaeger:

  * gateway span ~timeout budget
  * company-jobs span ends early (aborted), not 10s
* company-jobs logs mention aborted request (optional)

---

# B) Database faults (SQL Server + MongoDB)

## B1) SQL Server outage (company-jobs)

**Goal:** prove EF/DB failure surfaces, breaker opens on persistent DB outage.

Option 1 — scale SQL down (if it’s a deploy/statefulset you control):

```bash
kubectl get deploy,statefulset -n hireflow | grep mssql
kubectl scale statefulset -n hireflow mssql --replicas=0
```

Option 2 — kill the pod:

```bash
kubectl delete pod -n hireflow -l app.kubernetes.io/name=mssql
```

Test:

```bash
curl -i "http://$HOST/jobs/1"
```

**Expected**

* company-jobs returns 500/503 depending on your exception handling
* gateway sees 5xx responses (normal) OR forwarding failure (if connection breaks)
* breaker behavior:

  * if you configured breaker to treat **503/502/504** as failures, it should open
* Jaeger:

  * DB spans show failure (if the request reaches DB layer)
  * or client spans show connection exceptions

Recover: scale back to 1 replica.

---

## B2) MongoDB outage (applications)

**Goal:** prove outbox + API behavior under persistence failures.

```bash
kubectl delete pod -n hireflow -l app.kubernetes.io/name=mongodb
```

Test:

```bash
curl -i -X POST "http://$HOST/applications/<id>/interviews ...
```

**Expected**

* applications returns 500/503 (db unavailable)
* No outbox publish happens (because request fails earlier)
* Jaeger shows Mongo spans failing (if instrumented)
* Gateway mapping works as configured

---

# C) Broker faults (RabbitMQ)

## C1) RabbitMQ outage while publishing (Applications Outbox dispatcher)

**Goal:** outbox keeps retrying; requests still succeed (because publish is async).

```bash
kubectl delete pod -n hireflow -l app.kubernetes.io/name=rabbitmq
```

Trigger an interview scheduled flow (HTTP request that writes outbox).

**Expected**

* HTTP request succeeds (outbox written to Mongo)
* Outbox dispatcher logs:

  * “Outbox send failed attempt=x/y”
  * keeps retrying
* Jaeger:

  * HTTP trace ends OK
  * separate background activity may show errors
* When Rabbit returns:

  * dispatcher publishes backlog
  * worker consumes messages

---

## C2) RabbitMQ outage while consuming (Notifications worker)

**Goal:** worker crashes/reconnects; KEDA scales behavior ok.

Kill RabbitMQ:

```bash
kubectl delete pod -n hireflow -l app.kubernetes.io/name=rabbitmq
```

**Expected**

* worker logs connection/channel failure (depending on your code; currently it likely exits on dial failure)
* deployment restarts the worker
* queue backlog persists because queue is durable (once Rabbit returns)

(If you want a more “production-like” worker later, we can add reconnect loop — but Milestone 2 can keep fail-fast.)

---

## C3) Poison message → DLQ

**Goal:** invalid JSON causes DLQ path; trace/log includes message_id + correlation.

Publish a malformed payload (or temporarily alter publisher):

* Send message with `Body="not json"`
* Keep `MessageId` and `CorrelationId`

**Expected**

* worker:

  * logs “invalid json; dlq”
  * `Nack(requeue:false)` routes to DLQ
* Jaeger:

  * consumer span has error status
* DLQ grows

---

## C4) Retry + backoff works (transient handler failures)

**Goal:** show 2 failures + 1 success.

Your worker already simulates this:

* attempt 1/2 fail
* attempt 3 succeeds

**Expected**

* Jaeger: `notifications.handle` parent + 3 `notifications.attempt` children
* Logs show same trace_id across attempts
* No DLQ

---

# D) KEDA / scale-to-zero behaviors (worker)

## D1) Scale worker to zero (idle)

**Goal:** show SIGTERM is expected, not a crash.

Wait until queue empty and KEDA scales down, or force:

```bash
kubectl scale deploy -n hireflow notifications --replicas=0
```

**Expected**

* worker logs “shutdown requested … reason=context canceled”
* no error spam
* when new message arrives and KEDA scales up: worker starts cleanly