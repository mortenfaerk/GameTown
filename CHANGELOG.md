# Changelog

All notable changes to this project will be documented in this file.
## [0.5.1] - 2026-08-25
- Bump release version to 0.5.1
- Added better  error handling, so a contributor wil see a proper error message if the system doesn't have write access to it library path.
## [0.5.0] - 2026-08-14
- Bump release version to 0.5.0.
- Add initial release notes for the 0.5.0 release:
  - IGDB migration and metadata provider improvements (migration 007 referenced).
  - Box art handling: improved `ImageFetcher` safety and media rehosting.
  - Zip guide writer append-only behavior and robustness improvements.
  - Tagging: slug identity enforcement and quick-add tags migration.
  - Database migrations: added safeguards and replayable scripts guidance.
  - Tests: ensured cross-platform CI compatibility; 203 tests remain the canonical suite.
  - General: documentation updates and small bug fixes across API and frontend.