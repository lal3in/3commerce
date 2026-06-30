# Messaging Security and Privacy Runbook

Applies to the RabbitMQ operational bus, Kafka durable stream lane, stream outbox relay, and Quartz scheduler.

## Kafka stream lane guardrails

- `Restricted` payloads must not be published. Application code enforces this through `StreamPrivacyGuard` in producers and the stream outbox stager.
- Tenant-scoped topics require `tenantId` metadata before staging or publishing. Platform-global exceptions must be explicit and currently limited to workflow run style operational topics.
- Stream payloads must be minimized. Use labels, ids, hashes, and references instead of raw values.
- Never publish card PAN/CVV/expiry, raw bank details, provider secrets, password/session/token hashes, raw IP addresses, or request/response bodies.
- PII-adjacent topics require `Confidential` classification and explicit topic approval before use.

## Broker access policy

Production Kafka should be managed or separately hardened. Configure through Helm values/env only:

- `kafka.enabled=true`
- `kafka.deploy=false`
- `kafka.externalBootstrapServers=<managed-bootstrap>`
- `kafka.securityProtocol=SASL_SSL` or platform equivalent
- `kafka.saslMechanism=PLAIN`/`SCRAM-SHA-512` as provided
- `kafka.saslSecret.name=<secret>` with `SaslUsername` and `SaslPassword`

Minimum ACL shape:

| Principal | Allowed actions |
|---|---|
| Producing service relay | Write to owned topic(s), describe cluster/topic |
| Replay/projection consumer | Read from required topic(s), describe group/topic |
| Operator tooling | Read DLQ and committed topics only through audited break-glass access |

Do not grant wildcard write ACLs to app services in production.

## RabbitMQ operational bus

RabbitMQ remains internal-only. Keep management UI private. Do not expose RabbitMQ or Kafka through the public YARP gateway.

## Quartz jobs

Quartz jobs are typed code only. Do not add admin-defined SQL/shell jobs. Jobs that touch tenant-owned state must establish tenant scope in the target service command/handler, not by cross-service DB reads.

## Tests

Representative contract tests assert:

- `Restricted` stream envelopes are rejected before publish/staging.
- tenant-scoped stream topics reject missing `tenantId` metadata.
- audit stream payloads are hash/reference oriented and do not include raw sensitive field values.
- domain stream fact payloads do not contain obvious card/secret terms.
