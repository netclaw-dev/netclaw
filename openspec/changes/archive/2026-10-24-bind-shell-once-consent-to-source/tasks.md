## 1. Bind live shell consent to source

- [x] 1.1 Add a stable, case-sensitive shell source key and carry it on the call-local approval context; test exact hash identity.
- [x] 1.2 Include the source key in shell Once keys and preserve non-shell keys; test identical source, changed source, case changes, and multiline source with equal candidates.
- [x] 1.3 Add a narrow mutation target that removes shell source from Once identity; verify the test rejects that mutant.

## 2. Bind recovered redrive to original call

- [x] 2.1 Correlate resolved call IDs with original structured arguments in history; fail closed when the shell command is missing or malformed.
- [x] 2.2 Test the workflow with exact original ArgumentsJson and test a cold recovered prompt through the real producer and consent consumer.
- [x] 2.3 Verify the existing legacy `command` argument alias uses the same source extraction and adds no journal or protocol field.

## 3. Update contracts and verify

- [x] 3.1 Update the tool-authorization architecture and mapped operational guidance; verify source identity stays call-local.
- [x] 3.2 Run focused authorization and recovery tests, Slopwatch, file-header verification, and strict OpenSpec validation.
