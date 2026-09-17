# Security Policy

## Supported versions

dotNotes is pre-1.0. Only the latest commit on `main` receives security fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Report privately via GitHub's
private vulnerability reporting: go to the repository's **Security** tab
and choose **Report a vulnerability**.

Include what you found, steps to reproduce, and the affected version or
commit if you know it.

## What to expect

This is a personal, single-maintainer project with no formal SLA. I'll
aim to acknowledge reports within a couple of weeks and fix confirmed
issues as time allows. Please allow a reasonable window before public
disclosure.

Note that dotNotes is designed for single-user, local/LAN self-hosting
and has no built-in authentication beyond per-note share tokens — exposing
it directly to the internet without a reverse proxy and auth in front is
not a supported configuration (see `DEPLOYMENT.md`).
