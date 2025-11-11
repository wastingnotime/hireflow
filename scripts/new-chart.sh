#!/usr/bin/env bash
set -euo pipefail
NAME="$1" ; DEST="deploy/helm/$NAME"
mkdir -p "$DEST/templates"
cat > "$DEST/Chart.yaml" <<EOF
apiVersion: v2
name: $NAME
description: HireFlow - $NAME
type: application
version: 0.1.0
appVersion: "local"
dependencies:
  - name: hf-base
    version: 0.1.0
    repository: "file://../hf-base"
EOF
cat > "$DEST/values.yaml" <<'EOF'
image:
  repository: hireflow/REPLACE
  tag: local
  pullPolicy: IfNotPresent
replicaCount: 1
service:
  type: ClusterIP
  port: 80
  targetPort: 8080
env:
  ASPNETCORE_URLS: "http://0.0.0.0:8080"
secretRefName: "hireflow-connections"
resources:
  requests: { cpu: 100m, memory: 128Mi }
  limits:   { cpu: 500m, memory: 512Mi }
ingress:
  enabled: false
  className: nginx
  host: hireflow.localtest.me
  annotations: {}
  path: /
  pathType: Prefix
EOF
# deployment
cat > "$DEST/templates/deployment.yaml" <<'EOF'
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ include "hf.fullname" . }}
  labels: {{- include "hf.labels" . | nindent 4 }}
spec:
  replicas: {{ .Values.replicaCount }}
  selector:
    matchLabels: {{- include "hf.labels" . | nindent 6 }}
  template:
    metadata:
      labels: {{- include "hf.labels" . | nindent 8 }}
    spec:
      containers:
      - name: {{ include "hf.name" . }}
        image: "{{ .Values.image.repository }}:{{ .Values.image.tag }}"
        imagePullPolicy: {{ .Values.image.pullPolicy }}
        ports: [{ containerPort: {{ .Values.service.targetPort }}, name: http }]
        envFrom: [{ secretRef: { name: {{ .Values.secretRefName | quote }} } }]
        env:
          {{- range $k, $v := .Values.env }}
          - name: {{ $k }} ; value: "{{ $v }}"
          {{- end }}
        readinessProbe: { httpGet: { path: /healthz, port: http }, initialDelaySeconds: 3, periodSeconds: 5 }
        livenessProbe:  { httpGet: { path: /livez,  port: http }, initialDelaySeconds: 10, periodSeconds: 10 }
        resources: {{- toYaml .Values.resources | nindent 10 }}
EOF
# service
cat > "$DEST/templates/service.yaml" <<'EOF'
apiVersion: v1
kind: Service
metadata:
  name: {{ include "hf.fullname" . }}
  labels: {{- include "hf.labels" . | nindent 4 }}
spec:
  type: {{ .Values.service.type }}
  ports:
    - name: http
      port: {{ .Values.service.port }}
      targetPort: {{ .Values.service.targetPort }}
  selector: {{- include "hf.labels" . | nindent 4 }}
EOF
# ingress
cat > "$DEST/templates/ingress.yaml" <<'EOF'
{{- if .Values.ingress.enabled }}
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ include "hf.fullname" . }}
  annotations: {{- toYaml .Values.ingress.annotations | nindent 4 }}
spec:
  ingressClassName: {{ .Values.ingress.className }}
  rules:
    - host: {{ .Values.ingress.host }}
      http:
        paths:
          - path: {{ .Values.ingress.path }}
            pathType: {{ .Values.ingress.pathType }}
            backend:
              service: { name: {{ include "hf.fullname" . }}, port: { number: {{ .Values.service.port }} } }
{{- end }}
EOF
echo "Chart created at $DEST (remember to set values.image.repository)"
