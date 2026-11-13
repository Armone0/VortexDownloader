# Security Policy

## Supported versions

Only the [latest release](https://github.com/Armone0/VortexDownloader/releases/latest) receives security fixes.

## Reporting a vulnerability

Please **do not** open a public issue. Report it privately via
[GitHub Security Advisories](https://github.com/Armone0/VortexDownloader/security/advisories/new).

Include the version, steps to reproduce and the impact you expect. You can expect a first reply within a few days.

Areas we especially care about: the local browser bridge (`127.0.0.1:19741`) and extension pairing, archive
extraction (path traversal, decompression bombs), and anything that could write outside the chosen download folder.
