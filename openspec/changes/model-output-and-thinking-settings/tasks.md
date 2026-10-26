## 1. Resolve the wire questions

- [x] 1.1 Verify the Anthropic SDK 12.50.0 effort mapping, the OpenAI SDK 2.12.0 and M.E.AI 10.8.3 Chat fields, and the OllamaSharp 5.4.30 `think` mapping by decompile. Record each fact in design.md D3. Result: verified; see D3 "Facts behind the rows".
- [x] 1.2 Verify the OpenRouter, Venice, DeepSeek, Ollama, llama.cpp, vLLM, and models.dev facts from the official docs, the live catalogs, and the server source. Record them in D3 and D5. Result: verified, except the items in Open Questions.
- [x] 1.3 Answer design.md Open Questions 1-7. Mark each unconfirmed level as not offered in the dialect map. Verify that no D3 or D5 row says "U" for a field that section 4 sends.

## 2. Failing tests first

Write each test, run it, and verify that it fails for the expected reason before section 3.

- [x] 2.1 Pure table test `ThinkingDialectMapTests.Applies_level_to_request` (Netclaw.Daemon.Tests). Rows:
  - `native_off_sets_effort_none`, `native_low_sets_effort_low`, `native_max_sets_effort_extra_high`
  - `deepseek_off_sets_thinking_disabled`, `deepseek_low_sets_enabled_and_low`, `deepseek_high_sets_enabled_and_high`, `deepseek_max_sets_enabled_and_max`, `deepseek_medium_throws_unsupported`
  - `reasoning_effort_off_sets_none`, `reasoning_effort_medium_sets_top_level_field`, `reasoning_effort_max_throws_unsupported`
  - `chat_template_kwargs_off_sets_enable_thinking_false`, `chat_template_kwargs_low_throws_unsupported`, `chat_template_kwargs_merges_caller_kwargs_without_changing_them`
  - `ollama_think_off_sets_think_false`, `ollama_think_low_sets_reasoning_low`, `ollama_think_max_throws_unsupported`
  - `none_low_throws_unsupported`
  - `unset_level_writes_nothing` (one row for each dialect)
  - `suppress_on_native_writes_nothing`, `suppress_on_reasoning_effort_sets_none`, `suppress_on_deepseek_sets_disabled`, `suppress_on_unresolved_sets_enable_thinking_false`
- [x] 2.2 Pure table test `OfferedLevelsTests.Intersects_dialect_and_capability`. Rows:
  - `unknown_with_dialect_offers_off_only`, `unknown_without_dialect_offers_nothing`
  - `none_offers_nothing_with_reason`, `always_on_offers_nothing_with_reason`
  - `controllable_without_off_greys_off`, `controllable_levels_intersect_dialect` (DeepSeek: catalog Medium is not offered)
- [x] 2.3 Pure table test `DefaultOutputLimitTests.Computes_default` with the D4 rows:
  - `short_prompt_sends_32000_default`
  - `prompt_near_window_sends_14519_default_clamped`
  - `prompt_past_floor_sends_1024_and_warns`
  - `unknown_window_sends_32000_default`
  - `model_max_16384_sends_16384_default_clamped`
  - `explicit_configured_value_is_not_clamped`
  - `call_site_value_is_not_clamped`
- [x] 2.4 Capability resolver tests with recorded fixtures and no network:
  - `OpenRouterOracleResolverTests`: `mandatory_with_efforts_maps_to_controllable_without_off`, `no_reasoning_parameter_maps_to_none`, `reasoning_without_efforts_maps_to_controllable_with_off`, `top_provider_max_completion_tokens_maps_to_max_output`
  - `OllamaCapabilityResolverTests`: `thinking_values_strings_map_to_levels`, `thinking_values_false_maps_to_none`, `no_thinking_object_maps_to_unknown`
  - `OpenAiCompatibleCapabilityResolverTests`: `llama_cpp_supports_reasoning_effort_maps_to_levels`, `llama_cpp_enable_thinking_marker_maps_to_off_only`, `vllm_maps_to_unknown`
  - `AnthropicCapabilityResolverTests`: `effort_flags_map_to_levels`, `max_tokens_maps_to_max_output`
  - `CompositeCapabilityResolverTests`: `native_thinking_wins_over_catalog`, `catalog_wins_over_unknown`
- [x] 2.5 Wire table test `ProviderRequestMappingTests.Maps_settings_to_wire` with a capture handler and no network. Rows:
  - `anthropic_unset_limit_sends_clamped_default`, `anthropic_limit_64000_sends_64000`, `anthropic_low_sends_adaptive_and_effort_low`, `anthropic_max_sends_effort_max`, `anthropic_unset_level_sends_no_thinking`
  - `openai_responses_limit_sends_max_output_tokens`, `openai_responses_low_sends_reasoning_effort_low`
  - `copilot_responses_low_sends_reasoning_effort_low`
  - `openrouter_high_keeps_exclude_and_sets_effort`, `openrouter_limit_sends_max_completion_tokens`
  - `venice_max_sends_reasoning_effort_max`, `venice_limit_sends_max_completion_tokens`
  - `deepseek_max_sends_thinking_enabled_and_max`
  - `ollama_limit_sends_num_predict`, `ollama_off_sends_think_false`, `ollama_low_sends_think_low`
  - `compat_limit_sends_max_tokens`, `compat_reasoning_effort_off_sends_none`, `compat_chat_template_off_sends_enable_thinking_false`
- [x] 2.6 Precedence tests in `ModelRequestSettingsChatClientTests`: `call_site_limit_wins_over_model`, `model_limit_fills_unset_call_limit`, `default_fills_unset_model_limit`, `suppression_wins_over_model_level`.
- [x] 2.7 Failover tests in `RoutingChatClientProviderTests` (#2681): `fallback_receives_no_dialect_field_from_failed_candidate`, `fallback_receives_no_vendor_option_field_from_failed_candidate`, `fallback_applies_its_own_definition_settings`, `caller_options_unchanged_after_all_candidates`.
- [x] 2.8 One orchestration test `LlmSessionModelSettingsTests.Session_turn_request_carries_mapped_model_settings`. It runs a session turn through the real `ProviderPluginFactory` stack against a capture endpoint. Main is an OpenAI-compatible definition with dialect `ReasoningEffort`, `MaxOutputTokens` 32000, and `Thinking` `Off`. Verify that the body holds `max_tokens` 32000 and `reasoning_effort` `none`.
- [x] 2.9 Config tests in `ModelReferenceBindingTests` and the schema doctor tests: `limit_and_thinking_bind_from_named_definition`, `invalid_thinking_fails_with_key_path`, `zero_limit_fails_with_key_path`, `schema_accepts_both_fields`, `doctor_fix_keeps_invalid_limit`.
- [x] 2.10 CLI tests in `ModelCommandTests`. Each reject row asserts that the `netclaw.json` bytes do not change. Each save row reloads the file through `ModelConfigurationResolver`. Rows:
  - `set_saves_limit_and_thinking_and_reload_resolves_them`
  - `zero_limit_rejected_file_unchanged`, `limit_not_below_context_window_rejected_file_unchanged`, `limit_above_known_model_max_rejected_file_unchanged`
  - `deepseek_medium_rejected_lists_offered_levels_file_unchanged`
  - `unknown_capability_low_rejected_file_unchanged`, `unknown_capability_off_saved`
  - `vllm_without_dialect_thinking_rejected_names_key_file_unchanged`, `vllm_without_dialect_limit_saved`
  - `llama_cpp_detected_dialect_saved_with_definition`
  - `ollama_disable_thinking_conflict_rejected_file_unchanged`
  - `clear_flags_remove_settings`, `set_and_clear_together_rejected`
  - `list_shows_max_out_and_thinking_columns`
- [x] 2.11 TUI tests in `ModelManagerViewModelTests`: `p_opens_properties_for_selected_role`, `typed_limit_digits_reach_the_field` (headless typed keys), `save_valid_limit_persists_and_reloads`, `unoffered_level_is_greyed_with_reason`, `shared_definition_shows_also_used_by`, `no_dialect_allows_only_output_field`, `role_table_shows_max_out_and_thinking`.
- [x] 2.12 Doctor and startup tests: `doctor_reports_negative_limit_with_key_path`, `doctor_reports_unoffered_level_with_remediation`, `doctor_reports_invalid_thinking_dialect`, `startup_fails_on_hand_edited_unsupported_level`.
- [x] 2.13 Log tests: `startup_line_shows_limit_inputs_level_dialect_and_capability`, `debug_line_shows_default_clamped_and_prompt_estimate`, `debug_line_shows_call_source_for_curation_limit`.

## 3. Configuration, capability, and validation

- [x] 3.1 Add `MaxOutputTokens` and `Thinking` to `ModelReference`, and add the `ThinkingLevel` enum. Update `netclaw-config.v1.schema.json` in the same commit. Verify with 2.9.
- [x] 3.2 Move dialect resolution to `IProviderDescriptor.ResolveThinkingDialect(ProviderEntry)`. Rename `ReasoningSuppressionDialect` to `ThinkingDialect`, and add the static map of expressible levels. Add `ThinkingDialect` to the OpenAI-compatible vendor options. Verify with 2.1 and `doctor_reports_invalid_thinking_dialect`.
- [x] 3.3 Add `ThinkingSupport` and `MaxOutputTokens` to `ResolvedModelCapabilities`. Change the composite merge to "first non-`Unknown` wins" for `Thinking`. Extend the OpenRouter, Ollama, and OpenAI-compatible resolvers, and add the Anthropic Models API resolver. Verify with 2.2 and 2.4.
- [x] 3.4 Add the one validator (limit, context window, model max, offered level, Ollama conflict). Call it from `ModelEntryWriter`, doctor, and startup validation. Verify with 2.12.

## 4. Runtime mapping

- [x] 4.1 Replace `ReasoningSuppressionChatClient` with `ModelRequestSettingsChatClient`. Make it the outermost wrapper in `ProviderPluginFactory.Create`, and copy the options first. Verify with 2.1, 2.6, and 2.7.
- [x] 4.2 Add the default limit and clamp. Share the `EstimateTokens` rule with `SessionCompactionPipeline` through one helper. Resolve the capability inputs once for each client on first use. Verify with 2.3.
- [x] 4.3 Move the DeepSeek reasoning code from `OpenAiCompatibleChatClient` into the DeepSeek dialect. Keep the replay rules in the wire profile. Verify with the `deepseek_*` rows in 2.1 and 2.5 and the existing DeepSeek tests.
- [x] 4.4 Add the plugin-owned `max` effort for Anthropic (raw `output_config.effort`) and Venice (JSON patch `reasoning_effort`). Verify with `anthropic_max_sends_effort_max` and `venice_max_sends_reasoning_effort_max`.
- [x] 4.5 Change `OpenRouterReasoningExcludePolicy` to merge `exclude` with the effort. Verify with `openrouter_high_keeps_exclude_and_sets_effort` and the existing policy tests (update `OverwritesExistingReasoningField`).
- [x] 4.6 Add the startup line and the debug line. Verify with 2.13.
- [x] 4.7 Run 2.8 and verify that it passes.

## 5. CLI and TUI

- [x] 5.1 Add the four `model set` flags, the two `model list` columns, and dialect detection through the existing probe. Verify with 2.10.
- [x] 5.2 Add the TUI role-table columns and the `P` properties panel. Verify with 2.11.
- [x] 5.3 Extend `tests/smoke/tapes/model-manager.tape`: press `P`, set the output limit to 32000 and the level to `off`, save, and assert the persisted values. Then assert that an unoffered level shows its reason. Verify with `./scripts/smoke/run-smoke.sh model-manager` and `./scripts/smoke/run-smoke.sh light`.

## 6. Docs, guidance, and checks

- [x] 6.1 Add "thinking level", "thinking dialect", and "thinking capability" to `docs/spec/GLOSSARY.md`. Verify that each spec delta links the glossary.
- [x] 6.2 Update SPEC-008 with the D3, D4, and D5 tables, and update the `netclaw model` help text. Verify by reading SPEC-008 and `netclaw model set --help`.
- [x] 6.3 Add a `RELEASE_NOTES.md` entry for the behavior change: every provider now receives a default output limit of up to 32000 tokens. Verify by reading the entry.
- [ ] 6.4 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md`: the flags, the default and its clamp, the dialect key, the offered-level rules, and that the limit includes thinking tokens. Bump `metadata.version`. Run `./evals/run-evals.sh` and verify that it passes.
- [ ] 6.5 Review the focused mutation scope for the failover copy and the clamp (reliability boundaries). Add one narrow target if a deterministic test rejects a specific unsafe mutant, for example a removed `Clone()`. Verify with the Stryker procedure in `TOOLING.md`.
- [x] 6.6 Run `dotnet test` for the touched test projects, `dotnet slopwatch analyze`, and `./scripts/Add-FileHeaders.ps1 -Verify`. Verify 0 failures and 0 new violations.
- [ ] 6.7 Close #2681 from the PR text. Run `/opsx-verify`, `/opsx-sync`, and `/opsx-archive` after the merge.
