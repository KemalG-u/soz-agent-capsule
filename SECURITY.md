# Security Policy

## Supported versions

| Version | Supported |
|---|---|
| latest commit on `main` | yes |
| older | no |

## Reporting a vulnerability

Please do **not** open a public issue for a security problem.

Report it privately with GitHub's private vulnerability reporting: open the repository's **Security** tab and choose **Report a vulnerability** (https://github.com/KemalG-u/soz-agent-capsule/security/advisories/new).

Include: affected version, a description, steps to reproduce, and the impact you see. Never include real secrets or personal data in a report; describe where they appear instead.

## What to expect

This is a one-person project maintained on a best-effort basis. The maintainer aims to send an initial response within **14 days** of a report. A fix date is not promised; the maintainer will tell you what is planned.

## Scope

In scope: the Söz command-line tool, local server and desktop shell in this repository (the guard rules, the evidence gate, the secret filter, the local HTTP API and the optional phone endpoint).

Out of scope: weaknesses of the third-party agent tools Söz starts (Claude Code, Codex), and attacks that require control of the Windows account Söz runs under. The README ("Limits") states this boundary; the guard is a care layer, not a security boundary, and the security claims have not been independently verified.

## Repository hygiene

If you find a secret or personal data that was missed in this repository, report it through the private channel above.
