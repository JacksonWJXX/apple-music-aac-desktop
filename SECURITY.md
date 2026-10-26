# Local credential and publication safety

- The application never extracts browser Cookies or reads a browser profile/OS credential store. Login guidance is manual; the masked field is populated only by the user's own paste action.
- Token values, complete Cookie text, licenses, keys and signed media URLs must never be placed in Issues, screenshots, logs or commits. Errors use fixed categories. Worker stdout/stderr are discarded.
- Optional retained login uses Windows DPAPI CurrentUser in `private/login.dpapi`, with a private per-user directory ACL. The checkbox affects the next save only: unchecking does not silently remove an existing vault. Explicit Clear Login removes saved/current login. DPAPI does not protect against malware already running as that user.
- Temporary plaintext exists only for the pinned library's Cookie-file interface. Normal completion/cancel removes tool-owned copies; startup clears stale tool job directories. The selected original import file is never deleted.
- The scan is explicitly user-triggered, non-recursive and filename-limited, inspecting only the app import folder and a user-owned Downloads/Download/下载 folder (or child) the user chooses. Templates, links, AppData itself/children, known browser/portable-profile paths, profile marker files and disk roots are excluded. No scheduled scan, browser extraction or telemetry exists.
- All account-bearing headers (including Media-User-Token, case-insensitively) are restricted to exact Apple account hosts in `network_guard.py`. Other media/CDN hosts use a positive public-header allowlist. Only HTTPS on the default port or explicit 443 is permitted. Unexpected destinations or headers stop the task; do not weaken this check to make an error disappear.
- Private settings, DPAPI ciphertext, runtimes and media are excluded from publication. `.gitignore` alone is insufficient: use the exact publish allowlist and independent review before pushing.
- The source installer requires an explicit risk acknowledgment before dependency installation. Downloading a file does not establish rights for commercial use, redistribution or analysis under applicable terms.

For a problem report, provide only a fixed error category and steps with fake URLs/tokens. Never attach the private directory or import file. This repository does not operate a credential collection service.
