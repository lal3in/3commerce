---
description: Create a Product Requirements Document (multi-file) from conversation
---

# Create PRD (Multi-file): Generate Product Requirements Document

## Overview

Generate a comprehensive Product Requirements Document (PRD) based on the current conversation context and requirements discussed.

*Important change:* This command generates:
1) A main PRD index file at `./docs/prd/PRD.md`
2) One Markdown file per required section in /prd/
3) The main PRD contains section headings that include the *path/link* to each section file.

This keeps the PRD scannable while allowing each section to be edited independently.

---

## Output Files

### Main file (index)
Write the PRD index to: `./docs/prd/PRD.md`

### Section files (chapters)
- Each section/chapter is considered a derived <prd-slug> where each section/chapter <prd-slug> will be: normalized to lowercase, have its spaces replaced with '-' and unsafe characters removed.
- Write section files to: /docs/prd/<prd-slug>/... (recommended to avoid collisions)
- If you cannot create a subfolder, fall back to /docs/prd/... with names prefixed by <prd-slug>-<filename>.md

*Example:*
- PRD.md → <prd-slug> = prd
- password-reset.md → <prd-slug> = password-reset

---

## Required Sections → One file each

Create *one Markdown file* per section using the filenames below (inside /docs/prd/<prd-slug>/).

0. TL;DR → 00-tldr.md  
1. Executive Summary → 01-executive-summary.md  
2. Mission → 02-mission.md  
3. Target Users → 03-target-users.md  
4. MVP Scope → 04-mvp-scope.md  
5. User/Flows Stories → 05-user-stories.md  
6. Core Architecture & Patterns → 06-architecture.md  
7. Tools/Features → 07-tools-features.md  
8. Technology Stack → 08-technology-stack.md  
9. Security & Configuration → 09-security-configuration.md  
10. API Specification → 10-api-specification.md  
11. Success Criteria → 11-success-criteria.md  
12. Implementation Phases → 12-implementation-phases.md  
13. Future Considerations → 13-future-considerations.md  
14. Risks & Mitigations → 14-risks-mitigations.md  
15. Appendix → 15-appendix.md  

---

## Main PRD Index Structure

The main PRD file `./docs/prd/PRD.md` should be an index and navigation hub.

It must include:

1) Header metadata:
- PRD: <feature/product>
- Status: Draft | Approved | Shipped
- Owner:
- Last updated:

2) A Table of Contents linking to each section file with a note before the table stating "Do NOT auto-load ANY of the files in the Table of contents (load only if task depends on that specific requirements)"

3) Each required section as a heading with a *path* to its file, like:

### 0. TL;DR
When to read/load this file into context (keep it short, but b=very accurate.)
Path: /docs/prds/<prd-slug>/00-tldr.md   
(Optionally: 1–2 bullet summary lines, but DO NOT inline the full content.)

Repeat this pattern for all sections.

---

## Section Files Content Requirements

Each section file must:
- Start with a section title heading, e.g. # 0. TL;DR
- Contain the full content for that section
- Be written in professional, clear, action-oriented tone
- Use markdown formatting heavily (headings, lists, checkboxes, code blocks)

### Formatting rules
- MVP scope checkboxes: ✅ in-scope, ❌ out-of-scope
- Requirements and success criteria should be measurable and testable
- Keep terminology consistent across all files

---

## Section Content Templates (what to write inside each file)

Write the content using the same structure as before, but in its own file:

### 0. TL;DR (00-tldr.md)
- Problem
- Target user
- Proposed solution
- Success metrics
- Out of scope

### 1. Executive Summary (01-executive-summary.md)
- Concise product overview (2-3 paragraphs)
- Core value proposition
- MVP goal statement

### 2. Mission (02-mission.md)
- Product mission statement
- Core principles (3-5 key principles)

### 3. Target Users (03-target-users.md)
- Primary user personas
- Technical comfort level
- Key user needs and pain points

### 4. MVP Scope (04-mvp-scope.md)
- In Scope: ✅ checkboxes
- Out of Scope: ❌ checkboxes
- Group by categories (Core Functionality, Technical, Integration, Deployment)

### 5. User/Flows Stories (05-user-stories.md)
- 5-8 user stories in: "As a [user], I want to [action], so that [benefit]"
- Include concrete examples for each story
- Add technical user stories if relevant

### 6. Core Architecture & Patterns (06-architecture.md)
- High-level architecture approach
- Directory structure (if applicable)
- Key design patterns and principles
- Technology-specific patterns

### 7. Tools/Features (07-tools-features.md)
- Detailed feature specifications
- If agent: tool designs (purpose, operations, key features)
- If app: feature breakdown

### 8. Technology Stack (08-technology-stack.md)
- Backend/Frontend technologies with versions
- Dependencies and libraries
- Optional dependencies
- Third-party integrations

### 9. Security & Configuration (09-security-configuration.md)
- AuthN/AuthZ approach
- Configuration management (env vars, settings)
- Security scope (in/out)
- Deployment considerations

### 10. API Specification (10-api-specification.md) (if applicable)
- Endpoints
- Request/response formats
- Auth requirements
- Example payloads

### 11. Success Criteria (11-success-criteria.md)
- MVP success definition
- Functional `FR-` + Non-functional `NRF-` requirements (numbered, ✅ checkboxes)
- Quality indicators
- UX goals

### 12. Implementation Phases (12-implementation-phases.md)
- 3-4 phases
- Each phase: Goal, Deliverables (✅), Validation criteria
- Timeline estimates

### 13. Future Considerations (13-future-considerations.md)
- Post-MVP enhancements
- Integration opportunities
- Advanced later-phase features

### 14. Risks & Mitigations (14-risks-mitigations.md)
- 3-5 key risks
- Each risk: impact, likelihood, mitigation, detection, contingency

### 15. Appendix (15-appendix.md)
- Related docs
- Key dependencies with links
- Repo/project structure references

---

## Instructions

### 1. Extract Requirements
- Review the entire conversation history
- Identify explicit requirements and implicit needs
- Note technical constraints and preferences
- Capture user goals and success criteria

### 2. Synthesize Information
- Organize requirements into appropriate sections
- Fill in reasonable assumptions where details are missing
- Maintain consistency across sections
- Ensure technical feasibility

### 4. Write the PRD
- Use clear, professional language
- Include concrete examples and specifics
- Use markdown formatting (headings, lists, code blocks, checkboxes)
- Add code snippets for technical sections where helpful
- Keep Executive Summary concise but comprehensive

### 5. Create Files
- Ensure folder /docs/prd/<prd-slug>/ exists
- Write the PRD index in `./docs/prd/PRD.md` linking to each section file
- Write each section file with its full content
- Use clear, professional language
- Include concrete examples and specifics
- Use markdown formatting (headings, lists, code blocks, checkboxes)
- Add code snippets for technical sections where helpful
- Keep Executive Summary concise but comprehensive

### 6. Quality Checks
- ✅ All section files created and non-empty
- ✅ PRD index links are correct and relative
- ✅ Terminology consistent across files
- ✅ User stories have clear benefits
- ✅ MVP scope is realistic, well-defined and checklisted
- ✅ Technology choices are justified
- ✅ Implementation phases are actionable
- ✅ Success criteria are measurable and testable
- ✅ Consistent terminology throughout

---

## Style Guidelines

- **Tone:** Professional, clear, action-oriented
- **Format:** Use markdown extensively (headings, lists, code blocks, tables)
- **Checkboxes:** Use ✅ for in-scope items, ❌ for out-of-scope
- **Specificity:** Prefer concrete examples over abstract descriptions
- **Length:** Comprehensive but scannable (typically 30-60 sections worth of content)

## Output Confirmation

After creating the PRD:
1. Confirm the main PRD file path
2. Confirm the /docs/prd/<prd-slug>/ folder path
3. List all created section files
4. Provide a brief summary of the contents
5. Highlight any assumptions made due to missing information
6. Suggest next steps (e.g., review, refinement, planning)


## Notes

- If critical information is missing, ask clarifying questions before generating
- Adapt section depth based on available details
- For highly technical products, emphasize architecture and technical stack
- For user-facing products, emphasize user stories and experience
- This command contains the complete PRD template structure - no external references needed
