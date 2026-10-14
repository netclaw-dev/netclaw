# Historical coordination wire corpus

`legacy-writer-records.json` contains neutral bytes from the deployed draft writer at revision `9f79c0c4b637f005ffba5572d0bf50aa550464f2`.

The isolated capture test used the old `NetclawProtobufSerializer.Manifest(object)` and `ToBinary(object)` methods. The temporary capture source is retained in the assessment evidence directory. The candidate serializer did not create these bytes.

Corpus SHA-256: `994682681c870ae7b5242405c488c445569f3dd8f327ac6b18d3d0ee0ec85a36`.

The corpus contains `cra-v1`, all seven `cre-v1` facts, `tta-v1`, `ia-v1`, `ic-v1`, `tbs-v1`, `tcr-v1`, `tar-v1`, `tares-v1`, and `ss-v1`. Each record stores the manifest, Base64 payload, and payload SHA-256.

The #2453 case holds input A pending, admits input B without source IDs on either record, then records adoption of B. Its task text is neutral. The fixture preserves the writer's bytes and does not imply that the sequence is valid.
