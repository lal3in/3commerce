---
description: Create global rules (AGENTS.md) from codebase analysis
---

# Create Global Rules

Generate a AGENTS.md file by analyzing the codebase and extracting patterns.

---

## Objective

Create project-specific global rules that give AI context about:
- What this project is
- Technologies used
- How the code is organized
- Patterns and conventions to follow
- How to build, test, and validate

---

## Phase 1: DISCOVER

### Identify Project Type

First, determine what kind of project this is:

| Type | Indicators |
|------|------------|
| Web App (Full-stack) | Separate client/server dirs, API routes |
| Web App (Frontend) | React/Vue/Svelte, no server code |
| API/Backend | Express/Fastify/etc, no frontend |
| Library/Package | `main`/`exports` in package.json, publishable |
| CLI Tool | `bin` in package.json, command-line interface |
| Monorepo | Multiple packages, workspaces config |
| Script/Automation | Standalone scripts, task-focused |

### Analyze Configuration

Look at root configuration files:

```
package.json       → dependencies, scripts, type
tsconfig.json      → TypeScript settings
vite.config.*      → Build tool
*.config.js/ts     → Various tool configs
```

### Map Directory Structure

Explore the codebase to understand organization:
- Where does source code live?
- Where are tests?
- Any shared code?
- Configuration locations?

---

## Phase 2: ANALYZE

### Extract Tech Stack

From package.json and config files, identify:
- Runtime/Language (Node, Bun, Deno, browser)
- Framework(s)
- Database (if any)
- Testing tools
- Build tools
- Linting/formatting

### Identify Patterns

Study existing code for:
- **Naming**: How are files, functions, classes named?
- **Structure**: How is code organized within files?
- **Errors**: How are errors created and handled?
- **Types**: How are types/interfaces defined?
- **Tests**: How are tests structured?

### Find Key Files

Identify files that are important to understand:
- Entry points
- Configuration
- Core business logic
- Shared utilities
- Type definitions

---

## Phase 3: GENERATE

### Create AGENTS.md

Use the template at `~/.pi/agent/resources/templates/Project-AGENTS-template.md` as the mandatory output structure.

**Output path**: `AGENTS.md` in the project root.

**Strict template rules:**
- Preserve every top-level `##` section from the template.
- Preserve the section order from the template.
- Do not delete sections unless explicitly marked as optional below.
- Replace placeholders with project-specific content.
- If a section does not apply, keep the section and write `Not applicable for this project` with a short reason.
- Do not leave unresolved placeholders such as `{tech}`, `{path}`, or `{test-command}`.
- Keep custom rules from the template exactly, especially:
  - Collaboration protocol
  - Sources of truth
  - PRD Loading Rule
  - Rules
  - Definition of Done
  - Boundaries

**Optional sections that may be omitted only if irrelevant:**
- API endpoints (for backends)
- Component patterns (for frontends)
- Database patterns (if using a DB)
- On-Demand Context References
- Notes

**Required sections:**
1. Project Overview (What is this and what does it do?)
2. Collaboration protocol
3. Sources of truth
4. Tech Stack (What technologies are used?)
5. Commands (How to dev, build, test, lint?)
6. Project Structure (How is the code organized?)
7. Architecture
8. Rules
9. Code Patterns (What conventions should be followed?)
10. Definition of Done
11. Testing
12. Validation
13. Key Files (What files are important to know?)
14. Boundaries

---

## Phase 4: OUTPUT

```markdown
## Global Rules Created

**File**: `AGENTS.md`

### Project Type

{Detected project type}

### Tech Stack Summary

{Key technologies detected}

### Structure

{Brief structure overview}

### Next Steps

1. Review the generated `AGENTS.md`
2. Add any project-specific notes
3. Remove any sections that don't apply
4. Optionally create reference docs in `./docs/reference/`
```

---

## Tips

- Keep AGENTS.md focused and scannable
- Don't duplicate information that's in other docs (link instead)
- Focus on patterns and conventions, not exhaustive documentation
- Update it as the project evolves
