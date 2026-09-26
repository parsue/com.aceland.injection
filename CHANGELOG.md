# Changelog

All notable changes to this project will be documented in this file.

---
# Release - published

## [1.0.3] - 2026-09-26
### Fixed
- Suppressed compiler warning CS0436 in the generated injector module. The Source Generator's `ModuleInitializerAttribute` polyfill could shadow an identically-named type from a referenced assembly; the benign shadowing is now silenced with a `#pragma warning disable CS0436` in the generated file.

## [1.0.2] - 2026-09-24
### Modified
- Rearrange Tools menu

## [1.0.1] - 2026-09-16
### Modified
- Licensing is now an OPTIONAL dependency. Installing AceLand Injection no longer force-installs AceLand Licensing. The runtime and builds are unaffected as always; the paid Editor tools (dependency graph, validation, diagnostics) simply stay disabled and show a one-click install prompt until AceLand Licensing is present.
- When AceLand Licensing is installed, the Editor tools automatically restore their full license flow — no manual setup required.

## [1.0.0] - 2026-09-07
### Added
- Open Core & Paid Editor Tools model: the runtime container, Source Generator, and Abstractions stay free & open source forever; only the development-time Editor tools (dependency graph, validation, diagnostics) require a paid AceLand Injection license.
- Editor licensing flow (subscription and one-time perpetual purchase) with offline signed-token verification, machine-id seat binding, and a reusable license window.
- "Buy Once" button alongside the existing subscription button on all entitlement-gated Editor windows.
### Notes
- First stable (non-experimental) release. Runtime and builds are never gated.

---
# Exp - published

## [0.3.3] - 2026-08-26
### Modified
- code optimized
- [Graph] separate columns of installers and registers, add linked curve to show relationship 

## [0.3.2] - 2026-08-25
### Added
- [Graph] show issue on missing Lifetime Scope

## [0.3.1] - 2026-08-25
### Modified
- [ACEDI005] compiler warning on [Inject] private member without `partial` keyword

## [0.3.0] - 2026-08-24
### Added
- Editor Tester for internal test. Not user-concern item.
### Fixed
- [Source Generator] cold initial cost
- [Source Generator] Emitter dead code
- [Source Generator] support lower version of Unity (2022.3 or later)

--- 
# Beta - published

## [0.2.2] - 2026-08-14
- [Graph] fixed open script fail on script is not in same filename
- [Sample] added sample to learn 

## [0.2.1] - 2026-08-13
- fixed graph issues
- add installer to graph

## [0.2.0] - 2026-08-13
- beta published

---
# Dev - unpublished

## [0.1.1] - 2026-08-12
dev optimize and bug fix

## [0.1.0] - 2026-08-11
Repo created, project in dev level, not published.   
For detail please visit and bookmark our [GitBook](https://aceland-workshop.gitbook.io/aceland-unity-packages/)
