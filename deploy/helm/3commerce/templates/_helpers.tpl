{{- define "3commerce.labels" -}}
app.kubernetes.io/part-of: 3commerce
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
{{- end -}}

{{- /* image ref: takes a dict {root, name} */ -}}
{{- define "3commerce.image" -}}
{{- .root.Values.image.repoPrefix }}/{{ .name }}:{{ .root.Values.image.tag }}
{{- end -}}

{{- /* DB connection string for a service (dev creds from init SQL) */ -}}
{{- define "3commerce.conn" -}}
Host=postgres;Port=5432;Database={{ . }}_db;Username={{ . }}_svc;Password={{ . }}_dev
{{- end -}}

{{- /* Release identity (ADR-0064): the deployment environment on every OTel resource. Takes the root context. */ -}}
{{- define "3commerce.otelResourceEnv" -}}
- name: OTEL_RESOURCE_ATTRIBUTES
  value: {{ printf "deployment.environment.name=%s" .Values.deploymentEnvironment | quote }}
{{- end -}}

{{- /* OTLP export (ADR-0065, audit P0-5): every .NET process pushes to the in-chart collector, which owns the
       backend endpoints, tenant header and auth. `observability.otlp.headersSecret` names a Secret holding
       OTEL_EXPORTER_OTLP_HEADERS, only for a collector that demands auth from its clients (empty by default — the
       collector is cluster-internal). Takes the root context; renders nothing when observability is off. */ -}}
{{- define "3commerce.otlpEnv" -}}
{{- if .Values.observability.enabled }}
- name: OTEL_EXPORTER_OTLP_ENDPOINT
  value: {{ .Values.observability.otlp.endpoint | quote }}
{{- with .Values.observability.otlp.headersSecret }}
- name: OTEL_EXPORTER_OTLP_HEADERS
  valueFrom:
    secretKeyRef:
      name: {{ . }}
      key: OTEL_EXPORTER_OTLP_HEADERS
{{- end }}
{{- end }}
{{- end -}}

{{- /* Error inbox (ADR-0067): SENTRY_DSN from `errorTracking.dsnSecret` (key `backend` for every .NET process,
       `storefront` for the storefront). `optional: true`, so a missing Secret or key leaves error tracking OFF rather
       than failing the pod. Renders nothing when no Secret is named. Takes a dict {root, key}. */ -}}
{{- define "3commerce.errorTrackingEnv" -}}
{{- with .root.Values.errorTracking.dsnSecret }}
- name: SENTRY_DSN
  valueFrom:
    secretKeyRef:
      name: {{ . }}
      key: {{ $.key }}
      optional: true
{{- end }}
{{- end -}}
