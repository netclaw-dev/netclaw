# Microsoft Teams app package

This directory contains the source for the Netclaw Microsoft Teams app package.

The package grants only personal and team bot scopes. It requests the single
team-scoped RSC permission `ChannelMessage.Read.Group` so Teams can deliver an
unmentioned reply in an established bot thread. It does not request group-chat,
meeting, tab, calling, video, file, or message-write permissions.

That RSC permission delivers standard channel messages to the bot endpoint for
the installed team. Netclaw admits an unmentioned message only when it is from
the same approved human in a root that they established with a genuine bot
mention. It discards all other unmentioned messages before a session or model
turn.

## Build the package

Use public HTTPS pages for your privacy policy and terms of use.

```powershell
$BuildPackage = @{
    AppId = '00000000-0000-0000-0000-000000000000'
    DeveloperName = 'Example Operator'
    PrivacyUrl = 'https://example.com/privacy'
    TermsOfUseUrl = 'https://example.com/terms'
    OutputPath = './artifacts/netclaw-teams.zip'
    Version = '1.0.0'
    Verbose = $true
}

./build-package.ps1 @BuildPackage
```

The developer name has a 32-character limit. Increase the semantic version for
each package update. The team owner must approve the requested RSC permission
when they upgrade or reinstall the package in the team.

The ZIP file contains these three files at its root:

- `manifest.json`
- `color.png`
- `outline.png`

Do not commit an operator-specific package. The generated manifest contains
your app registration ID and your policy URLs.

See [the Microsoft Teams runbook](../../docs/integrations/microsoft-teams-channel.md)
for registration, deployment, health, rotation, and rollback procedures.
