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


### application services

build the services
```bash
# ensure using minikube's docker
eval $(minikube docker-env)

# build each app
docker build -t hireflow/gateway:local ./services/gateway
docker build -t hireflow/identity:local ./services/identity
docker build -t hireflow/company-jobs:local ./services/company-jobs
docker build -t hireflow/candidates:local ./services/candidates
docker build -t hireflow/applications:local ./services/applications
docker build -t hireflow/search:local ./services/search
docker build -t hireflow/notifications:local ./workers/notifications
```


for the first time, before deploy services in the cluster, we need to run dep update in order of helm resolve the config inheritance we're using
```bash
helm dependency update deploy/helm/gateway
helm dependency update deploy/helm/identity
helm dependency update deploy/helm/company-jobs
helm dependency update deploy/helm/candidates
helm dependency update deploy/helm/applications
helm dependency update deploy/helm/search
helm dependency update deploy/helm/notifications
```


check if the charts are ok
```bash
helm lint deploy/helm/gateway
helm lint deploy/helm/identity
helm lint deploy/helm/company-jobs
helm lint deploy/helm/candidates
helm lint deploy/helm/applications
helm lint deploy/helm/search
helm lint deploy/helm/notifications
```


deploy the services
```bash
helm upgrade --install gateway       deploy/helm/gateway       -n hireflow
helm upgrade --install identity      deploy/helm/identity      -n hireflow
helm upgrade --install company-jobs  deploy/helm/company-jobs  -n hireflow
helm upgrade --install candidates    deploy/helm/candidates    -n hireflow
helm upgrade --install applications  deploy/helm/applications  -n hireflow
helm upgrade --install search        deploy/helm/search        -n hireflow
helm upgrade --install notifications deploy/helm/notifications -n hireflow
```


verify if all the pods
```bash
kubectl -n hireflow get pods
```


lets allow ingress receive api requests and calls
```bash
# get entry ip
MINIIP=$(minikube ip)

# create a new host name based on minikube ip
HOST="hireflow.$(echo $MINIIP | tr . -).nip.io"

# updates ingress to understand that host and forward accordingly
kubectl -n hireflow patch ingress gateway \
  --type='json' \
  -p='[{"op":"replace","path":"/spec/rules/0/host","value":"'"$HOST"'"}]'
```


### api - smoke tests

open a new shell
```bash
kubectl -n hireflow logs -l app.kubernetes.io/name=gateway -f
```

open another new shell
```bash
kubectl -n hireflow logs -l app.kubernetes.io/name=company-jobs -f
```


let's call our services and check if they are working
```bash
# call our health check endpoint
curl -s http://$HOST/healthz

# simulate creating a company via gateway → company-jobs
curl -s -X POST http://$HOST/api/companies -H "Content-Type: application/json" \
  -d '{"name":"Acme Inc."}'


# simulate creating a job (just title & companyId)
curl -s -X POST http://$HOST/api/jobs -H "Content-Type: application/json" \
  -d '{"companyId":"111111-111111-111111-111111","title":"Senior Software Engineer"}'
```

we should see no errors on gateway and company-jobs services.


### worker - smoke test

in a new shell watch notifications pod logs
```bash
kubectl -n hireflow logs -l app.kubernetes.io/name=notifications -f
```

let's send a manual message for our notifications service
```bash
# port-forward allowing pod receives the message from host
kubectl -n hireflow port-forward svc/mq-rabbitmq 15672:15672 5672:5672

# sending
curl -u hireflow:hireflowpass -H "content-type:application/json" -X POST -d'{"properties":{"delivery_mode":2},"routing_key":"notifications.commands","payload":"{\"type\":\"Test\",\"message\":\"hello-from-M0\"}","payload_encoding":"string"}' http://localhost:15672/api/exchanges/%2f/amq.default/publish
```


## troubleshooting

for now just keeping some useful commands at hand

check helm deploys
```bash
helm ls -n hireflow
```

check individual last status
```bash
helm status gateway -n hireflow
```

check if all the pods for hireflow are ok
```bash
kubectl -n hireflow get pods
```

check a specific pod
```bash
kubectl describe pod  gateway-68fc54c6fc-w874c -n hireflow
```

check logs of a specif application (label)
```bash
kubectl -n hireflow logs -l app.kubernetes.io/name=notifications
```


## misc

script to scaffold helm charts for new services: /scripts/new-chart.sh
