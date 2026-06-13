# 1. Executive Summary

## Product overview

3commerce is a from-scratch e-commerce platform for selling physical goods drawn from large third-party catalogs (thousands of SKUs). It is deliberately built as a **distributed system** — six C# microservices with asynchronous, event-driven communication — because the project serves two goals at once: it must become a real, launchable store, and the act of building it must teach production-grade distributed-systems engineering (sagas, outbox, eventual consistency, service data ownership).

Customers browse a searchable catalog rendered by a server-side-rendered Next.js storefront, buy as guests or with optional accounts, pay by card via Stripe, and can open order-linked support tickets including a structured return/refund (RMA) flow. Operators manage the catalog, orders, RMA approvals, and supplier feed health from a Blazor Server admin application. All money movements are recorded in a custom double-entry ledger — the system of record — which posts daily summary journals and per-refund detail to Xero for accounting.

Two business facts shape the v1 posture: **no supplier is signed yet** (so the catalog is built on a neutral internal schema with a pluggable importer interface and seeded sample data) and **no legal entity exists yet** (so the platform runs end-to-end on Stripe test mode, with currency and tax strategy kept configurable until registration).

## Core value proposition

- **For shoppers:** fast, polished storefront with guest checkout, transparent order tracking, and a self-service refund flow.
- **For the operator:** one place to run catalog, orders, refunds, and accounting sync without manual bookkeeping.
- **For the builder:** every hard distributed-systems pattern (saga orchestration, transactional outbox, per-service data, idempotent consumers, edge-vs-internal auth) implemented hands-on, in code that is intended to take real traffic later.

## MVP goal statement

Deliver a fully functional store on test payment rails: a shopper can find a product among ≥ 10k seeded SKUs, check out as a guest, pay with a Stripe test card, receive order emails, and successfully request and receive a refund — with every step flowing through the six services via RabbitMQ, every cent accounted for in a balanced double-entry ledger, and accounting entries landing in Xero.
