# Sanitized tool-loop corpus

This corpus records semantic facts from historical tool loops. The Python
validator defines the fixture contract. It contains no
raw prompt, tool arguments, path, result text, call identifier, timestamp, or
private identity.

The `baseline` object locks replay to the exact beta.3 action-and-outcome
detector. The detector supports periods one through three and keeps six
completed iterations. It records the release source revision and hash.

Each case uses symbolic tokens. A completed event represents one completed
tool batch. A batch contains one or more calls. The replay sorts calls by tool
family and argument class before it compares them. A candidate event
represents the next batch before dispatch. A boundary event records a new
turn, compaction, or recovery without payload data. The
`expected_decision` field records the locked baseline decision. The case label
records a separate corpus assessment: harmful, productive, or uncertain.
The first case records aggregate counts of 45 requests and a 43-request exact
suffix. Its replay prefix keeps two distinct failures and two equal failures.
The baseline must propose a correction at relative request index 5 before a
new result enters history. An index of zero means that the case has no
intervention.

Run the offline replay from the repository root:

```bash
python3 evals/tool_loop_replay.py evals/fixtures/tool-loop/corpus.json
```

The explicit source audit appears in `evals/README.md`. Normal replay uses the
recorded release provenance and does not inspect the current source file.

The command writes deterministic, compact JSON to standard output. Raw logs
must remain outside the repository. Use `extract_sanitized_case` only with
symbolic facts after local redaction.

The corpus is a small seed set. It does not prove a false-block bound or a
production safety rate.
