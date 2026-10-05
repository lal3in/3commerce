# AGENTS.md Template

A flexible template for creating global rules. Adapt sections based on your project type.

---

# AGENTS.md

This file provides guidance to AI Agents when working with code in this repository.

## Project Overview

<!-- What is this project? One paragraph description -->

{Project description and purpose}

---

## Collaboration protocol (always follow)
- Ask clarifying questions when requirements are ambiguous.
- Prefer small, incremental changes over large rewrites.
- Always provide verification steps (tests run, commands, expected output).
- If a task references product scope/UX/requirements: consult PRD (see below) and produce a Working Brief before coding.

---

## Sources of truth (do NOT auto-load PRD)
- Product requirements: ./docs/prd/PRD.md (load only if task depends on requirements)
- Architecture decisions: ./docs/adr/
- API contracts: ./docs/api/

### PRD Loading Rule
Only read PRD sections when the task involves:
- new feature implementation,
- changes to user flows,
- acceptance criteria / scope questions,
- rollout
- telemetry/metrics requirements.

When PRD is needed:
1) Read only the relevant PRD sections.
2) Write a short "Working Brief" in the chat:
   - Goal
   - Non-goals
   - Requirements (FR-#, NFR-#)
   - Acceptance criteria
   - Test/verification plan
3) Implement to the brief and verify via commands below.

---

## Tech Stack

<!-- List technologies used. Add/remove rows as needed -->

| Technology | Purpose |
|------------|---------|
| {tech} | {why it's used} |

---

## Commands

<!-- Common commands for this project. Adjust based on your package manager and setup -->

```bash
# Development
{dev-command}

# Build
{build-command}

# Lint
{lint-command}

# Typecheck
{typecheck-command}

# Unit Tests
{test-command}

# E2E Integration
{integration-command}
```

---

## Project Structure

<!-- Describe your folder organization. This varies greatly by project type -->

```
{root}/
├── {dir}/     # {description}
├── {dir}/     # {description}
└── {dir}/     # {description}
```
---

## Architecture

<!-- Describe how the code is organized. Examples:
- Layered (routes → services → data)
- Component-based (features as self-contained modules)
- MVC pattern
- Event-driven
- etc.
-->

{Describe the architectural approach and data flow}

---

## Rules

The following repository rules must always be followed:

- Maintain project structure updated: everytime a folder/file of significance for the Project is add/updated/removed, maintain Projec Structure section updated.

- Architecture Decision Records: for each architectural decision made create and add a new adr file into `.docs/adr/<adr_decision_description>.md`, and add its pertinent entry into the ADR Index file `.docs/adr/adr_index.md`, if adr index file does not exist then create it.

- API Contracts: add every single API contract files into `.docs/api/`, and add its pertinent entry into the API Contracts Index file `.docs/api/api_contracts_index.md`, if api contracts index file does not existe then create it.

---

## Code Patterns

<!-- Key patterns and conventions used in this codebase -->

### Naming Conventions
- {convention}

### File Organization
- {pattern}

### Error Handling
- {approach}

---

## Definition of Done

<!--  -->

- Tests pass: `{<list>}`
- Lint/typecheck pass: `{<list>}`
- Docs updated: `{if behavior changes}`

---

## Testing

<!-- How to test and what patterns to follow -->

- **Run tests**: `{test-command}`
- **Test location**: `{test-directory}`
- **Pattern**: `{describe test approach}`

---

## Validation

<!-- Commands to run before committing -->

```bash
{validation-commands}
```

---

## Key Files

<!-- Important files to know about -->

| File | Purpose |
|------|---------|
| `{path}` | {description} |

---

## On-Demand Context

<!-- Optional: Reference docs for deeper context -->

| Topic | File |
|-------|------|
| {topic} | `{path}` |

---

## Boundaries (Do NOT)

<!-- Important Rules -->

- Don’t change CI/infra without explicit instruction
- Don’t refactor unrelated modules during feature work
- Don’t introduce new dependencies without justification
- No secrets in logs or commits

---

## Notes

<!-- Any special instructions, constraints, or gotchas -->

- {note}
