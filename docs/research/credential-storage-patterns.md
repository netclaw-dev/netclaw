# Credential Storage Patterns in Self-Hosted AI Agents and Assistant Frameworks

Research date: 2026-02-23

## Executive Summary

This document surveys how real-world self-hosted AI agents, assistant frameworks,
and homelab platforms store and manage credentials (API keys, OAuth tokens,
integration secrets). The findings reveal a spectrum from plaintext-with-permissions
to encrypted-at-rest-with-vault-integration, with most projects landing somewhere
in the middle.

**Key patterns observed:**

1. **Plaintext JSON with filesystem permissions** -- the most common pattern for
   dev-oriented CLI tools (dotnet user-secrets, OpenCode, Home Assistant secrets.yaml)
2. **OS keychain integration** -- used by Electron apps and polished CLI tools
   (Claude Code on macOS, VS Code, Cursor, Git Credential Manager)
3. **Application-level AES encryption with a user-provided key** -- used by
   multi-user web apps (n8n, LibreChat, Activepieces)
4. **Environment variable injection into subprocesses** -- universal pattern for
   MCP servers, Docker containers, workflow tools
5. **External vault delegation** -- emerging pattern for enterprise deployments
   (n8n External Secrets, Rasa + Vault, Docker MCP Gateway)

---

## 1. Claude Code

**Type:** CLI tool (Node.js/TypeScript)

### Where credentials are stored

| Platform | Storage Location | Encryption |
|----------|-----------------|------------|
| macOS | macOS Keychain (service: `Claude Code-credentials`) | OS-managed keychain encryption |
| Linux | `~/.claude/.credentials.json` (mode 0600) | **None** -- plaintext JSON |
| Windows | `~/.claude/.credentials.json` (mode 0600) | **None** -- plaintext JSON |

### Credential format (Linux/Windows `.credentials.json`)

```json
{
  "claudeAiOauth": {
    "accessToken": "sk-ant-oat01-...",
    "refreshToken": "sk-ant-ort01-...",
    "expiresAt": 1748658860401,
    "scopes": ["user:inference", "user:profile"]
  }
}
```

### How credentials are provided

- **OAuth flow**: `claude` CLI launches browser-based OAuth, receives tokens,
  stores them in keychain (macOS) or `.credentials.json` (Linux/Windows)
- **API key**: Set `ANTHROPIC_API_KEY` environment variable
- **API key helper**: Configure `apiKeyHelper` in settings to run a shell script
  that returns an API key. Called on startup, refreshed every 5 minutes or on
  HTTP 401. TTL configurable via `CLAUDE_CODE_API_KEY_HELPER_TTL_MS`.

### MCP server credential passing

MCP servers are subprocesses. Credentials are passed via the `env` block in
`.mcp.json` configuration:

```json
{
  "mcpServers": {
    "postgres": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-postgres", "..."],
      "env": {
        "PGPASSWORD": "the-actual-password"
      }
    }
  }
}
```

**Known limitation:** Environment variable substitution (`${VAR}`) is NOT
supported in `.mcp.json` files. The `env` block contains literal values. This
means committing `.mcp.json` to source control with credentials is unsafe. The
recommended workaround is to use a wrapper script or keep credentials in
non-committed config.

For stdio transport MCP servers, process isolation is the security boundary --
only the process that launches the server can communicate with it via stdin/stdout.

For HTTP/SSE transport MCP servers, OAuth is supported. The `MCP_CLIENT_SECRET`
env var can skip interactive OAuth prompts. OAuth tokens are stored in the macOS
keychain or a credentials file.

### References

- [Claude Code Authentication Docs](https://code.claude.com/docs/en/authentication)
- [Managing API key environment variables](https://support.claude.com/en/articles/12304248-managing-api-key-environment-variables-in-claude-code)
- [MCP server env var security issue #2065](https://github.com/anthropics/claude-code/issues/2065)
- [Credential file bug #10039](https://github.com/anthropics/claude-code/issues/10039)
- [Keychain issue #9403](https://github.com/anthropics/claude-code/issues/9403)

---

## 2. OpenCode

**Type:** CLI tool (Go, now TypeScript/Bun)

### Where credentials are stored

- `~/.local/share/opencode/auth.json` -- plaintext JSON, mode 0600
- `~/.local/share/opencode/mcp-auth.json` -- OAuth tokens for MCP servers
- Environment variables auto-detected (e.g., `OPENAI_API_KEY`)
- `.env` file in project root

### Encryption at rest

**None.** The `auth.json` file is plaintext. There is an open feature request
([#4318](https://github.com/sst/opencode/issues/4318)) to add system keyring
support using `@napi-rs/keyring` or `Bun.secrets` for cross-platform keychain
access (GNOME Keyring, macOS Keychain, Windows Credential Manager).

### How credentials are provided

- `opencode auth login` -- interactive CLI flow, stores to `auth.json`
- Environment variables -- auto-detected per provider convention
- `.env` file -- loaded from project root

### Secret reference pattern

OpenCode does not currently support referencing external secret stores. The
proposed keyring integration would store credentials with service name `opencode`
and account name as the provider ID.

### References

- [OpenCode Providers Docs](https://opencode.ai/docs/providers/)
- [OpenCode CLI Docs](https://opencode.ai/docs/cli/)
- [Keyring feature request #4318](https://github.com/sst/opencode/issues/4318)

---

## 3. Cursor / Windsurf / AI IDEs (Electron-based)

**Type:** Desktop IDE (Electron/VS Code fork)

### Where credentials are stored

All Electron-based IDEs (Cursor, Windsurf, VS Code) use the same underlying
mechanism inherited from VS Code:

| Platform | Backend | Location |
|----------|---------|----------|
| macOS | macOS Keychain | Keychain Access, service: `<App> Safe Storage` |
| Windows | DPAPI | `%APPDATA%\<App>\` encrypted SQLite |
| Linux | libsecret (GNOME Keyring) / kwallet | `~/.config/<App>/` SQLite (encrypted if keyring available) |

The actual storage is an **encrypted SQLite database** in the user data directory.
Electron's `safeStorage` API encrypts values before writing them to SQLite.

### Encryption mechanism

Electron's `safeStorage` is a thin wrapper (~100 lines C++) around Chromium's
`OSCrypt` package:
- Uses **AES-128-CBC** for encryption
- Keys are derived from the OS credential store (Keychain/DPAPI/libsecret)
- **Linux fallback**: If no secret store is available, uses a hardcoded plaintext
  password -- effectively no encryption

### How credentials are provided

- **Built-in auth**: Cursor/Windsurf use account-based auth, synced to their
  cloud service. API keys are sent to their backend with each request.
- **BYOK (Bring Your Own Key)**: Users paste API keys into settings UI. Keys
  are stored via `safeStorage` in the encrypted SQLite database.
- **MCP config**: `~/.codeium/windsurf/mcp_config.json` for Windsurf MCP
  servers, with `env` blocks for API keys (same pattern as Claude Code)

### VS Code SecretStorage API (for extension developers)

```typescript
// Extensions use context.secrets
const token = await context.secrets.get('myExtension.apiKey');
await context.secrets.store('myExtension.apiKey', 'sk-...');
```

This API was previously backed by `keytar` (now archived), migrated to
Electron's `safeStorage` in VS Code 1.80 (June 2023).

### References

- [Cursor API Keys Docs](https://cursor.com/docs/settings/api-keys)
- [Windsurf Provider API Keys](https://windsurf.com/subscription/provider-api-keys)
- [Electron safeStorage Docs](https://www.electronjs.org/docs/latest/api/safe-storage)
- [VS Code keytar migration #185677](https://github.com/microsoft/vscode/issues/185677)
- [VS Code SecretStorage discussion #748](https://github.com/microsoft/vscode-discussions/discussions/748)

---

## 4. Open WebUI

**Type:** Self-hosted web app (Python/Svelte)

### Where credentials are stored

- **Database**: SQLite (default), PostgreSQL, or cloud storage backends
- **Environment variables**: Provider API keys (`OPENAI_API_KEY`, etc.)
- **OAuth client credentials**: Encrypted in database with `OAUTH_CLIENT_INFO_ENCRYPTION_KEY`

### Encryption at rest

- OAuth client credentials are encrypted using **Fernet symmetric encryption**
  (AES-128-CBC with HMAC-SHA256) with the `OAUTH_CLIENT_INFO_ENCRYPTION_KEY`
- API keys for providers are stored in the database but documentation is unclear
  on whether they are encrypted at rest
- SQLite itself can optionally be encrypted

### How credentials are provided

- **Web UI**: Admin settings page for configuring provider connections
- **Environment variables**: `OPENAI_API_KEY`, `OLLAMA_BASE_URL`, etc.
- **Per-user API keys**: JWT-based, disabled by default, must be explicitly enabled

### Special handling

- Connection management UI to enable/disable individual OpenAI and Ollama
  connections
- API key functionality is disabled by default for security
- Supports S3, GCS, Azure Blob for storage backends

### References

- [Open WebUI Features](https://docs.openwebui.com/features/)
- [Open WebUI API Keys & Monitoring](https://docs.openwebui.com/reference/monitoring/)
- [Open WebUI Auth & Security (DeepWiki)](https://deepwiki.com/open-webui/open-webui/11-authentication-and-access-control)

---

## 5. AnythingLLM

**Type:** Self-hosted desktop + Docker app (Node.js/React)

### Where credentials are stored

- **SQLite database**: `anythingllm.db` in the storage directory
- **`.env` file**: `server/.env` for environment-level configuration
- **Desktop app**: Local settings in OS-specific app data directory

### Encryption at rest

**Credentials are NOT encrypted at rest.** API keys entered through the web UI
are stored in the SQLite database in plaintext or in the `.env` file. The
documentation advises treating the host storage as sensitive.

### How credentials are provided

- **Web UI**: Provider configuration pages for each LLM provider (15+ supported)
- **Environment variables**: `.env` file with provider-specific keys
  (`OPEN_AI_KEY`, `ANTHROPIC_API_KEY`, etc.)
- **Docker**: Mount `.env` to `/app/server/.env` and storage to
  `/app/server/storage`

### Storage structure

```
storage/
  anythingllm.db          # SQLite database
  vector-cache/           # Embedded file cache
  models/                 # Locally stored LLMs
  plugins/                # Custom agent skills
  direct-uploads/         # User-uploaded files
```

### References

- [AnythingLLM Configuration](https://docs.anythingllm.com/configuration)
- [AnythingLLM Desktop Storage](https://docs.anythingllm.com/installation-desktop/storage)
- [AnythingLLM Docker Installation](https://docs.anythingllm.com/installation-docker/local-docker)

---

## 6. LibreChat

**Type:** Self-hosted web app (Node.js/React, MongoDB backend)

### Where credentials are stored

- **MongoDB**: User API keys are encrypted and stored in the database
- **`.env` file**: Server-side provider keys, encryption keys, JWT secrets

### Encryption at rest

**Yes -- AES encryption with application-managed keys.** This is one of the more
sophisticated implementations in this survey.

**Required environment variables:**
```bash
CREDS_KEY=   # openssl rand -base64 32 (AES encryption key)
CREDS_IV=    # openssl rand -base64 16 (AES initialization vector)
JWT_SECRET=  # For session tokens
JWT_REFRESH_SECRET=  # For refresh tokens
```

The application **will crash on startup** if `CREDS_KEY` and `CREDS_IV` are not
set. Encryption is implemented in `api/server/services/Config/encrypt.js`.

### How credentials are provided

- **Admin `.env`**: Server-wide provider API keys
- **User-provided keys**: Users can enter their own API keys via the UI; these
  are encrypted with `CREDS_KEY`/`CREDS_IV` before storage in MongoDB
- **Config file reference pattern**: `librechat.yaml` supports `${VARIABLE_NAME}`
  syntax to reference environment variables from `.env`
- **MCP server user vars**: `customUserVars` allows per-user credentials for MCP
  servers using `{{VARIABLE_NAME}}` syntax

### Secret reference pattern

```yaml
# librechat.yaml
endpoints:
  custom:
    - name: "My Provider"
      apiKey: "${MY_PROVIDER_KEY}"  # References .env variable
```

### Credentials generator tool

LibreChat provides a [web-based credentials generator](https://www.librechat.ai/toolkit/creds_generator)
to generate `CREDS_KEY`, `CREDS_IV`, `JWT_SECRET`, and `JWT_REFRESH_SECRET`.

### References

- [LibreChat Environment Variables](https://www.librechat.ai/docs/configuration/dotenv)
- [LibreChat Credentials Generator](https://www.librechat.ai/toolkit/creds_generator)
- [LibreChat Config Structure](https://www.librechat.ai/docs/configuration/librechat_yaml/object_structure/config)
- [Manual Encryption Discussion #6678](https://github.com/danny-avila/LibreChat/discussions/6678)

---

## 7. Home Assistant

**Type:** Self-hosted homelab platform (Python)

### Where credentials are stored

Home Assistant has **three distinct credential storage mechanisms**:

| Type | Location | Format | Encrypted? |
|------|----------|--------|------------|
| User passwords | `.storage/auth_provider.homeassistant` | JSON | **Hashed + salted** (bcrypt) |
| OAuth2 app credentials | `.storage/application_credentials` | JSON | **No** (plaintext in JSON) |
| Integration config entries | `.storage/core.config_entries` | JSON | **No** (plaintext tokens/keys) |
| User-defined secrets | `secrets.yaml` | YAML | **No** (plaintext) |
| OAuth2 refresh tokens | `.storage/auth` | JSON | **No** (plaintext) |

### The `secrets.yaml` pattern

```yaml
# secrets.yaml (NOT encrypted, just separated from config)
slack_token: "xoxb-your-token-here"
github_pat: "ghp_xxxxxxxxxxxx"

# configuration.yaml
slack:
  token: !secret slack_token
```

**The `!secret` directive provides separation, not security.** The file is
plaintext. Its purpose is to allow sharing `configuration.yaml` publicly (e.g.,
GitHub) while keeping secrets in a `.gitignore`d file.

### OAuth2 integration flow

- Modern integrations use the **Application Credentials** component
- Users register OAuth client ID/secret via the web UI
- Home Assistant handles the OAuth2 authorization code flow
- Refresh tokens are stored in `.storage/auth` as plaintext JSON
- Token refresh happens automatically in the background

### Why no encryption at rest?

Home Assistant's security model assumes the host filesystem is the trust boundary.
If an attacker has filesystem access, they have access to everything. Encryption
at rest would require a key management solution that doesn't align with the
"run on a Raspberry Pi" deployment model.

### References

- [Home Assistant Storing Secrets](https://www.home-assistant.io/docs/configuration/secrets)
- [Home Assistant Application Credentials](https://developers.home-assistant.io/docs/core/platform/application_credentials/)
- [Home Assistant Authentication](https://developers.home-assistant.io/docs/auth_index/)
- [Community: Where are credentials stored?](https://community.home-assistant.io/t/where-are-config-values-credentials-stored/588068)

---

## 8. n8n

**Type:** Self-hosted workflow automation (Node.js/TypeScript)

### Where credentials are stored

- **Database**: SQLite (default) or PostgreSQL
- All credential data is **encrypted before writing to the database**

### Encryption at rest

**Yes -- AES-256 encryption.** This is the strongest built-in encryption in the
survey.

**How it works:**
1. On first launch, n8n generates a random encryption key and saves it to
   `~/.n8n/` (or uses `N8N_ENCRYPTION_KEY` environment variable)
2. Every credential is encrypted with AES-256 before database insertion
3. Credentials are decrypted only at workflow execution time

```bash
# Set custom encryption key
export N8N_ENCRYPTION_KEY="your-32-char-hex-key"
```

**Security model:** If an attacker has only database access, decryption is hard.
If they have full server access (including the encryption key file), credentials
are compromised.

### External secrets (vault integration)

n8n supports **runtime secret resolution** from external vaults:

| Provider | Reference Syntax |
|----------|-----------------|
| HashiCorp Vault | `={{ $secrets.vault.secretName }}` |
| AWS Secrets Manager | `={{ $secrets.awsSecretsManager.secretName }}` |
| Infisical | `={{ $secrets.infisical.secretName }}` |
| Azure Key Vault | `={{ $secrets.azureKeyVault.secretName }}` |
| GCP Secret Manager | (supported) |

Secrets are fetched at runtime, not stored in n8n's database. Secret names must
be alphanumeric with underscores only (no hyphens or spaces).

### How credentials are provided

- **Web UI**: Credential creation forms with type-specific fields
- **OAuth flows**: Built-in OAuth2 redirect handling for supported services
- **Environment variables**: `N8N_ENCRYPTION_KEY`, database connection, etc.

### Cloud vs self-hosted

n8n Cloud adds Azure server-side encryption (AES-256, FIPS-140-2 compliant) on
top of the application-level encryption.

### References

- [n8n Security](https://n8n.io/legal/security/)
- [n8n Custom Encryption Key](https://docs.n8n.io/hosting/configuration/configuration-examples/encryption-key/)
- [n8n External Secrets](https://docs.n8n.io/external-secrets/)
- [Community: How are credentials stored?](https://community.n8n.io/t/how-are-credentials-stored/40166)

---

## 9. Activepieces

**Type:** Self-hosted workflow automation (TypeScript)

### Where credentials are stored

- **Database**: PostgreSQL (primary) or SQLite
- Credentials encrypted before storage

### Encryption at rest

**Yes -- AES-256 encryption** with a user-configured key.

```bash
# .env
AP_ENCRYPTION_KEY=  # 256-bit / 32 hex character encryption key
```

### Authentication types supported

- **SecretText**: Masked input for API keys and passwords
- **OAuth2**: Full OAuth2 flow with auth URL, token URL, scope
- **BasicAuth**: Username + password
- **CustomAuth**: Arbitrary properties (base URL, access token, etc.)

### How credentials are provided

- **Web UI**: Type-specific credential forms
- **OAuth2**: Built-in redirect flow
- **Predefined connections**: Admin can pre-configure connections for embedding
  scenarios

### References

- [Activepieces Authentication](https://www.activepieces.com/docs/developers/piece-reference/authentication)
- [Activepieces Predefined Connection](https://www.activepieces.com/docs/embedding/predefined-connection)
- [Activepieces .env.example](https://github.com/activepieces/activepieces/blob/main/.env.example)

---

## 10. Botpress

**Type:** Cloud-first chatbot platform (TypeScript)

### Where credentials are stored

- **Cloud**: Botpress-managed secure storage
- **Integration definitions**: Secrets declared in `integration.definition.ts`

### How secrets work

Secrets are defined declaratively in the integration schema:

```typescript
export default new IntegrationDefinition({
  name: 'my-integration',
  secrets: {
    CLIENT_ID: { description: 'OAuth Client ID' },
    CLIENT_SECRET: { description: 'OAuth Client Secret' },
    SIGNING_SECRET: { description: 'Webhook Signing Secret' },
  },
})
```

Secrets are accessed at runtime via `ctx.secrets`:

```typescript
const handler = async ({ ctx }) => {
  const apiKey = ctx.secrets.CLIENT_SECRET;
  // ...
}
```

### How credentials are provided

- **Botpress Studio UI**: Users enter secrets when installing an integration
- **OAuth**: Automatic OAuth flow for supported channels (Slack, etc.)
- Secrets are never exposed in plaintext in the UI after initial entry

### Rasa (comparison)

Rasa takes a very different approach:

- **`credentials.yml`**: Plaintext YAML file for channel integrations (Slack
  tokens, Facebook secrets, etc.)
- **Environment variables**: Recommended for sensitive values
- **HashiCorp Vault integration**: Enterprise feature via `endpoints.yml`
  - Credentials stored in Vault, encrypted at rest
  - Transit Engine for additional encryption layer
  - Token auto-renewal (15s before expiry)
  - Namespace isolation support

```yaml
# Rasa endpoints.yml
secrets_manager:
  type: "vault"
  url: "https://vault.example.com"
  secrets_path: "rasa-secrets"
  token: "${VAULT_TOKEN}"
```

### References

- [Botpress Secrets Docs](https://botpress.com/docs/integration/concepts/secrets/)
- [Botpress Slack Integration](https://botpress.com/docs/cloud/channels/slack)
- [Rasa Secrets Managers](https://rasa.com/docs/rasa/secrets-managers)
- [Rasa Vault Integration](https://rasa.com/docs/reference/integrations/secrets-managers/)

---

## 11. MCP Server Credential Patterns

MCP (Model Context Protocol) servers use a consistent credential pattern across
all client implementations.

### Standard pattern: env block injection

```json
{
  "mcpServers": {
    "my-server": {
      "command": "npx",
      "args": ["-y", "my-mcp-server"],
      "env": {
        "API_KEY": "sk-actual-key-value",
        "DATABASE_URL": "postgres://..."
      }
    }
  }
}
```

**Key properties:**
- Environment variables are injected into the subprocess at spawn time
- Each MCP server gets its own isolated env (servers cannot see each other's keys)
- The config file itself is the secret store -- **no env var substitution**
- Process isolation (stdin/stdout) is the security boundary for stdio transport

### Docker MCP Gateway (newer pattern)

Docker's MCP Gateway introduces a more sophisticated model:

- **Secret store**: `docker mcp secret` command manages encrypted secrets
- **OAuth flow**: `docker mcp oauth` handles OAuth token acquisition
- **Runtime scanning**: Gateway scans payloads for leaked secrets
- Secrets managed through Docker Desktop's credential store
- OAuth tokens managed automatically, no plaintext in env vars

### Workarounds for the "no substitution" problem

1. **Wrapper script**: Shell script that loads `.env` and exec's the MCP server
2. **`apiKeyHelper`** (Claude Code): Shell command that returns a key
3. **Non-committed config**: Keep `.mcp.json` in `.gitignore`, use a template
4. **Docker secret mounting**: Mount secrets as files in the container

### References

- [MCP Configuration is a sh*tshow (Medium)](https://0xhagen.medium.com/mcp-configuration-is-a-sh-tshow-but-heres-how-i-fixed-secrets-handling-5395010762a1)
- [Docker MCP Gateway](https://docs.docker.com/ai/mcp-catalog-and-toolkit/mcp-gateway/)
- [Docker MCP Gateway secret bypass #317](https://github.com/docker/mcp-gateway/issues/317)
- [MCP env var management](https://apxml.com/courses/getting-started-model-context-protocol/chapter-4-debugging-and-client-integration/managing-environment-variables)

---

## 12. OS Keychain / Credential Store APIs

### Cross-platform comparison

| Platform | API | Used By | Encryption |
|----------|-----|---------|------------|
| macOS | Keychain Services | Claude Code, Electron apps, GCM | AES-256-GCM, hardware-backed |
| Windows | DPAPI / Credential Manager | Electron apps, GCM, VS Code | DPAPI (user/machine key) |
| Linux | libsecret / Secret Service API | Electron apps (when available) | Depends on backend (GNOME Keyring, KWallet) |
| Linux (fallback) | File with hardcoded key | Electron apps (no keyring) | **Effectively none** |

### Electron `safeStorage` (used by Cursor, Windsurf, VS Code)

```javascript
// Encrypt
const encrypted = safeStorage.encryptString('my-api-key');
// Store encrypted buffer in SQLite or file

// Decrypt
const decrypted = safeStorage.decryptString(encrypted);
```

Technical details:
- Thin wrapper (~100 LOC C++) around Chromium's `OSCrypt`
- AES-128-CBC encryption
- Key derived from OS credential store
- **Linux caveat**: Without a running Secret Service, falls back to a hardcoded
  password, providing no real security

### Git Credential Manager (reference implementation)

GCM is the gold standard for cross-platform credential storage in CLI tools:

| Store | Platform | Mechanism |
|-------|----------|-----------|
| `wincredman` | Windows | Windows Credential Manager (DPAPI) |
| `dpapi` | Windows | DPAPI-encrypted files in `%USERPROFILE%\.gcm\` |
| `keychain` | macOS | macOS Keychain |
| `secretservice` | Linux | libsecret/Secret Service API |
| `gpg` | Linux | GPG-encrypted files |
| `cache` | All | In-memory, no persistence |
| `plaintext` | All | **Plaintext file** (last resort) |

### References

- [Electron safeStorage](https://www.electronjs.org/docs/latest/api/safe-storage)
- [Git Credential Manager credential stores](https://github.com/git-ecosystem/git-credential-manager/blob/release/docs/credstores.md)
- [VS Code Secret Storage discussion](https://github.com/microsoft/vscode-discussions/discussions/748)

---

## 13. .NET Ecosystem Patterns

### `dotnet user-secrets` (development only)

**Storage:**
- Windows: `%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json`
- macOS/Linux: `~/.microsoft/usersecrets/<UserSecretsId>/secrets.json`

**Encryption: None.** Plaintext JSON. Explicitly documented as "not a trusted
store" -- development only.

```bash
# Initialize
dotnet user-secrets init

# Set a secret
dotnet user-secrets set "Slack:BotToken" "xoxb-..."

# Access in code
builder.Configuration.AddUserSecrets<Program>();
var token = config["Slack:BotToken"];
```

### ASP.NET Core Data Protection API (production)

The Data Protection API is the .NET ecosystem's answer to cross-platform
credential encryption:

**Default behavior (Windows):**
- Keys stored in `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys`
- Encrypted at rest with DPAPI

**Default behavior (Linux/macOS):**
- Keys stored in user home directory
- **NOT encrypted at rest** by default (no DPAPI equivalent)
- Must explicitly configure encryption

**Configuration options:**

```csharp
// File system with certificate encryption
services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("/keys"))
    .ProtectKeysWithCertificate(cert);

// Database with Azure Key Vault
services.AddDataProtection()
    .PersistKeysToDbContext<MyDbContext>()
    .ProtectKeysWithAzureKeyVault(keyUri, credential);

// Redis
services.AddDataProtection()
    .PersistKeysToStackExchangeRedis(redis, "DataProtection-Keys");
```

**Key protection options:**
- X.509 certificate (cross-platform)
- Windows DPAPI (Windows only)
- Windows DPAPI-NG with certificate rule (Windows Server 2012 R2+)
- Azure Key Vault
- Null protector (no encryption -- for testing)

### Production patterns for self-hosted .NET apps

1. **Azure Key Vault**: `Azure.Extensions.AspNetCore.Configuration.Secrets`
   package. Secrets appear as regular `IConfiguration` keys.
2. **Environment variables**: Standard `IConfiguration` provider, highest
   priority in the default chain.
3. **Docker secrets**: Mounted as files, read via file-based config provider.
4. **Data Protection API**: For encrypting/decrypting arbitrary data at the
   application level, with pluggable key storage and protection backends.

### References

- [ASP.NET Core App Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets)
- [Data Protection Configuration](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview)
- [Key Storage Providers](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-storage-providers)
- [Secrets Management in .NET (Auth0)](https://auth0.com/blog/secret-management-in-dotnet-applications/)

---

## Comparative Analysis

### Encryption at rest

| Project | Encrypted at rest? | Method | Key management |
|---------|-------------------|--------|----------------|
| Claude Code (macOS) | Yes | OS Keychain | OS-managed |
| Claude Code (Linux) | **No** | Plaintext JSON | N/A |
| OpenCode | **No** | Plaintext JSON | N/A |
| Cursor/Windsurf | Yes* | Electron safeStorage (AES-128) | OS keychain |
| Open WebUI | Partial | Fernet for OAuth | App env var |
| AnythingLLM | **No** | Plaintext in SQLite/.env | N/A |
| LibreChat | Yes | AES (custom key/IV) | User-provided env vars |
| Home Assistant | **No** (secrets) / Hashed (passwords) | bcrypt for passwords | N/A |
| n8n | Yes | AES-256 | Auto-generated or env var |
| Activepieces | Yes | AES-256 | User-provided env var |
| Botpress | Yes (cloud) | Platform-managed | Platform-managed |
| Rasa + Vault | Yes | Vault Transit Engine | Vault-managed |

*Cursor/Windsurf on Linux without a keyring daemon: effectively no encryption.

### Credential provision methods

| Method | Used By |
|--------|---------|
| Environment variables | All projects |
| Web UI form | Open WebUI, AnythingLLM, LibreChat, n8n, Activepieces, Botpress, Home Assistant |
| CLI interactive | Claude Code, OpenCode |
| Config file | Home Assistant (secrets.yaml), Rasa (credentials.yml), MCP (.mcp.json) |
| OAuth browser flow | Claude Code, n8n, Activepieces, Botpress, Home Assistant, Docker MCP |
| OS keychain | Claude Code (macOS), Electron apps |
| External vault | n8n, Rasa, Docker MCP Gateway |
| Shell script helper | Claude Code (apiKeyHelper) |

### Subprocess credential passing

| Method | Used By | Security Model |
|--------|---------|---------------|
| Env var injection at spawn | MCP servers, Docker, n8n | Process isolation |
| Stdin/stdout pipe | MCP stdio transport | Process isolation |
| File mount | Docker secrets, Kubernetes | Filesystem permissions |
| Runtime vault fetch | n8n external secrets, Rasa | Network + auth |
| HTTP header injection | LibreChat MCP, Docker Gateway | TLS + auth |

---

## Patterns Relevant to Netclaw

Given Netclaw is a self-hosted .NET homelab assistant with Slack integration:

### Immediate patterns to consider

1. **Home Assistant's `secrets.yaml` model**: Simple, well-understood by homelab
   users. Plaintext but separated from config. Effective for single-user,
   single-machine deployments where filesystem = trust boundary.

2. **n8n's encryption model**: AES-256 with an auto-generated key stored on
   first run. Good balance of security and usability. The key can be
   user-provided via env var for advanced deployments.

3. **ASP.NET Core Data Protection**: Native .NET, cross-platform, pluggable
   backends. Could encrypt credentials at rest with DPAPI on Windows or
   X.509 certificate on Linux. Already part of the framework.

4. **`dotnet user-secrets` for development**: Keep Slack tokens and API keys
   out of `appsettings.json` during development. Already standard .NET practice.

5. **Environment variable injection for tools/MCP**: When Netclaw spawns tool
   subprocesses, pass credentials via env vars (not command-line args, which
   appear in `ps` output).

### Architecture decision points

- **Single-user homelab**: Home Assistant's model (plaintext secrets file,
  filesystem trust boundary) is arguably sufficient and is the simplest to
  implement and support.
- **Multi-user or shared access**: n8n/LibreChat's model (AES encryption with
  app-managed key) becomes necessary.
- **Enterprise/corporate**: External vault integration (n8n's pattern) or
  ASP.NET Core Data Protection with Azure Key Vault.

### Implementation tiers

**Tier 1 (MVP):**
- `appsettings.json` + environment variable overrides for provider API keys
- `dotnet user-secrets` for development
- Slack OAuth tokens stored in Akka persistence (serialized state)
- No encryption at rest beyond filesystem permissions

**Tier 2 (hardened):**
- ASP.NET Core Data Protection for encrypting stored credentials
- Auto-generated encryption key on first run (n8n pattern)
- `NETCLAW_ENCRYPTION_KEY` env var override for advanced deployments
- Separate secrets from config (Home Assistant pattern)

**Tier 3 (enterprise):**
- Azure Key Vault / HashiCorp Vault integration via `IConfiguration`
- External secret resolution at runtime (n8n external secrets pattern)
- Per-user credential isolation

---

## Key Takeaways

1. **Almost no one encrypts credentials at rest in single-user self-hosted
   tools.** Claude Code, OpenCode, Home Assistant, AnythingLLM -- all store
   plaintext. The security model is "if they have filesystem access, game over."

2. **Multi-user web apps encrypt because they must.** n8n, LibreChat, and
   Activepieces all encrypt because their database might be on a shared server
   or backed up to cloud storage.

3. **OS keychain is the gold standard for desktop/CLI tools** but has poor
   Linux support (requires a running keyring daemon, which headless servers
   don't have).

4. **Environment variables are the universal credential transport** -- every
   single project uses them for at least some credentials.

5. **The "secret reference" pattern is rare** outside of workflow tools. Most
   projects either embed credentials or use env vars. LibreChat's
   `${VAR_NAME}` and n8n's `={{ $secrets.vault.name }}` are notable exceptions.

6. **OAuth token lifecycle management is uniformly poor.** Most projects store
   refresh tokens but handle rotation ad-hoc. Only Rasa (via Vault) and Docker
   MCP Gateway have structured token lifecycle management.

7. **The .NET ecosystem has strong primitives** (Data Protection API, user-secrets,
   IConfiguration) that most of these projects would benefit from if they were
   .NET-based. Netclaw can leverage these immediately.
