#!/usr/bin/env bash
set -euo pipefail

# ============
# CONFIG
# ============
# External entry (your API gateway / ingress)
BASE_URL="${BASE_URL:-http://hireflow.local}"   # e.g., http://127.0.0.1:8080 or http://gateway.hireflow.local
AUTH="${AUTH:-}"                                 # e.g., "Authorization: Bearer <token>" if you’ve wired auth
RESUME_PATH="${RESUME_PATH:-./sample-resume.pdf}"

HEADERS=(-H "Content-Type: application/json")
if [[ -n "$AUTH" ]]; then
  HEADERS+=(-H "$AUTH")
fi

echo "== Milestone 1 • Happy Path =="

# 1) Create Company
COMPANY_PAYLOAD='{
  "name": "Wasting No Time Ltd.",
  "domain": "wastingnotime.org"
}'

COMPANY_JSON=$(
  curl -sS -X POST "$BASE_URL/companies" \
    "${HEADERS[@]}" \
    -d "$COMPANY_PAYLOAD"
)

COMPANY_ID=$(echo "$COMPANY_JSON" | jq -r '.id')
echo "Company created: $COMPANY_ID"

# 2) Create Recruiter (linked to company)
RECRUITER_PAYLOAD='{
  "name": "Henrique Riccio",
  "email": "henrique@wastingnotime.org"
}'

RECRUITER_JSON=$(
  curl -sS -X POST "$BASE_URL/companies/$COMPANY_ID/recruiters" \
   "${HEADERS[@]}" \
   -d "$RECRUITER_PAYLOAD"
)

RECRUITER_ID=$(echo "$RECRUITER_JSON" | jq -r '.id')
echo "Recruiter created: $RECRUITER_ID"

# 3) Create Job (draft)
JOB_PAYLOAD=$(jq -nc --arg cid "$COMPANY_ID" --arg rid "$RECRUITER_ID" '{
  "company_id": $cid,
  "title": "Senior Backend Engineer (Go/.NET)",
  "description": "Own critical services, distributed systems, reliability.",
  "requirements": ["Go", ".NET", "PostgreSQL", "K8s"],
  "status": "draft",
  "recruiter_id": $rid,
}')

JOB_JSON=$(
  curl -sS -X POST "$BASE_URL/jobs" \
    "${HEADERS[@]}" \
    -d "$JOB_PAYLOAD"
)

JOB_ID=$(echo "$JOB_JSON" | jq -r '.id')
echo "Job created: $JOB_ID"

# 4) Publish Job
JOB_PUB_JSON=$(
  curl -sS -X PATCH "$BASE_URL/jobs/$JOB_ID/publish" \
    "${HEADERS[@]}" 
)

echo "Job published: $(echo "$JOB_PUB_JSON" | jq -r '.status')"
