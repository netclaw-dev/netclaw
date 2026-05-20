## Purpose

Define how netclaw reconciles LLM-emitted tool-call argument values against an
MCP tool's declared input schema before dispatch. The MCP tool's declared
`inputSchema` is the sole authority for coercion: a value is only ever coerced
*toward* the type its parameter declares, never on a guess from the value's
runtime shape. Covers reconstruction of stringified `array`/`object` arguments,
preservation of `string`-typed and undeclared-type parameters, schema-gated
string→scalar coercion, and the requirement that coercion never bypasses or
alters an authorization decision.

## Requirements

### Requirement: Schema-directed tool-call argument coercion

netclaw SHALL treat an MCP tool's declared input schema (`inputSchema`) as the
sole authority for coercing tool-call argument values before they are dispatched
to the MCP server. Coercion SHALL move a value toward the type its schema
declares and SHALL NOT transform a value based on its runtime shape alone. An
argument value MUST be coerced consistently whether it arrives as a
`System.String` or as a `JsonElement` — the two representations provider SDKs
use for the same emitted value. When the schema does not declare a type for a
parameter — the schema is absent, the parameter is unknown to the schema, or its
type is unconstrained — the value SHALL be passed through unchanged rather than
coerced by inference.

#### Scenario: Correctly-shaped argument is passed through unchanged

- **WHEN** an argument value already matches the type its schema declares
- **THEN** netclaw forwards the value to the MCP server unchanged

#### Scenario: Argument with no governing schema is not inferred

- **WHEN** an argument has no declared type in the tool's input schema
- **THEN** netclaw forwards the value unchanged and performs no type inference on it

#### Scenario: Coercion is applied on every MCP dispatch path

- **WHEN** an MCP tool is invoked through any code path that dispatches to an MCP server
- **THEN** schema-directed coercion is applied to its arguments before dispatch
- **AND** no dispatch path forwards raw, uncoerced arguments

### Requirement: Stringified array and object arguments are reconstructed

netclaw SHALL reconstruct a tool-call argument into structured form when the
tool's input schema declares the parameter as `array` or `object` and the model
emitted the argument as a JSON-encoded string. A union type that includes
`array` or `object` — for example an `array` type paired with `null` — counts
as declaring that container type. Reconstruction MUST apply whether the
stringified value arrives as a `System.String` or as a `JsonElement` of
`ValueKind.String`. netclaw SHALL reconstruct only when the parsed JSON kind
matches the declared kind; a value that fails to parse as JSON, or that parses
as a different kind than the schema declares, SHALL be passed through unchanged
so the MCP server rejects it explicitly.

#### Scenario: Array-of-objects argument emitted as a JSON string

- **WHEN** a parameter declared `array` receives a JSON-encoded string that parses as a JSON array
- **THEN** netclaw forwards a structured JSON array to the MCP server

#### Scenario: Stringified object value arriving as a JsonElement

- **WHEN** a parameter declared `object` receives a `JsonElement` of `ValueKind.String` whose text parses as a JSON object
- **THEN** netclaw forwards a structured JSON object to the MCP server

#### Scenario: Unparseable string is not reconstructed

- **WHEN** a parameter declared `array` receives a value that is not valid JSON
- **THEN** netclaw forwards the value unchanged

#### Scenario: Parsed kind differs from the declared kind

- **WHEN** a parameter declared `array` receives a string that parses as a JSON object
- **THEN** netclaw forwards the value unchanged and does not coerce it across kinds

### Requirement: String-typed parameters are not re-typed

When a tool's input schema declares a parameter as `string`, netclaw SHALL
forward the value as a string and SHALL NOT infer a numeric or boolean type from
its contents. This prevents silent corruption of string identifiers, codes, and
literal text whose contents resemble other types.

#### Scenario: Zero-padded identifier is preserved

- **WHEN** a parameter declared `string` receives the value `"00713"`
- **THEN** netclaw forwards the string `"00713"` unchanged, preserving its leading zeros and string type

#### Scenario: Boolean-looking string is preserved

- **WHEN** a parameter declared `string` receives the value `"true"`
- **THEN** netclaw forwards the string `"true"` unchanged and does not convert it to a boolean

### Requirement: String-to-scalar coercion requires a scalar schema type

netclaw SHALL coerce a string argument to `integer`, `number`, or `boolean` only
when the tool's input schema declares the parameter as that scalar type. This
preserves compatibility with providers that emit scalar values as strings while
keeping every coercion schema-justified rather than shape-guessed.

#### Scenario: Numeric string is coerced under an integer schema

- **WHEN** a parameter declared `integer` receives the value `"42"`
- **THEN** netclaw forwards the integer `42` to the MCP server

#### Scenario: Numeric string is left alone without a scalar schema

- **WHEN** a parameter receives the value `"42"` and the schema does not declare it as a numeric or boolean type
- **THEN** netclaw forwards the value unchanged

### Requirement: Coercion does not bypass authorization

Schema-directed argument coercion SHALL occur only after a tool invocation has
been authorized by the access policy, and SHALL NOT alter the argument values
that authorization and approval decisions are evaluated against. Coercion is a
dispatch-time transport concern and MUST NOT widen, narrow, or otherwise change
an authorization outcome.

#### Scenario: Authorization evaluates pre-coercion arguments

- **WHEN** an MCP tool invocation is checked by the access policy
- **THEN** the policy evaluates the argument values as the model emitted them, before any schema-directed coercion is applied

#### Scenario: Denied invocation is never coerced or dispatched

- **WHEN** the access policy denies an MCP tool invocation under the default-deny ACL
- **THEN** netclaw does not coerce or dispatch its arguments
