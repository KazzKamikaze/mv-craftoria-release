# MV Craftoria Updater

Windows installer and updater for the private MV Craftoria CurseForge profile.

## Distribution model

- GitHub Releases host the current complete managed client payload.
- `mv-release.json` is signed with the offline MV ECDSA release key.
- The updater embeds only the public key and rejects unsigned or modified releases.
- Fresh installs and in-place updates use the same executable.
- Installed clients are selected explicitly from a CurseForge profile dropdown.
- Every verified GitHub release is available from a version dropdown.
- A release can update only the selected client or install as a separate client.
- Separate clients receive unique CurseForge GUIDs, names, and folders.
- Fresh installs are registered by CurseForge in the background; no import dialogs or file pickers are shown.
- In-place updates advance the displayed profile name to the installed version while preserving the profile GUID, path, group, and play-time metadata.
- Failed or cancelled operations remove downloads, staging data, failed backups, and incomplete new profiles.
- Abandoned updater work directories are cleaned on the next launch.
- Player saves, controls, maps, screenshots, shader selection, logs, caches, and Distant Horizons data are not managed.

## Release assets

Every GitHub release must contain:

- `MV-Craftoria-VERSION.zip`
- `MV-Craftoria-VERSION-MANUAL-INSTALL-CurseForge.zip` for manual CurseForge imports
- `MV-Craftoria-VERSION-UPDATER-DATA.zip` for the updater only; users must not import it
- `mv-release.json`
- `mv-release.sig`
- `MV-Craftoria-Updater.exe`

Build them with `tools/build-release.ps1`. The private signing key must never be committed or uploaded.

Publish a prepared version directory with `tools/publish-release.ps1`. It uses the existing Git Credential
Manager session and never writes the GitHub credential to disk or command output.
