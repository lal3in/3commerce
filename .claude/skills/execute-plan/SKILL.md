---
description: Execute an implementation plan

---

# Execute: Implement from Plan

## Plan to Execute

The plan status file `.ai-shared/plans/plan_status_executions.md` is large (hundreds of rows) — do NOT read it whole. Find the open rows with:

```bash
grep -nE '\| (pending|in_progress|blocked) \|' .ai-shared/plans/plan_status_executions.md
```

Pick the next task to work on (or the `in_progress` one to resume) and take its Plan Path column.

## Execution Instructions

### 1. Read and Understand

- Keep the selected rows of the plan-execution-status file updated (edit the one line by its Task_ID — `grep -n '^| <Task_ID> '`) so an interrupted execution can resume on the next run. Keep each Comments cell to a few sentences; longer detail goes in the plan file.
- Apply these status transitions explicitly:
  - `pending → in_progress`: immediately before starting a task.
  - `in_progress → completed`: only after that task's implementation and validation succeed.
  - `in_progress → blocked`: when work cannot continue; record the blocker and the next action in Comments.
  - `blocked → in_progress`: only after the blocker is resolved; record the resolution in Comments.
  - On interruption, leave the active task `in_progress` and record the exact resume point in Comments.
  - Update `Last Modified Date-Time` whenever any status row changes.
- Read the ENTIRE plan carefully plan path is in plan-execution-status selected record column Plan Path. 
- Understand all tasks and their dependencies
- Note the validation commands to run
- Review the testing strategy

### 2. Execute Tasks in Order

For EACH task in "Step by Step Tasks":

#### a. Navigate to the task
- Identify the file and action required, and current task status
- Read existing related files if modifying

#### b. Implement the task
- Follow the detailed specifications exactly
- Maintain consistency with existing code patterns
- Include proper type hints and documentation
- Add structured logging where appropriate

#### c. Verify as you go
- After each file change, check syntax
- Ensure imports are correct
- Verify types are properly defined

### 3. Implement Testing Strategy

After completing implementation tasks:

- Create all test files specified in the plan
- Implement all test cases mentioned
- Follow the testing approach outlined
- Ensure tests cover edge cases

### 4. Run Validation Commands

Execute ALL validation commands from the plan in order:

```bash
# Run each command exactly as specified in plan
```

If any command fails:
- Fix the issue
- Re-run the command
- Continue only when it passes

### 5. Final Verification

Before completing:

- ✅ All tasks from plan completed
- ✅ All tests created and passing
- ✅ All validation commands pass
- ✅ Code follows project conventions
- ✅ Documentation added/updated as needed

## Output Report

Provide summary:

### Completed Tasks
- List of all tasks completed
- Files created (with paths)
- Files modified (with paths)

### Tests Added
- Test files created
- Test cases implemented
- Test results

### Validation Results
```bash
# Output from each validation command
```

### Ready for Commit
- Confirm all changes are complete
- Confirm all validations pass
- Ask what to do when Ready for `/commit` command

## Notes

- If you encounter issues not addressed in the plan, document them
- If you need to deviate from the plan, explain why
- If tests fail, fix implementation until they pass
- Don't skip validation steps
