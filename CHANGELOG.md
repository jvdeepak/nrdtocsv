# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.4.2] - 2026-02-22
### Changed
- Analyze mode now switches to Parquet-aware behavior when pipeline mode is enabled
- Parquet analyze summary now reports complete/partial/outdated/missing/orphaned status against NRD source files

### Fixed
- Orphaned parquet counting is deduplicated by instrument/date pair

## [1.4.1] - 2026-02-22
### Added
- Configurable parallel worker count in UI (supports sequential mode with `1`)
- Manifest persistence for all pipeline settings (bridge command, bridge working directory, flags, selected paths, parallel workers)

### Fixed
- Analyze action UI-thread ownership crash caused by background access to WPF controls
- Bridge execution robustness for quoted paths and working directory handling

## [1.4.0] - 2026-02-22
### Added
- Optional Parquet pipeline mode (`NRD -> temp CSV -> Parquet`) in the NinjaTrader AddOn UI
- Bridge command integration for `csv-to-parquet-bridge` execution from conversion flow
- Parquet bridge working-directory setting for reliable `uv` project resolution
- Workspace persistence for Parquet pipeline settings

### Changed
- Pipeline execution now launches the bridge process directly (without `cmd /c`) for safer argument handling
- Conversion flow supports per-file Parquet handoff with optional temp CSV cleanup
- Manifest settings now persist both CSV root and Parquet root directories

## [1.3.0] - 2026-02-22
### Added
- Analyze mode to verify CSV completeness against NRD source data
- Export manifest tracking with cached complete/partial status
- Force re-export option to bypass manifest decisions
- Persistent CSV destination path restore on startup
- Append-mode recovery logic with overlap detection and gap handling
- Additional conversion status notifications and exception handling

### Changed
- UI/UX improvements and responsiveness updates
- Date handling so CSV timestamps align with source file date
- Parallel conversion tuned for safer default drive load behavior

## [1.2.0] - 2021-09-13
### Added
- Skip already converted files
- Estimated time to complete (ETA)
- Summary for complete files and bytes to proceed

### Changed
- Limit parallel conversion to 4 threads<br>
  (depending on a hard drive load (checked with `resmon` command)<br>
  you can adjust it with the `PARALLEL_THREADS_COUNT` constant)

### Fixed
- Fix minor typos

## [1.1.0] - 2021-09-12
### Added
- Progress bar indicator
- Ability to cancel conversion

### Changed
- Limit parallel conversion to 8 threads
  (enough for most CPU and Hard Drives loads during conversion)

## 1.0.0 - 2021-09-09
### Added
- Ability to specify output `*.csv` root directory
- Filter of `*.nrd` file names to convert based on RedExp

[Unreleased]: https://github.com/eugeneilyin/nrdtocsv/compare/v1.4.2...HEAD
[1.4.2]: https://github.com/eugeneilyin/nrdtocsv/compare/v1.4.1...v1.4.2
[1.4.1]: https://github.com/eugeneilyin/nrdtocsv/compare/v1.4.0...v1.4.1
[1.4.0]: https://github.com/eugeneilyin/nrdtocsv/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/eugeneilyin/nrdtocsv/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/eugeneilyin/nrdtocsv/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/eugeneilyin/nrdtocsv/compare/v1.0.0...v1.1.0
