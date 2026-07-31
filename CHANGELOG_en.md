# Changelog

All notable changes to this project will be recorded in this file.

Versioning rule: major.minor.patch. The major version is used for major feature improvements, the minor version is generally updated when a new Steam Workshop release is published, and the patch version is incremented after each code-related commit, starting from 0.

## [1.2.2] - 2026-7-31
### Fixed
- Corrected the timing of the save-path state check.

## [1.2.0] - 2026-6-19
### Changed
- Migrated to the official MOD publishing format.

## [1.1.0] - 2026-06-05
### Fixed
- Fixed cloud sync API compatibility issues introduced by game version 0.107 while retaining compatibility with the then-current stable version 0.103.

## [1.0.0] - 2026-05-30
### Added
- Official release.

## [0.1.10] - 2026-05-28
### Changed
- Updated the MOD avatar to the unified JMC MOD black-and-gold badge style, emphasizing save-slot expansion and copy-and-paste features.

## [0.1.9] - 2026-05-28
### Fixed
- Fixed the delete buttons for save slots 4 and above on extended pages being intercepted by the custom-button protection logic and therefore not responding to clicks.

## [0.1.8] - 2026-05-28
### Fixed
- Fixed the copy, delete, and import buttons potentially having incorrect counts or positions when the save-slot screen was opened for the first time. They are now aligned using global coordinates in the native button layer, with one delayed correction after the layout stabilizes.

## [0.1.7] - 2026-05-27
### Changed
- Switched to JML's L10n wrapper for resolving custom MOD localization text. Config entries continue to use JML's conventional keys for automatic localization.

## [0.1.6] - 2026-05-27
### Changed
- Reorganized the source tree into configuration, events, save-slot services, state, infrastructure, and save-slot screen patches for easier maintenance.

## [0.1.5] - 2026-05-27
### Fixed
- Corrected the visual center alignment of the copy, delete, and import buttons to make their layout more symmetrical.

## [0.1.4] - 2026-05-27
### Fixed
- Fixed incomplete extended-slot buttons, uncleared copy state, and modal overlays failing to cover newly added buttons after switching save slots or reopening the save-slot screen.

- Adjusted cloud-save writes after copying or importing to avoid uploading run history and backup files individually and directly to Steam Cloud.

## [0.1.3] - 2026-05-27
### Fixed
- Corrected the positions of the delete, copy, and import buttons for the second and subsequent extended pages so they are repositioned relative to their corresponding slot cards.

## [0.1.2] - 2026-05-27
### Fixed
- Fixed page buttons losing their action registration after being reparented to a slot node, which caused the next-page button to fall back to the native confirmation dialog for deleting slot 0.

- Fixed a formatting error caused by localization variables not being passed when initializing the vanilla-save import source buttons.

## [0.1.1] - 2026-05-27
### Changed
- Changed the save-slot action buttons to use icon styling consistent with the native delete button, and corrected the positioning of the previous-page and page navigation buttons.

### Added
- Each MOD save slot can independently select vanilla slot 1-3 as its import source. Overwriting an existing slot continues to use the native confirmation dialog.

## [0.1.0] - 2026-05-26
### Added
- Initial feature release: save-slot copy and paste, vanilla-save import into MOD saves, configurable additional save slots, and pagination with three slots per page.
