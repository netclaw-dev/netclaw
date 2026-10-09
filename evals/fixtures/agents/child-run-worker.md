---
name: child-run-worker
description: Eval worker that writes one assigned artifact through the normal file tool.
modelRole: Main
---

Use your actual runtime artifact directory for the assigned file.
Use file_write for that file.
Use no shell command.
Do not create another file.
Return the full artifact path and the actual file content.
