# NetClaw Quality Workflows

Two workflows: audit, then fix. Run them in order.

## 1. Audit

```
/open-prose:prose-run .prose/netclaw-quality-audit.prose repo_path=$PWD strictness=normal
```

## 2. Snapshot the audit report

Run in a shell (use `!` prefix to run it in this session):

```bash
!mkdir -p audit-reports && cp "$(ls -t .prose/runs/*/bindings/audit_report.md | head -1)" audit-reports/latest-quality-audit.md
```

## 3. Fix — dry run

```
/open-prose:prose-run .prose/netclaw-quality-fix.prose repo_path=$PWD report_path=audit-reports/latest-quality-audit.md
```

## 4. Fix — live

```
/open-prose:prose-run .prose/netclaw-quality-fix.prose repo_path=$PWD report_path=audit-reports/latest-quality-audit.md dry_run=false
```
