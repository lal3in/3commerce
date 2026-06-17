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
