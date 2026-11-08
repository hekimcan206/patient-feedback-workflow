# Changelog

All notable changes to this project are documented here. The file is maintained by hand, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-11-07

### Added

- Signed clinical-event webhook (Standard Webhooks HMAC, replay window, persisted de-duplication, key rotation)
- Transactional outbox in Oracle with `SKIP LOCKED` claims, leases and fenced updates; channel routing with retries,
  full-jitter back-off, WhatsApp → SMS fallback and opt-out handling; fake WhatsApp/SMS providers with realistic
  failure modes
- Tokenised single-use mobile survey (Turkish UI) with NPS, CSAT and CES scoring services
- Low-score case triggering with severity and SLA, SLA breach watcher, case-tracking panel updated live via SignalR
- AES-256-GCM envelope encryption of personal data, blind indexes, audited reveal, append-only audit log
- KVKK retention policy as a PL/SQL package run nightly by `DBMS_SCHEDULER` (anonymisation, crypto-shredding, legal hold)
- Synthetic visit generator, visit simulator and one-command `docker compose up` demo

### Changed

- Array-bound audit batch and higher dispatcher concurrency: 77 → 180 messages/s end to end under a 2,000-event burst

[Unreleased]: https://github.com/hekimcan206/patient-feedback-workflow/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/hekimcan206/patient-feedback-workflow/releases/tag/v1.0.0
