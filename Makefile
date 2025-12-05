# Makefile for Hireflow / company-jobs

# ---------- Config ----------
REGISTRY        ?= hireflow
NAMESPACE       ?= hireflow
IMAGE_TAG 		?= $(shell git rev-parse --short HEAD)


IMAGE_GATEWAY_API       	 := $(REGISTRY)/gateway:$(IMAGE_TAG)
IMAGE_COMPANY_JOBS_API       := $(REGISTRY)/company-jobs:$(IMAGE_TAG)
IMAGE_COMPANY_JOBS_MIGRATOR  := $(REGISTRY)/company-jobs-migrator:$(IMAGE_TAG)
IMAGE_CANDIDATES_API         := $(REGISTRY)/candidates:$(IMAGE_TAG)

CHART_COMPANY_JOBS := deploy/helm/company-jobs
CHART_GATEWAY := deploy/helm/gateway
CHART_CANDIDATES := deploy/helm/candidates

# Gateway base URL (through which we hit the happy path)
MINIIP := $(shell minikube ip)
HOST := hireflow.$(shell printf "%s" "$(MINIIP)" | tr . -).nip.io
GATEWAY_URL ?= http://$(HOST)

# Path to the happy path script (from earlier)
HAPPY_PATH_SCRIPT ?= scripts/happy-path.sh
RESUME_PATH       ?= ./misc/sample-resume.pdf

# ---------- Phony targets ----------
.PHONY: build build-ensure build-gateway build-company-jobs-api build-company-jobs-migrator build-candidates-api \
        logs-company-jobs logs-gateway logs-candidates \
		ingress-patch \
        test-happy-path

# ---------- Build ----------
MINIKUBE_DOCKER_ENV := eval "$$(minikube docker-env)"

build: build-gateway build-company-jobs-api build-company-jobs-migrator build-candidates-api ## Build all images

build-gateway:
	@$(MINIKUBE_DOCKER_ENV) && \
	docker build \
	  -t $(IMAGE_GATEWAY_API) \
	  ./services/gateway

build-company-jobs-api:
	@$(MINIKUBE_DOCKER_ENV) && \
	docker build \
	  -t $(IMAGE_COMPANY_JOBS_API) \
	  ./services/company-jobs

build-company-jobs-migrator:
	@$(MINIKUBE_DOCKER_ENV) && \
	docker build \
	  -f ./services/company-jobs/Dockerfile.migrator \
	  -t $(IMAGE_COMPANY_JOBS_MIGRATOR) \
	  ./services/company-jobs

build-candidates-api:
	@$(MINIKUBE_DOCKER_ENV) && \
	docker build \
	  -t $(IMAGE_CANDIDATES_API) \
	  ./services/candidates


# ---------- Debug helpers ----------

logs-company-jobs: ## Tail logs of company-jobs pods
	kubectl logs -n $(NAMESPACE) -l app.kubernetes.io/name=company-jobs -f --tail=200

logs-gateway: ## Tail logs of gateway pods
	kubectl logs -n $(NAMESPACE) -l app.kubernetes.io/name=gateway -f --tail=200

logs-candidates: ## Tail logs of candidates pods
	kubectl logs -n $(NAMESPACE) -l app.kubernetes.io/name=candidates -f --tail=200

# ---------- Milestone 1: Happy Path ----------

ingress-patch: ## updates ingress to understand that host and forward accordingly
	kubectl -n hireflow patch ingress gateway \
	--type='json' \
	-p='[{"op":"replace","path":"/spec/rules/0/host","value":"'$(HOST)'"}]'

test-happy-path: ## Run Milestone 1 happy path through the gateway
	@test -x $(HAPPY_PATH_SCRIPT) || { echo "Missing or not executable: $(HAPPY_PATH_SCRIPT)"; exit 1; }
	BASE_URL=$(GATEWAY_URL) RESUME_PATH=$(RESUME_PATH) bash $(HAPPY_PATH_SCRIPT)

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

RELEASE_COMPANY_JOBS     := company-jobs
RELEASE_GATEWAY          := gateway
RELEASE_CANDIDATES       := candidates

.PHONY: helm-deploy-company-jobs helm-deploy-gateway helm-deploy-candidates \
	helm-uninstall-company-jobs helm-uninstall-gateway helm-uninstall-candidates \
	helm-update-gateway helm-update-company-jobs helm-update-candidates
	helm-status-company-jobs helm-status-gateway helm-status-candidates

helm-deploy: helm-deploy-gateway helm-deploy-company-jobs helm-deploy-candidates

helm-deploy-gateway:
	@$(MINIKUBE_DOCKER_ENV) && \
	helm upgrade --install $(RELEASE_GATEWAY) $(CHART_GATEWAY) -n $(NAMESPACE) \
		--set image.tag=${IMAGE_TAG}

helm-deploy-company-jobs:
	@$(MINIKUBE_DOCKER_ENV) && \
	helm upgrade --install $(RELEASE_COMPANY_JOBS) $(CHART_COMPANY_JOBS) -n $(NAMESPACE) \
		--set image.tag=${IMAGE_TAG}

helm-deploy-candidates:
	@$(MINIKUBE_DOCKER_ENV) && \
	helm upgrade --install $(RELEASE_CANDIDATES) $(CHART_CANDIDATES) -n $(NAMESPACE) \
		--set image.tag=${IMAGE_TAG}

helm-update: helm-update-gateway helm-update-company-jobs helm-update-candidates

helm-update-gateway:
	helm dependency update $(CHART_GATEWAY) -n $(NAMESPACE)

helm-update-company-jobs:
	helm dependency update $(CHART_COMPANY_JOBS) -n $(NAMESPACE)

helm-update-candidates:
	helm dependency update $(CHART_CANDIDATES) -n $(NAMESPACE)


helm-uninstall: helm-uninstall-gateway helm-uninstall-company-jobs helm-uninstall-candidates

helm-uninstall-gateway:
	helm uninstall $(RELEASE_GATEWAY) -n $(NAMESPACE) || true

helm-uninstall-company-jobs:
	helm uninstall $(RELEASE_COMPANY_JOBS) -n $(NAMESPACE) || true

helm-uninstall-candidates:
	helm uninstall $(CHART_CANDIDATES) -n $(NAMESPACE) || true

helm-status-company-jobs:
	@echo "🔎 Helm release:"
	@helm status $(RELEASE_COMPANY_JOBS) -n $(NAMESPACE) || echo "No Helm release found"
	@echo
	@echo "🔎 Pods:"
	@kubectl get pods -n $(NAMESPACE) -l app.kubernetes.io/name=company-jobs || true

helm-status-gateway:
	@echo "🔎 Helm release:"
	@helm status $(RELEASE_GATEWAY) -n $(NAMESPACE) || echo "No Helm release found"
	@echo
	@echo "🔎 Pods:"
	@kubectl get pods -n $(NAMESPACE) -l app.kubernetes.io/name=gateway || true

helm-status-candidates:
	@echo "🔎 Helm release:"
	@helm status $(CHART_CANDIDATES) -n $(NAMESPACE) || echo "No Helm release found"
	@echo
	@echo "🔎 Pods:"
	@kubectl get pods -n $(NAMESPACE) -l app.kubernetes.io/name=candidates || true

# -------------------------------------------
# curl: quick calls
# -------------------------------------------

JOB_ID ?=1
COMPANY_ID ?=1
#RECRUITER_ID ?=1

.PHONY: api-healthz \
	api-jobs-create api-jobs-publish api-jobs-get \
	api-companies-create api-companies-get api-companies-get-all \
	api-recruiters-get-all api-recruiters-create

api-healthz:
	curl -s $(GATEWAY_URL)/healthz | jq

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


# -------------------------------------------
# MongoDB shell (hireflow namespace)
# -------------------------------------------

NAMESPACE ?= hireflow

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
