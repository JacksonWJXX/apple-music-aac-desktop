# Third-party code and distribution boundaries

The MIT license in this repository applies to the original GUI, orchestration, validation, tests and documentation here. Dependencies retain their respective licenses; this is not a claim that the whole installed dependency graph is MIT.

## Reviewed primary dependencies

| Component | Pin / reviewed source | Declared license / finding |
| --- | --- | --- |
| Enhanced applemusic-dl | 2.4.12; `d31ea49f826d99b380c911d39c38ef4c9d6546b1` | Repository LICENSE is Unlicense; wheel metadata omits a license field. Copy of the reviewed repository license is under LICENSES. |
| gamdl | 3.8.5; `478c3f26464b499f3a6b875c4ceb1d3c9fc2eefc` | MIT. Upstream wheel contains a native Rust engine and an emulator-derived WVD device profile. Neither is bundled in this repository/release. |
| pywidevine | 1.9.0 | Declares GPL-3.0-only. |
| mutagen | 1.48.1 | Declares GPL-2.0-or-later. |
| Unidecode | 1.4.0 | Metadata says GPL; classifier says GPLv2+. Do not infer a more precise license than the upstream terms. |
| certifi | 2026.10.23 | Declares MPL-2.0. |
| construct | 2.8.8 | MIT; source-only release. The locked source archive is built locally. |
| FFmpeg / ffprobe | User-installed, not bundled | License depends on the selected build. The full GPL build tested locally is not redistributed here. Review your chosen FFmpeg build's notices. |
| Python / Windows .NET Framework | User-installed / OS component | Their own licenses. Not bundled here. |

`docs/dependency-license-metadata.json` records the actual declared metadata for all 42 locked runtime distributions, including BSD, MIT, Apache, PSF, GPL and other licenses. It is an inventory, not a completed legal clearance. Ambiguous/omitted declarations are retained as such rather than invented.

Primary upstream sources:
- https://github.com/Casper-Feehily/AppleMusic-Downloader-enhanced
- https://github.com/glomatico/gamdl
- https://github.com/devine-dl/pywidevine
- https://ffmpeg.org/legal.html

## What must not be redistributed in this project's source/release

No user tokens (including DPAPI ciphertext), Cookie files, downloaded music, logs, account databases, machine configuration, virtual environments, dependency wheels, APK/APKM files, extracted Apple libraries, CDM/device/emulator files, `.wvd`, native dependency binaries or media caches. The source release is an allowlisted set of original code/docs plus license notices.

The setup script installs pinned upstream packages on the user's own computer. That does **not** establish redistribution rights for all contents of those packages or grant rights to use their device material. In particular, the bundled profile in the upstream gamdl distribution is a material provenance constraint: no separate device profile is supplied, extracted or published here. If you cannot lawfully use upstream's distribution and authentication/decryption mechanism, do not configure the download engine.

The bootstrap has not yet been independently tested on a fresh Windows computer. Runtime supply artifacts are hash-locked, but build tooling is version-pinned without artifact hashes, and local source builds are not claimed to be bit-for-bit reproducible. Source publication is not clearance for a combined runtime distribution.

Do not publish a combined standalone runtime/installer until a separate dependency, native-code, device-material and copyleft compliance review is complete. Preserve the notices/source obligations of any GPL/LGPL components if you choose to redistribute them in a different distribution. No such combined binary release is prepared here.
