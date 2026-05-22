# Upstream MCP Sync

RevitSuite incorporates selected MCP functionality from [`mcp-servers-for-revit`](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit).

This document defines how upstream MCP changes are reviewed, ported, and recorded.

## Upstream Remote

- Remote name: `upstream-mcp`
- Remote URL: `https://github.com/mcp-servers-for-revit/mcp-servers-for-revit.git`
- Branch: `main`

## Review Workflow

Fetch upstream and list changes since the current RevitSuite baseline:

```powershell
.\build\scripts\review-upstream-mcp.ps1
```

Review a specific upstream commit:

```powershell
.\build\scripts\review-upstream-mcp.ps1 -Commit <upstream-commit>
```

Review a specific upstream commit with its full patch:

```powershell
.\build\scripts\review-upstream-mcp.ps1 -Commit <upstream-commit> -ShowPatch
```

After the relevant changes have been ported or intentionally skipped, record the new baseline:

```powershell
.\build\scripts\review-upstream-mcp.ps1 -Commit <upstream-commit> -Record
```

## Baseline Semantics

The baseline in [`upstream-mcp-sync.json`](upstream-mcp-sync.json) records the latest upstream commit that has been handled by RevitSuite.

Handled means one of the following:

- The applicable changes were ported into RevitSuite.
- The changes were reviewed and intentionally skipped.

The baseline is used as the starting point for future upstream reviews. It does not imply that RevitSuite mirrors upstream file-for-file.

## Maintenance Guidelines

- Port upstream changes selectively.
- Keep RevitSuite-specific behavior and packaging authoritative.
- Do not merge upstream directly into `main`.
- Do not advance the baseline for changes that have only been fetched or briefly inspected.
