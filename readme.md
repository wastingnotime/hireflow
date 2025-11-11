# HireFlow

## overview

A lightweight Applicant Tracking System where companies create job campaigns, publish openings, receive applications (incl. LinkedIn “apply” simulators), auto-screen, schedule interviews, and track funnel analytics.

### solution (draft)

The solution diagram below is a non-strict diagram to give us the overview of services and its responsibilities.
For now, it still a draft of desirable outcome.
```mermaid
flowchart LR
  subgraph Web["SPA / BFF"]
    UI[SPA]
    BFF[BFF]
  end

  UI -->|JWT| BFF

  subgraph S1["Core services"]
    CJ[Company+Jobs API]
    APP[Applications API]
    CAN[Candidates API]
    INT[Interviews API]
  end

  subgraph S3["Generic services"]
    IDP[Identity]
  end

  subgraph S2["Async/Workers"]
    SCR[Screening/Scoring Worker]
    SCH[Interview Scheduler Worker]
    NOTIF[Notifications Worker]
    REP[Reporting ETL]
  end

  subgraph DS["Data Stores"]
    SQL[OLTP Database]
    NOSQL[Document Database]
    REDIS[Cache]
    BLOB[Blob - CVs]
    Q[Pub/Sub]
  end

  BFF --> CJ
  BFF --> APP
  BFF --> CAN
  BFF --> INT
  BFF --> IDP

  CJ --- SQL
  CAN --- SQL
  APP --- SQL
  INT --- SQL
  SCR --- Q
  SCH --- Q
  NOTIF --- Q
  REP --- NOSQL

  APP -->|Outbox| Q
  CJ -->|Domain events| Q
  CAN -->|Domain events| Q
  INT -->|Domain events| Q

  SCR --> APP
  SCH --> INT
  NOTIF --> BLOB
  CAN --> BLOB
  APP --> REDIS
  REP -->|Aggregates| NOSQL
```

### technology candidates (draft)

The candidates for implementation, for now, there is a great possibility of change. The list exists only as a roadmap.

- SPA: React Hooks
- BFF: .Net Minimal API
- Identity: .Net Minimal API/Duende
- Reporting ETL: .Net
- Interview Scheduler Worker: .Net
- Notifications Worker: .Net or Go
- Screening/Scoring Worker: .Net
- Company+Jobs API: .Net Minimal API
- Applications API: .Net Minimal API
- Candidates API: .Net Minimal API
- Interviews API: .Net Minimal API
- OLTP Database: SQL Server
- Cache: Redis
- NOSQL Database: MongoDB
- Blob: MinIO
- Pub/Sub: RabbitMQ


## approach

That is an MVP but to allow it to be easy understandable and manageable we split the MVP in milestones.

* Milestone 0 — Bootable skeleton <---- "WE ARE HERE"
    * Services: Identity, Company&Jobs, Candidates, Applications, Search, Notifications, Gateway.
    * Infra: kubernetes, RabbitMQ, SQL Server, Mongo, Redis, Blob.
    * CI/CD: build → test → Helm deploy → smoke tests.
* Milestone 1 — “Happy path”
    * Create company & recruiter → publish job → candidate applies (resume upload) → screening score → move to interview → schedule slot → send email.
* Milestone 2 — Scale & resiliency
    * KEDA scaling on queue depth; circuit breaker on Search; outbox pattern for Applications → Messaging; retries + DLQ viewer.
* Milestone 3 — Observability & security
    * Trace a request across gateway→apps→workers in Jaeger.
    * RBAC unit tests; PII encryption at rest; GDPR “export/delete me” job.


## starting guide (for Linux)

This guide was only tested on Linux Mint 21.3, but it should work on any Debian based distro.
For the adventurous, just keep in mind that any kubernetes cluster should behave same way in any OS that it could be installed, and once we are using docker it is very probable that everything gonna be all right.
Suggestion for the braves: if you think that makes sense, after taking note of your experience and ask us for a pull-request to include it here.

### pre-requirements

#### install helm

official site
https://helm.sh/docs/intro/install/

using APT - download and install
```bash
sudo apt-get install curl gpg apt-transport-https --yes
curl -fsSL https://packages.buildkite.com/helm-linux/helm-debian/gpgkey | gpg --dearmor | sudo tee /usr/share/keyrings/helm.gpg > /dev/null
echo "deb [signed-by=/usr/share/keyrings/helm.gpg] https://packages.buildkite.com/helm-linux/helm-debian/any/ any main" | sudo tee /etc/apt/sources.list.d/helm-stable-debian.list
sudo apt update
sudo apt install helm
```

including some well-know charts that we know we need in advance
```bash
# Helm repos
helm repo add bitnami https://charts.bitnami.com/bitnami
helm repo update

```

#### install Minikube

official site
https://minikube.sigs.k8s.io/docs/start/

download package and install
```bash
curl -LO https://storage.googleapis.com/minikube/releases/latest/minikube_latest_amd64.deb
sudo dpkg -i minikube_latest_amd64.deb
```

start Minikube (the parameters are only to ensure compatibility with this walkthrough)
```bash
minikube start \
  --driver=docker \
  --cpus=4 --memory=8192 --disk-size=40g \
  --kubernetes-version=v1.34.0
```

verify
```bash
minikube status
```

create alias (add it in you .bashrc in order to load automatically every time you boot your OS)
```bash
alias kubectl="minikube kubectl --"
```

verify cluster
```bash
kubectl get po -A
```

force Minikube use its own docker (for now let's do it every time we need and ensure it verifying on Minikube status)
```bash
eval $(minikube docker-env)
```


### configure kubernetes local cluster

include ingress - we gonna need it to allow requests from outside the cluster
```bash
minikube addons enable ingress
```

create namespace for HireFlow
```bash
kubectl create ns hireflow
```


### prepare minimum infra

here the common services that will be shared by microservices

```bash
# SQL Server
kubectl apply -f deploy/infra/mssql.yaml


# RabbitMQ - how should it be
# helm upgrade --install mq bitnami/rabbitmq -n hireflow \
#   --set auth.username=hireflow --set auth.password=hireflowpass \

# As confirmed by community reports (October 2025), Debian-based Bitnami images have been deprecated, but older Helm chart versions still reference them, causing ImagePullBackOff.


# RabbitMQ - workaround
helm upgrade --install mq bitnami/rabbitmq -n hireflow \
  --set auth.username=hireflow \
  --set auth.password=hireflowpass \
  --set image.repository=bitnamilegacy/rabbitmq \
  --set image.tag=4.1.3-debian-12-r1 \
  --set global.security.allowInsecureImages=true


# Redis (will be idle on M0)
helm upgrade --install redis bitnami/redis -n hireflow \
  --set architecture=standalone --set auth.enabled=false


# Mongo
helm upgrade --install mongo bitnami/mongodb -n hireflow \
  --set architecture=replicaset --set auth.rootPassword=hireflowmongo
```


### secrets (passwords, connection-strings, etc)

for now let's apply via console

app secrets (one shared secret)
```bash
kubectl -n hireflow create secret generic hireflow-connections \
  --from-literal=SqlServer="Server=mssql.hireflow.svc.cluster.local,1433;Database=hireflow;User ID=sa;Password=P@ssw0rd12345!;TrustServerCertificate=True" \
  --from-literal=RabbitMQ="amqp://hireflow:hireflowpass@mq-rabbitmq.hireflow.svc.cluster.local:5672/" \
  --from-literal=Mongo="mongodb://root:hireflowmongo@mongo-mongodb-0.mongo-mongodb-headless.hireflow.svc.cluster.local:27017/?replicaSet=rs0" \
  --from-literal=JwtSigningKey="dev_hmac_super_secret_change_me"
```

## misc

script to scaffold helm charts for new services: /scripts/new-chart.sh
