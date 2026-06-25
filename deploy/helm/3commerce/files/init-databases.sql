-- Creates one database + dedicated role per service (ADR-0008: hard isolation).
-- Runs once on first container start via /docker-entrypoint-initdb.d/.
-- Local-dev credentials only — never reuse in deployed environments.

\set ON_ERROR_STOP on

CREATE ROLE identity_svc    LOGIN PASSWORD 'identity_dev';
CREATE ROLE catalog_svc     LOGIN PASSWORD 'catalog_dev';
CREATE ROLE entity_svc      LOGIN PASSWORD 'entity_dev';
CREATE ROLE ordering_svc    LOGIN PASSWORD 'ordering_dev';
CREATE ROLE payments_svc    LOGIN PASSWORD 'payments_dev';
CREATE ROLE fulfillment_svc LOGIN PASSWORD 'fulfillment_dev';
CREATE ROLE support_svc     LOGIN PASSWORD 'support_dev';
CREATE ROLE marketing_svc   LOGIN PASSWORD 'marketing_dev';
CREATE ROLE pricing_svc   LOGIN PASSWORD 'pricing_dev';
CREATE ROLE audit_svc   LOGIN PASSWORD 'audit_dev';
CREATE ROLE workflow_svc   LOGIN PASSWORD 'workflow_dev';

CREATE DATABASE identity_db    OWNER identity_svc;
CREATE DATABASE catalog_db     OWNER catalog_svc;
CREATE DATABASE entity_db      OWNER entity_svc;
CREATE DATABASE ordering_db    OWNER ordering_svc;
CREATE DATABASE payments_db    OWNER payments_svc;
CREATE DATABASE fulfillment_db OWNER fulfillment_svc;
CREATE DATABASE support_db     OWNER support_svc;
CREATE DATABASE marketing_db   OWNER marketing_svc;
CREATE DATABASE pricing_db   OWNER pricing_svc;
CREATE DATABASE audit_db   OWNER audit_svc;
CREATE DATABASE workflow_db   OWNER workflow_svc;

-- Extensions that must be created by a superuser, per database that needs them.
-- (pg_trgm for catalog search arrives in Phase 2; created here so migrations need no superuser.)
\connect catalog_db
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS citext;

\connect identity_db
CREATE EXTENSION IF NOT EXISTS citext;
