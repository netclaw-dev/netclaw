## Context

See proposal.md for the two live prompts. Two facts already exist:

- `ShellCommandAnalysis.HasProvedDataOperands` proves that each operand of a
  Bash data command is data. `TryCreateAssignmentDigest` uses it to drop the
  assignment digest of such a command.
- `ApprovalCandidate.WordRewriteCanResolve` marks an exact candidate whose
  only cause is the pathname-expansion rule.
  `SelectCommandWordsCorrection` reads it, but only for a candidate with
  unknown command words.

## Goals / Non-Goals

**Goals:**
- Reuse the existing facts. Add no second list of data commands and no new
  parser fact.

**Non-Goals:**
- No change to the D1 rule, to a folder or repository grant, or to the
  pathname-expansion rule itself.
- No program grammar.

## Decisions

1. F2 owner: `ShellApprovalMatcher.ExtractCommandCandidates`. Schematic flow
   for one command of an unresolved Bash call (call-local data):

   ```text
   if directory is Exact or IsScopeFreeDataCommand(command):
       part = analysis.GetUnresolvedPart(command)   # program word, structure
   else:
       part = Command                               # exact candidate
   if part == None: normal candidates (echo -> exempt)
   ```

   `IsScopeFreeDataCommand` is the condition that `TryCreateAssignmentDigest`
   already used: a Bash data command, no redirect, and
   `HasProvedDataOperands`. Both callers now share it. Alternative: test the
   bare verb in `IsPureSideEffect` for an exact candidate. Rejected: an exact
   candidate can carry a redirect or an unproved operand, so the verb alone
   is not enough.

2. F4 owner: `ShellPolicyCoordinator.SelectCommandWordsCorrection`. For an
   uncovered candidate with known command words and `WordRewriteCanResolve`,
   it returns `ToolCorrection.ShellWordQuoteSuggested` with the source text
   of each word that `ShellCommandAnalysis.GetUnboundedPathnameExpansionWords`
   gives. The word list uses the same predicate as the expansion rule. The
   delivery reuses the `rewrite_shell_command_words` remediation code. The
   message shows the word in double quotes only when double quotes keep the
   meaning of the rest of the word (no quote, backslash, glob character,
   brace, or tilde). Alternative: a new enum value of
   `ShellCommandWordsRewrite`. Rejected: the message must name the word.

3. Unattended runs: the existing rewrite correction already applies attended
   and unattended (D2). The quote correction does the same.

## Risks / Trade-offs

- [A scope-free data command after an unknown directory could reach a path]
  → The rule requires no redirect and proved data operands, so the command
  has no path scope. A mutation target covers each condition.
- [The quote advice can change the meaning of a word that must expand] → The
  message tells the agent to write each path literally in that case. The
  correction grants no authority, and the retry passes normal approval.
