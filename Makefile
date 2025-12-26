# -------------------------------------------
# Config
# -------------------------------------------
NAMESPACE ?= hireflow
SERVICE_COMPANY_JOBS := company-jobs
SERVICE_COMPANY_JOBS_MIGRATOR := company-jobs-migrator
SERVICE_GATEWAY := gateway
SERVICE_CANDIDATES := candidates
SERVICE_APPLICATIONS := applications
SERVICE_IDENTITY := identity
SERVICE_SEARCH := search
SERVICE_NOTIFICATIONS := notifications

# Gateway base URL (through which we hit the happy path)
MINIIP := $(shell minikube ip)
HOST := hireflow.$(shell printf "%s" "$(MINIIP)" | tr . -).nip.io
GATEWAY_URL ?= http://$(HOST)

# -------------------------------------------
# Build
# -------------------------------------------
REGISTRY ?= hireflow
IMAGE_TAG ?= $(shell git rev-parse --short HEAD)
IMAGE_GATEWAY_API := $(REGISTRY)/$(SERVICE_GATEWAY):$(IMAGE_TAG)
IMAGE_COMPANY_JOBS_API := $(REGISTRY)/$(SERVICE_COMPANY_JOBS):$(IMAGE_TAG)
IMAGE_COMPANY_JOBS_MIGRATOR := $(REGISTRY)/$(SERVICE_COMPANY_JOBS_MIGRATOR):$(IMAGE_TAG)
IMAGE_CANDIDATES_API := $(REGISTRY)/$(SERVICE_CANDIDATES):$(IMAGE_TAG)
IMAGE_APPLICATIONS_API := $(REGISTRY)/$(SERVICE_APPLICATIONS):$(IMAGE_TAG)
IMAGE_SEARCH_API := $(REGISTRY)/$(SERVICE_SEARCH):$(IMAGE_TAG)
IMAGE_IDENTITY_API := $(REGISTRY)/$(SERVICE_IDENTITY):$(IMAGE_TAG)
IMAGE_NOTIFICATIONS := $(REGISTRY)/$(SERVICE_NOTIFICATIONS):$(IMAGE_TAG)

.PHONY: build build-gateway build-company-jobs-api build-company-jobs-migrator build-candidates-api build-applications-api build-search-api build-identity-api build-notifications-worker \
        logs-company-jobs logs-gateway logs-candidates logs-notifications logs-applications logs-search logs-identity \
		notifications-watch \
		ingress-patch \
        test-happy-path

build: build-gateway build-company-jobs-api build-company-jobs-migrator build-candidates-api build-applications-api build-search-api build-identity-api build-notifications-worker

MINIKUBE_DOCKER_ENV := eval "$$(minikube docker-env)"

build-gateway:
	@$(MINIKUBE_DOCKER_ENV) && \
	docker build \
	  -t $(IMAGE_GATEWAY_API) \
	  ./services/$(SERVICE_GATEWAY)

build-company-jobs-api:
	@$(MINIKUBE_DOCKER_ENV) && \
	docker build \
	  -t $(IMAGE_COMPANY_JOBS_API) \
	  ./services/$(SERVICE_COMPANY_JOBS)

build-company-jobs-migrator:
	@$(MINIKUBE_DOCKER_ENV) && \
	docker build \
	  -f ./services/company-jobs/Dockerfile.migrator \
	  -t $(IMAGE_COMPANY_JOBS_MIGRATOR) \
	  ./services/$(SERVICE_COMPANY_JOBS)

build-candidates-api:
	@$(MINIKUBE_DOCKER_ENV) && \
	docker build \
	  -t $(IMAGE_CANDIDATES_API) \
	  ./services/$(SERVICE_CANDIDATES)

build-applications-api:
	@$(MINIKUBE_DOCKER_ENV) && \
	docker build \
	  -t $(IMAGE_APPLICATIONS_API) \
	  ./services/$(SERVICE_APPLICATIONS)

build-search-api:
	@$(MINIKUBE_DOCKER_ENV) && \
	docker build \
	  -t $(IMAGE_SEARCH_API) \
	  ./services/$(SERVICE_SEARCH)

build-identity-api:
	@$(MINIKUBE_DOCKER_ENV) && \
	docker build \
	  -t $(IMAGE_IDENTITY_API) \
	  ./services/$(SERVICE_IDENTITY)

build-notifications-worker:
	@$(MINIKUBE_DOCKER_ENV) && \
	docker build \
	  -t $(IMAGE_NOTIFICATIONS) \
	  ./workers/$(SERVICE_NOTIFICATIONS)

# -------------------------------------------
# Debug helpers
# -------------------------------------------

logs-company-jobs:
	kubectl logs -n $(NAMESPACE) -l app.kubernetes.io/name=$(SERVICE_COMPANY_JOBS) -f --tail=200

logs-gateway:
	kubectl logs -n $(NAMESPACE) -l app.kubernetes.io/name=$(SERVICE_GATEWAY) -f --tail=200

logs-candidates:
	kubectl logs -n $(NAMESPACE) -l app.kubernetes.io/name=$(SERVICE_CANDIDATES) -f --tail=200

logs-notifications:
	kubectl logs -n $(NAMESPACE) -l app.kubernetes.io/name=$(SERVICE_NOTIFICATIONS) -f --tail=200

logs-applications:
	kubectl logs -n $(NAMESPACE) -l app.kubernetes.io/name=$(SERVICE_APPLICATIONS) -f --tail=200

logs-search:
	kubectl logs -n $(NAMESPACE) -l app.kubernetes.io/name=$(SERVICE_SEARCH) -f --tail=200

logs-identity:
	kubectl logs -n $(NAMESPACE) -l app.kubernetes.io/name=$(SERVICE_IDENTITY) -f --tail=200

#kubectl n $(NAMESPACE) exec -it deploy/notification -- bash

# ---------- Milestone 1: Happy Path ----------

# Path to the happy path script (from earlier)
HAPPY_PATH_SCRIPT ?= scripts/happy-path.sh
RESUME_PATH ?= ./misc/sample-resume.pdf

ingress-patch: ## updates ingress to understand that host and forward accordingly
	kubectl -n hireflow patch ingress gateway \
	--type='json' \
	-p='[{"op":"replace","path":"/spec/rules/0/host","value":"'$(HOST)'"}]'

test-happy-path: ## Run Milestone 1 happy path through the gateway
	@test -x $(HAPPY_PATH_SCRIPT) || { echo "Missing or not executable: $(HAPPY_PATH_SCRIPT)"; exit 1; }
	BASE_URL=$(GATEWAY_URL) RESUME_PATH=$(RESUME_PATH) bash $(HAPPY_PATH_SCRIPT)

# ---------- Milestone 2:  ----------

notifications-watch:
	watch -n 2 "kubectl get deploy notifications -n hireflow; echo; kubectl get hpa -n hireflow"


# -------------------------------------------
# EF Core Migrations
# -------------------------------------------

MIGRATIONS_PROJECT := services/company-jobs/WastingNoTime.HireFlow.CompanyJobs.Data
MIGRATIONS_STARTUP := services/company-jobs/WastingNoTime.HireFlow.CompanyJobs.Migrator

.PHONY: migrations migrations-add migrations-list migrations-remove

## Create a new migration:
##   make migrations-add NAME=InitialCreate
migrations-add:
	@if [ -z "$(NAME)" ]; then \
		echo "ERROR: Missing migration NAME. Usage:"; \
		echo "  make migrations-add NAME=AddJobsTable"; \
		exit 1; \
	fi
	@echo "🔧 Adding migration '$(NAME)'..."
	dotnet ef migrations add $(NAME) \
		--project $(MIGRATIONS_PROJECT) \
		--startup-project $(MIGRATIONS_STARTUP)
	@echo "✅ Migration added: $(NAME)"

## Remove the last migration
migrations-remove:
	@echo "🛠 Removing last migration..."
	dotnet ef migrations remove \
		--project $(MIGRATIONS_PROJECT) \
		--startup-project $(MIGRATIONS_STARTUP)
	@echo "✅ Last migration removed"

## List pending and applied migrations
migrations-list:
	@echo "📋 Listing migrations..."
	dotnet ef migrations list \
		--project $(MIGRATIONS_PROJECT) \
		--startup-project $(MIGRATIONS_STARTUP)

# -------------------------------------------
# Helm
# -------------------------------------------

RELEASE_COMPANY_JOBS := $(SERVICE_COMPANY_JOBS)
RELEASE_GATEWAY := $(SERVICE_GATEWAY)
RELEASE_CANDIDATES := $(SERVICE_CANDIDATES)
RELEASE_APPLICATIONS := $(SERVICE_APPLICATIONS)
RELEASE_IDENTITY := $(SERVICE_IDENTITY)
RELEASE_SEARCH := $(SERVICE_SEARCH)
RELEASE_NOTIFICATIONS := $(SERVICE_NOTIFICATIONS)

CHART_COMPANY_JOBS := deploy/helm/$(SERVICE_COMPANY_JOBS)
CHART_GATEWAY := deploy/helm/$(SERVICE_GATEWAY)
CHART_CANDIDATES := deploy/helm/$(SERVICE_CANDIDATES)
CHART_APPLICATIONS := deploy/helm/$(SERVICE_APPLICATIONS)
CHART_SEARCH := deploy/helm/$(SERVICE_SEARCH)
CHART_IDENTITY := deploy/helm/$(SERVICE_IDENTITY)
CHART_NOTIFICATIONS := deploy/helm/$(SERVICE_NOTIFICATIONS)

.PHONY: helm-deploy-company-jobs helm-deploy-gateway helm-deploy-candidates helm-deploy-applications helm-deploy-identity helm-deploy-search helm-deploy-notifications \
	helm-uninstall-company-jobs helm-uninstall-gateway helm-uninstall-candidates helm-uninstall-applications helm-uninstall-identity helm-uninstall-search helm-uninstall-notifications \
	helm-update-gateway helm-update-company-jobs helm-update-candidates helm-update-applications helm-update-identity helm-update-search helm-update-notifications \
	helm-status-company-jobs helm-status-gateway helm-status-candidates helm-status-applications helm-status-identity helm-status-search helm-status-notifications

helm-deploy: helm-deploy-gateway helm-deploy-company-jobs helm-deploy-candidates helm-deploy-applications helm-deploy-identity helm-deploy-search helm-deploy-notifications

helm-deploy-gateway:
	@$(MINIKUBE_DOCKER_ENV) && \
	helm upgrade --install $(RELEASE_GATEWAY) $(CHART_GATEWAY) -n $(NAMESPACE) \
		--set image.tag=${IMAGE_TAG}; \
	$(MAKE) ingress-patch;

helm-deploy-company-jobs:
	@$(MINIKUBE_DOCKER_ENV) && \
	helm upgrade --install $(RELEASE_COMPANY_JOBS) $(CHART_COMPANY_JOBS) -n $(NAMESPACE) \
		--set image.tag=${IMAGE_TAG} \
		--set migrator.image.tag=${IMAGE_TAG}

helm-deploy-candidates:
	@$(MINIKUBE_DOCKER_ENV) && \
	helm upgrade --install $(RELEASE_CANDIDATES) $(CHART_CANDIDATES) -n $(NAMESPACE) \
		--set image.tag=${IMAGE_TAG}

helm-deploy-applications:
	@$(MINIKUBE_DOCKER_ENV) && \
	helm upgrade --install $(RELEASE_APPLICATIONS) $(CHART_APPLICATIONS) -n $(NAMESPACE) \
		--set image.tag=${IMAGE_TAG}

helm-deploy-identity:
	@$(MINIKUBE_DOCKER_ENV) && \
	helm upgrade --install $(RELEASE_IDENTITY) $(CHART_IDENTITY) -n $(NAMESPACE) \
		--set image.tag=${IMAGE_TAG}

helm-deploy-search:
	@$(MINIKUBE_DOCKER_ENV) && \
	helm upgrade --install $(RELEASE_SEARCH) $(CHART_SEARCH) -n $(NAMESPACE) \
		--set image.tag=${IMAGE_TAG}

helm-deploy-notifications:
	@$(MINIKUBE_DOCKER_ENV) && \
	helm upgrade --install $(RELEASE_NOTIFICATIONS) $(CHART_NOTIFICATIONS) -n $(NAMESPACE) \
		--set image.tag=${IMAGE_TAG}

helm-update: helm-update-gateway helm-update-company-jobs helm-update-candidates helm-update-applications helm-update-identity helm-update-search helm-update-notifications

helm-update-gateway:
	helm dependency update $(CHART_GATEWAY) -n $(NAMESPACE)

helm-update-company-jobs:
	helm dependency update $(CHART_COMPANY_JOBS) -n $(NAMESPACE)

helm-update-candidates:
	helm dependency update $(CHART_CANDIDATES) -n $(NAMESPACE)

helm-update-applications:
	helm dependency update $(CHART_APPLICATIONS) -n $(NAMESPACE)

helm-update-identity:
	helm dependency update $(CHART_IDENTITY) -n $(NAMESPACE)

helm-update-search:
	helm dependency update $(CHART_SEARCH) -n $(NAMESPACE)

helm-update-notifications:
	helm dependency update $(CHART_NOTIFICATIONS) -n $(NAMESPACE)

helm-uninstall: helm-uninstall-gateway helm-uninstall-company-jobs helm-uninstall-candidates helm-uninstall-applications helm-uninstall-identity helm-uninstall-search helm-uninstall-notifications

helm-uninstall-gateway:
	helm uninstall $(RELEASE_GATEWAY) -n $(NAMESPACE) || true

helm-uninstall-company-jobs:
	helm uninstall $(RELEASE_COMPANY_JOBS) -n $(NAMESPACE) || true

helm-uninstall-candidates:
	helm uninstall $(RELEASE_CANDIDATES) -n $(NAMESPACE) || true

helm-uninstall-applications:
	helm uninstall $(RELEASE_APPLICATIONS) -n $(NAMESPACE) || true

helm-uninstall-identity:
	helm uninstall $(RELEASE_IDENTITY) -n $(NAMESPACE) || true

helm-uninstall-search:
	helm uninstall $(RELEASE_SEARCH) -n $(NAMESPACE) || true

helm-uninstall-notifications:
	helm uninstall $(RELEASE_NOTIFICATIONS) -n $(NAMESPACE) || true

helm-status-company-jobs:
	@echo "🔎 Helm release:"
	@helm status $(RELEASE_COMPANY_JOBS) -n $(NAMESPACE) || echo "No Helm release found"
	@echo
	@echo "🔎 Pods:"
	@kubectl get pods -n $(NAMESPACE) -l app.kubernetes.io/name=$(SERVICE_COMPANY_JOBS) || true

helm-status-gateway:
	@echo "🔎 Helm release:"
	@helm status $(RELEASE_GATEWAY) -n $(NAMESPACE) || echo "No Helm release found"
	@echo
	@echo "🔎 Pods:"
	@kubectl get pods -n $(NAMESPACE) -l app.kubernetes.io/name=$(SERVICE_GATEWAY) || true

helm-status-candidates:
	@echo "🔎 Helm release:"
	@helm status $(RELEASE_CANDIDATES) -n $(NAMESPACE) || echo "No Helm release found"
	@echo
	@echo "🔎 Pods:"
	@kubectl get pods -n $(NAMESPACE) -l app.kubernetes.io/name=$(SERVICE_CANDIDATES) || true

helm-status-applications:
	@echo "🔎 Helm release:"
	@helm status $(RELEASE_APPLICATIONS) -n $(NAMESPACE) || echo "No Helm release found"
	@echo
	@echo "🔎 Pods:"
	@kubectl get pods -n $(NAMESPACE) -l app.kubernetes.io/name=$(SERVICE_APPLICATIONS) || true

helm-status-identity:
	@echo "🔎 Helm release:"
	@helm status $(RELEASE_IDENTITY) -n $(NAMESPACE) || echo "No Helm release found"
	@echo
	@echo "🔎 Pods:"
	@kubectl get pods -n $(NAMESPACE) -l app.kubernetes.io/name=$(SERVICE_IDENTITY) || true

helm-status-search:
	@echo "🔎 Helm release:"
	@helm status $(RELEASE_SEARCH) -n $(NAMESPACE) || echo "No Helm release found"
	@echo
	@echo "🔎 Pods:"
	@kubectl get pods -n $(NAMESPACE) -l app.kubernetes.io/name=$(SERVICE_SEARCH) || true

helm-status-notifications:
	@echo "🔎 Helm release:"
	@helm status $(RELEASE_NOTIFICATIONS) -n $(NAMESPACE) || echo "No Helm release found"
	@echo
	@echo "🔎 Pods:"
	@kubectl get pods -n $(NAMESPACE) -l app.kubernetes.io/name=$(SERVICE_NOTIFICATIONS) || true

# -------------------------------------------
# curl: quick calls
# -------------------------------------------

JOB_ID ?= 1
COMPANY_ID ?= 1

.PHONY: api-healthz api-ready api-metrics api-metrics-forward \
	api-jobs-create api-jobs-publish api-jobs-get \
	api-companies-create api-companies-get api-companies-get-all \
	api-recruiters-get-all api-recruiters-create \
	api-notifications-spike

api-healthz:
	curl -s $(GATEWAY_URL)/healthz && echo

api-ready:
	curl -s $(GATEWAY_URL)/ready && echo

api-metrics:
	curl -s $(GATEWAY_URL)/metrics && echo

api-metrics-forward:
	curl -s http://localhost:18080/metrics

# # some domain calls you already have from M1
# curl -sS http://hireflow.local/companies
# curl -sS http://hireflow.local/candidates


api-companies-create:
	curl -sS -X POST $(GATEWAY_URL)/companies \
		-H "Content-Type: application/json" \
		-d '{"name":"Wasting No Time Ltd.","domain":"wastingnotime.org"}' | jq

api-jobs-create:
	curl -sS -X POST $(GATEWAY_URL)/jobs \
		-H "Content-Type: application/json" \
		-d '{"companyId":$(COMPANY_ID),"title":"Senior Backend Engineer (Go/.NET)","recruiterId":$(RECRUITER_ID)}' | jq

api-jobs-publish:
	curl -sS -X PATCH $(GATEWAY_URL)/jobs/$(JOB_ID)/publish | jq

api-jobs-get:
	curl -sS $(GATEWAY_URL)/jobs/$(JOB_ID) | jq

api-companies-get:
	curl -sS $(GATEWAY_URL)/companies/$(COMPANY_ID) | jq

api-companies-get-all:
	curl -sS $(GATEWAY_URL)/companies | jq

api-recruiters-get-all:
	curl -sS $(GATEWAY_URL)/companies/$(COMPANY_ID)/recruiters | jq

api-recruiters-create:
	curl -sS -X POST $(GATEWAY_URL)/companies/$(COMPANY_ID)/recruiters \
		-H "Content-Type: application/json" \
		-d '{"companyId":$(COMPANY_ID),"name": "Henrique Riccio", "email":"hriccio@wastingnotime.org"}' | jq

api-notifications-spike:
	@for i in $$(seq 1 100); do \
		curl -s -X POST "$(GATEWAY_URL)/applications/$(APPLICATION_ID)/interviews" \
			-H "Content-Type: application/json" \
			-d '{"scheduledAtUtc":"2025-12-13T15:00:00Z","durationMinutes":60,"location":"Google Meet"}' \
			> /dev/null; \
	done; \
	echo "Sent 100 notifications"

api-applications-trace:
	curl $(GATEWAY_URL)/applications/trace-ping


# -------------------------------------------
# MongoDB shell (hireflow namespace)
# -------------------------------------------

.PHONY: mongo-shell mongo-port-forward

## Open a shell inside the primary MongoDB pod with mongosh
mongo-shell:
	@echo "🔍 Looking for primary MongoDB pod..."
	@POD=$$(kubectl -n $(NAMESPACE) get pods -l app.kubernetes.io/component=mongodb -o jsonpath='{.items[0].metadata.name}'); \
	echo "📦 Connecting to pod: $$POD"; \
	kubectl -n $(NAMESPACE) exec -it $$POD -- bash -c 'mongosh -u root -p "$${MONGODB_ROOT_PASSWORD:-hireflowmongo}" --authenticationDatabase admin'

## Optional: port-forward MongoDB to localhost for external tools
mongo-port-forward:
	kubectl -n $(NAMESPACE) port-forward svc/mongo-mongodb 27017:27017


# -------------------------------------------
# RabbitMQ shell (hireflow namespace)
# -------------------------------------------

.PHONY: rabbitmq-port-forward rabbitmq-list-queues rabbitmq-send-broken-message rabbitmq-send-via-curl

rabbitmq-port-forward:
	kubectl -n hireflow port-forward svc/mq-rabbitmq 15672:15672

rabbitmq-list-queues:
	kubectl -n hireflow exec -it mq-rabbitmq-0 -- \
		rabbitmqctl list_queues name messages

rabbitmq-send-message:
	curl -u hireflow:hireflowpass -H "content-type:application/json" -X POST -d'{"properties":{"delivery_mode":2},"routing_key":"notifications.commands","payload":"{\"type\":\"SendEmail\",\"to\":\"someone\", \"message\":\"hello-from-M2\"}","payload_encoding":"string"}' http://localhost:15672/api/exchanges/%2f/amq.default/publish

rabbitmq-send-broken-message:
	curl -u hireflow:hireflowpass -H "content-type:application/json" -X POST -d'{"properties":{"delivery_mode":2},"routing_key":"notifications.commands","payload":"non_json_msg","payload_encoding":"string"}' http://localhost:15672/api/exchanges/%2f/amq.default/publish


## TODO: diagnose
# helm template company-jobs deploy/helm/company-jobs -n hireflow



# -------------------------------------------
# forwards
# -------------------------------------------
.PHONY: forward-jaeger forward-applications forward-notifications

forward-jaeger:
	kubectl port-forward -n observability svc/jaeger-query 16686:16686

forward-applications:
	kubectl -n hireflow port-forward svc/applications 18080:80

forward-notifications:
	kubectl -n hireflow port-forward deploy/notifications 18080:9090
