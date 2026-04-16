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

## 2. Snapshot the audit report

The fix workflow takes a repo-relative path, so copy the latest run's
binding into `audit-reports/` under a dated filename AND a stable
`latest-quality-audit.md` that the fix commands below point at.

Run from the repo root in a normal shell (`!` prefix in this session):

```bash
mkdir -p audit-reports && \
  cp "$(ls -td .prose/runs/*/bindings/audit_report.md | head -1)" \
     "audit-reports/pre-oss-quality-audit-$(date +%Y%m%d).md" && \
  cp "$(ls -td .prose/runs/*/bindings/audit_report.md | head -1)" \
     audit-reports/latest-quality-audit.md
```

## 3. Fix — dry run (default)

Parses the audit's Section 8, drift-checks, prints diffs. No edits,
no commits.

```
/open-prose:prose-compile .prose/netclaw-quality-fix.prose
```

```
/open-prose:prose-run .prose/netclaw-quality-fix.prose \
  repo_path=/home/petabridge/repositories/stannardlabs/netclaw \
  report_path=audit-reports/latest-quality-audit.md
```

## 4. Fix — live

Applies fixes on a fresh `netclaw/quality-fix-<date>` branch with
build/test gates. Requires a clean working tree and a non-default
current branch.

```
/open-prose:prose-run .prose/netclaw-quality-fix.prose \
  repo_path=/home/petabridge/repositories/stannardlabs/netclaw \
  report_path=audit-reports/latest-quality-audit.md \
  dry_run=false
```
