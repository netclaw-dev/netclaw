# NetClaw Quality Workflows

Copy-paste commands for the two OpenProse quality workflows.

## 1. Audit

Fans out auditors in parallel, produces a ranked report with a
`tasks.md` checklist the fix workflow reads.

```
/open-prose:prose-compile .prose/netclaw-quality-audit.prose
```

```
/open-prose:prose-run .prose/netclaw-quality-audit.prose \
  repo_path=/home/petabridge/repositories/stannardlabs/netclaw \
  strictness=normal
```

Output lands at `.prose/runs/<run-id>/bindings/audit_report.md`. Copy
it to `audit-reports/pre-oss-quality-audit-<date>.md` — the fix
workflow takes a repo-relative path.

## 2. Fix — dry run (default)

Parses the audit's Section 8, drift-checks, prints diffs. No edits,
no commits.

```
/open-prose:prose-compile .prose/netclaw-quality-fix.prose
```

```
/open-prose:prose-run .prose/netclaw-quality-fix.prose \
  repo_path=/home/petabridge/repositories/stannardlabs/netclaw \
  report_path=audit-reports/pre-oss-quality-audit-<date>.md
```

## 3. Fix — live

Applies fixes on a fresh `netclaw/quality-fix-<date>` branch with
build/test gates. Requires a clean working tree and a non-default
current branch.

```
/open-prose:prose-run .prose/netclaw-quality-fix.prose \
  repo_path=/home/petabridge/repositories/stannardlabs/netclaw \
  report_path=audit-reports/pre-oss-quality-audit-<date>.md \
  dry_run=false
```
