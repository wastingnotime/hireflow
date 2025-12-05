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

* Milestone 0 — Bootable skeleton
    * Services: Identity, Company&Jobs, Candidates, Applications, Search, Notifications, Gateway.
    * Infra: kubernetes, RabbitMQ, SQL Server, Mongo, Redis, Blob.
    * CI/CD: build → test → Helm deploy → smoke tests.
* Milestone 1 — “Happy path” **<---- "WE ARE HERE"**
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


### important

start from [bootable-skeleton](https://github.com/wastingnotime/hireflow/tree/v0.1.0-m0-bootable-skeleton)  
this documentation is complementar

### milestone 1  - happy flow

```mermaid
flowchart LR
  subgraph Candidate Side
    C[Candidate\napply + upload resume]
  end

  subgraph Gateway/Ingress
    G[API Gateway]
  end

  subgraph Core Services
    CO[Company Svc]
    R[Recruiter Svc]
    J[Job Svc]
    Can[Candidate Svc]
    A[Application Svc]
    S[Screening Svc]
    I[Interview Svc]
    N[Notification Svc]
  end

  subgraph Infra
    DB[(PostgreSQL)]
    OBJ[(MinIO/S3\nresumes/attachments)]
    Q[(Event Bus\nNATS/RabbitMQ)]
    SMTP[(SMTP Mock\nMailHog)]
  end

  C -->|HTTP| G
  G --> CO
  G --> R
  G --> J
  G --> Can
  G --> A
  G --> S
  G --> I
  G --> N

  CO --- DB
  R --- DB
  J --- DB
  Can --- DB
  A --- DB
  S --- DB
  I --- DB
  N --- DB

  A -->|multipart upload| OBJ
  S -->|sync score or event| A
  A -->|stage=interview -> event| Q
  Q --> I
  I -->|schedule| DB
  N -->|SMTP| SMTP
```


### pre-requirements


#### start Minikube


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

verify cluster
```bash
kubectl get po -A
```

#### update secrets (passwords, connection-strings, etc)

update secrects
```bash
kubectl -n hireflow create secret generic hireflow-connections \
  --from-literal=SqlServer='Server=mssql.hireflow.svc.cluster.local,1433;Database=hireflow;User ID=sa;Password=P@ssw0rd12345!;TrustServerCertificate=True' \
  --from-literal=RabbitMQ='amqp://hireflow:hireflowpass@mq-rabbitmq.hireflow.svc.cluster.local:5672/' \
  --from-literal=Mongo='mongodb://root:hireflowmongo@mongo-mongodb-0.mongo-mongodb-headless.hireflow.svc.cluster.local:27017/?replicaSet=rs0' \
  --from-literal=JwtSigningKey='dev_hmac_super_secret_change_me' \
  --dry-run=client -o yaml | kubectl apply -f -
```


#### update application services

build and deploy the services
```bash
# build each app
make build

# build update helm definitions (chart of company has changed)
make helm-update

# deploy them on the cluster
make helm-deploy

# call ingress patch after every gateway upgrade (todo: add to helm-deploy flow)
make ingress-patch
```


verify if all the pods
```bash
kubectl -n hireflow get pods
```

test
```bash
make test-happy-path
```



## behind the scenes (just recording some steps used during preparation)

### happy path - what need to be done

migration tools dependency
```
dotnet tool update --global dotnet-ef
dotnet tool install --global dotnet-ef --version 9.0.11
```

create the first migration
```
export COMPANYJOBS_CONNECTION_STRING='Server=mssql.hireflow.svc.cluster.local,1433;Database=hireflow;User ID=sa;Password=P@ssw0rd12345!;TrustServerCertificate=True'

dotnet ef migrations add InitialCreate \
  --project services/company-jobs/WastingNoTime.HireFlow.CompanyJobs.Data \
  --startup-project services/company-jobs/WastingNoTime.HireFlow.CompanyJobs.Migrator
```

check database
```
kubectl -n hireflow exec -it deploy/mssql -- bash


/opt/mssql-tools18/bin/sqlcmd \
  -S localhost \
  -U sa \
  -P 'P@ssw0rd12345!' \
  -d hireflow \
  -C


SELECT * FROM companyjobs.__EFMigrationsHistory;
GO

SELECT Id, CompanyId, Title, Status FROM companyjobs.jobs;
GO
```

second migration
```bash
make migrations-add NAME=AddRecruiters COMPANYJOBS_CONNECTION_STRING='Server=mssql.hireflow.svc.cluster.local,1433;Database=hireflow;User ID=sa;Password=P@ssw0rd12345!;TrustServerCertificate=True'
```


#### quick test 
a lot of targets on make file, or run the full test

#### check nosql

connect to container
```bash
kubectl -n hireflow exec -it mongo-mongodb-0 -- bash
```

access database
```bash
mongosh -u root -p hireflowmongo
```

query
```mongo
use hireflow_candidates

db.applications.find().pretty()
```

to allow access from host (basic foward)
```bash
kubectl -n hireflow port-forward svc/mongo-mongodb 27017:27017
```


#### troubleshooting

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
