## 1. Rule and proof

- [x] 1.1 Skip the assignment digest only for proved data operands, and verify with the catalog cases `output-glob-from-binding-prompts` and `output-glob-from-binding-unattended-denies`
- [x] 1.2 Add mutation targets for `HasProvedDataOperands` and `IsOneDoubleQuotedWord`, and verify that every mutant dies
- [x] 1.3 Run `openspec validate require-proved-data-operands --strict`, sync with `/opsx:sync`, and archive with `/opsx:archive`
