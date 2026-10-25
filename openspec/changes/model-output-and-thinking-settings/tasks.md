## 1. Resolve the wire questions

- [ ] 1.1 Answer OQ1-OQ3 with ILSpy on Anthropic 12.50.0, `Microsoft.Extensions.AI.OpenAI`, and OpenAI. Record each answer in design.md D3. Verify that each OQ row in the D3 table has a confirmed field name or a plugin-owned mapping.
- [ ] 1.2 Answer OQ4-OQ11 from the provider and server docs. Mark each unconfirmed level as rejected in the dialect map. Verify that no D3 row says "OQ" when section 2 starts.

## 2. Failing tests first

Write each test, run it, and verify that it fails for the expected reason before section 3.

- [ ] 2.1 Pure table test `ThinkingDialectMapTests.Applies_level_to_request` (Netclaw.Daemon.Tests). Rows:
  - `native_off_sets_effort_none`, `native_low_sets_effort_low`, `native_max_sets_effort_extra_high`
  - `deepseek_off_sets_thinking_disabled`, `deepseek_high_sets_enabled_and_high`, `deepseek_max_sets_enabled_and_max`, `deepseek_low_throws_unsupported`
  - `chat_template_kwargs_off_sets_enable_thinking_false`, `chat_template_kwargs_low_throws_unsupported`, `chat_template_kwargs_merges_caller_kwargs_without_changing_them`
  - `reasoning_effort_medium_sets_top_level_field`, `reasoning_effort_off_throws_unsupported`
  - `ollama_think_off_sets_think_false`, `none_low_throws_unsupported`
  - `unset_level_writes_nothing` (one row for each dialect)
  - `suppress_on_native_writes_nothing`, `suppress_on_reasoning_effort_writes_nothing`, `suppress_on_deepseek_sets_disabled`, `suppress_on_unresolved_sets_enable_thinking_false`
- [ ] 2.2 Pure table test `ThinkingDialectMapTests.Supported_levels` with one row for each dialect. The rows match the D2 table.
- [ ] 2.3 Wire table test `ProviderRequestMappingTests.Maps_settings_to_wire` with a capture handler and no network. Rows:
  - `anthropic_unset_limit_sends_32000`, `anthropic_limit_64000_sends_64000`, `anthropic_low_sends_adaptive_and_effort_low`, `anthropic_max_sends_effort_max`, `anthropic_unset_level_sends_no_thinking`
  - `openai_responses_limit_sends_max_output_tokens`, `openai_responses_low_sends_reasoning_effort_low`, `openai_unset_sends_neither_field`
  - `copilot_responses_low_sends_reasoning_effort_low`, `venice_low_sends_mapped_field`
  - `openrouter_high_keeps_exclude_and_sets_effort`
  - `deepseek_max_sends_thinking_enabled_and_max`
  - `ollama_limit_sends_num_predict`, `ollama_off_sends_think_false`
  - `compat_limit_sends_max_tokens`, `compat_chat_template_off_sends_enable_thinking_false`, `compat_reasoning_effort_high_sends_reasoning_effort`
- [ ] 2.4 Precedence tests in `ModelRequestSettingsChatClientTests`: `call_site_limit_wins_over_model`, `model_limit_fills_unset_call_limit`, `suppression_wins_over_model_level`.
- [ ] 2.5 Failover tests in `RoutingChatClientProviderTests` (#2681): `fallback_receives_no_dialect_field_from_failed_candidate`, `fallback_receives_no_vendor_option_field_from_failed_candidate`, `fallback_applies_its_own_definition_settings`, `caller_options_unchanged_after_all_candidates`.
- [ ] 2.6 One orchestration test `LlmSessionModelSettingsTests.Session_turn_request_carries_mapped_model_settings`. It runs a session turn through the real `ProviderPluginFactory` stack against a capture endpoint. Main is an OpenAI-compatible definition with dialect `ChatTemplateKwargs`, `MaxOutputTokens` 32000, and `Thinking` `Off`. Verify that the body holds `max_tokens` 32000 and `chat_template_kwargs.enable_thinking` false.
- [ ] 2.7 Config tests in `ModelReferenceBindingTests` and the schema doctor tests: `limit_and_thinking_bind_from_named_definition`, `invalid_thinking_fails_with_key_path`, `zero_limit_fails_with_key_path`, `schema_accepts_both_fields`, `doctor_fix_keeps_invalid_limit`.
- [ ] 2.8 CLI save tests in `ModelCommandTests`. Each reject row asserts that the `netclaw.json` bytes do not change. Each save row reloads the file through `ModelConfigurationResolver`. Rows:
  - `set_saves_limit_and_thinking_and_reload_resolves_them`
  - `zero_limit_rejected_file_unchanged`, `limit_not_below_context_window_rejected_file_unchanged`
  - `deepseek_low_rejected_lists_supported_levels_file_unchanged`
  - `vllm_without_dialect_thinking_rejected_names_key_file_unchanged`, `vllm_without_dialect_limit_saved`
  - `llama_cpp_detected_dialect_saved_with_definition`
  - `ollama_disable_thinking_conflict_rejected_file_unchanged`
  - `clear_flags_remove_settings`, `set_and_clear_together_rejected`
- [ ] 2.9 TUI save tests in `ModelManagerViewModelTests`: `typed_limit_digits_reach_the_field` (headless typed keys), `save_valid_limit_persists_and_reloads`, `save_unsupported_level_shows_supported_levels_and_does_not_write`.
- [ ] 2.10 Doctor and startup tests: `doctor_reports_negative_limit_with_key_path`, `doctor_reports_unsupported_level_with_remediation`, `doctor_reports_invalid_thinking_dialect`, `startup_fails_on_hand_edited_unsupported_level`.
- [ ] 2.11 Log tests: `startup_line_shows_limit_level_dialect_and_sources`, `debug_line_shows_call_source_for_curation_limit`.

## 3. Configuration and validation

- [ ] 3.1 Add `MaxOutputTokens` and `Thinking` to `ModelReference`, and add the `ThinkingLevel` enum. Update `netclaw-config.v1.schema.json` in the same commit. Verify with the 2.7 tests.
- [ ] 3.2 Move dialect resolution to `IProviderDescriptor.ResolveThinkingDialect(ProviderEntry)`. Rename `ReasoningSuppressionDialect` to `ThinkingDialect`, and add the static supported-level map. Add `ThinkingDialect` to the OpenAI-compatible vendor options. Verify with 2.2 and `doctor_reports_invalid_thinking_dialect`.
- [ ] 3.3 Add the one validator (limit, context window, level support, Ollama conflict). Call it from `ModelEntryWriter`, doctor, and startup validation. Verify with 2.10.

## 4. Runtime mapping

- [ ] 4.1 Replace `ReasoningSuppressionChatClient` with `ModelRequestSettingsChatClient`. Make it the outermost wrapper in `ProviderPluginFactory.Create`, and copy the options first. Verify with 2.1, 2.4, and 2.5.
- [ ] 4.2 Move the DeepSeek reasoning code from `OpenAiCompatibleChatClient` into the DeepSeek dialect. Keep the replay rules in the wire profile. Verify with `deepseek_*` rows in 2.1 and 2.3 and the existing DeepSeek tests.
- [ ] 4.3 Add the Anthropic `DefaultMaxOutputTokens` (32000, pending Aaron's D6 decision) and the Anthropic thinking mapping from 1.1. Verify with the `anthropic_*` rows in 2.3.
- [ ] 4.4 Change `OpenRouterReasoningExcludePolicy` to merge `exclude` with the effort. Verify with `openrouter_high_keeps_exclude_and_sets_effort` and the existing policy tests (update `OverwritesExistingReasoningField`).
- [ ] 4.5 Add the startup line and the debug line. Verify with 2.11.
- [ ] 4.6 Run 2.6 and verify that it passes.

## 5. CLI and TUI

- [ ] 5.1 Add the four `model set` flags and dialect detection through the existing probe. Verify with 2.8.
- [ ] 5.2 Add the two TUI model manager fields. Verify with 2.9.
- [ ] 5.3 Extend `tests/smoke/tapes/model-manager.tape`: set the output limit to 32000 and the level to `off`, save, and assert the persisted values. Then try an unsupported level and assert the error text. Verify with `./scripts/smoke/run-smoke.sh model-manager` and `./scripts/smoke/run-smoke.sh light`.

## 6. Docs, guidance, and checks

- [ ] 6.1 Add "thinking level" and "thinking dialect" to `docs/spec/GLOSSARY.md`. Verify that each spec delta links the glossary.
- [ ] 6.2 Update SPEC-008 with the D3 table and the D6 default, and update the `netclaw model set` help text with the four flags. Verify by reading SPEC-008 and `netclaw model set --help`.
- [ ] 6.3 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md` (flags, dialect key, rejection text, limit includes thinking tokens) and bump `metadata.version`. Run `./evals/run-evals.sh` and verify that it passes.
- [ ] 6.4 Review the focused mutation scope for the failover copy (a reliability boundary). Add one narrow target if a deterministic test rejects a specific unsafe mutant, for example a removed `Clone()`. Verify with the Stryker procedure in `TOOLING.md`.
- [ ] 6.5 Run `dotnet test` for the touched test projects, `dotnet slopwatch analyze`, and `./scripts/Add-FileHeaders.ps1 -Verify`. Verify 0 failures and 0 new violations.
- [ ] 6.6 Close #2681 from the PR text. Run `/opsx-verify`, `/opsx-sync`, and `/opsx-archive` after the merge.
