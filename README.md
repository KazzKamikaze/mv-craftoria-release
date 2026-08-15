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
- In-place updates preserve profile identity and play-time metadata.
- Player saves, controls, maps, screenshots, shader selection, logs, caches, and Distant Horizons data are not managed.

## Release assets

Every GitHub release must contain:

- `MV-Craftoria-VERSION.zip`
- `mv-release.json`
- `mv-release.sig`

Build them with `tools/build-release.ps1`. The private signing key must never be committed or uploaded.
