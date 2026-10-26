# Source-only release procedure

1. Start from a fresh sanitized staging directory, never the live application directory.
2. Copy only the exact entries in `publish-allowlist.json`. Do not use `git add .` on a task or live-tool workspace.
3. Run `python tools/audit_release.py`; review every listed path/hash and any failed check. Do not read private files to satisfy the check.
4. Run synthetic tests in a separate build copy; do not copy generated configs, runtime, test results, screenshots with personal paths or private state back into staging.
5. Obtain independent privacy/license review of the exact manifest. Confirm repository owner, new repository name and public visibility before creation/push.
6. Stage files explicitly from the allowlist, review `git diff --cached --stat` and the content, then publish only after approval. Avoid uploading a complete local folder as a ZIP.
7. GitHub's automatic source archives are sufficient. Any manually generated source archive must use the same allowlist. This project does not ship an integrated runtime or dependency/device binaries.

The setup/build workflow is documented in README. A new computer must install its own Python/FFmpeg/dependencies and provide fresh local login. Existing credentials are deliberately not portable or included.
