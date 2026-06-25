# Demo AppHost


## Demo AppHost


For "show me Netclaw working end-to-end" or "I want to kick the tires
without setting up Slack and provider accounts," point the user at the
self-contained .NET Aspire demo under `samples/Netclaw.Demo.AppHost/`.

```text
dotnet run --project samples/Netclaw.Demo.AppHost
```

One command brings up a containerized Mattermost (seeded with admin,
team, bot, access token, default channel, and a test user), a
containerized Ollama with `qwen3.5:2b-q4_K_M` pulled and cached, and the
Netclaw daemon as an Aspire project resource sandboxed via
`NETCLAW_HOME` so nothing touches a host-installed `~/.netclaw/`.
Default credentials and the seeded channel name are printed to the
Aspire dashboard.

Key facts to share with the operator:

- Aspire dashboard at <http://localhost:15294>; Mattermost web UI URL is
  visible there under the `mattermost` resource's `web` endpoint
  (port allocated dynamically).
- Default Mattermost login for the demo's non-admin test user:
  `testuser` / `TestUser1234!`. Admin is `admin` / `Admin1234!`.
- The demo launches the `fast` profile by default. It keeps the seeded
  Mattermost channel on the `public` audience, caps tool loops
  aggressively, disables Ollama thinking mode, tunes Ollama for
  single-user local inference, and prewarms the model before the
  daemon starts.
- For the heavier tool-rich path, opt into
  `NETCLAW_DEMO_PROFILE=full dotnet run --project samples/Netclaw.Demo.AppHost`.
- Daemon binds `127.0.0.1:5299` (not the production default 5199, so
  it never collides with a host-installed daemon).
- `fast` is materially quicker on CPU than the old demo path, but GPU is
  still the best experience; the README documents the
  `WithGPUSupport(OllamaGpuVendor.Nvidia)` opt-in for snappy demos.
- Clean reset: `rm -rf samples/Netclaw.Demo.AppHost/.demo-home/` plus
  `docker volume rm` for the Ollama volume.

The demo is for evaluation, not production. It uses `mattermost-preview`
(deprecated upstream but self-contained), runs the daemon as a host
process (containerizing collides with `ExposureMode.Local` + loopback
auth — see the README's "Why the daemon isn't containerized" section),
and ships with no custom `netclaw.json` so the default secure posture
applies.
