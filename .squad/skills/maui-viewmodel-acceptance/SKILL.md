---
name: "maui-viewmodel-acceptance"
description: "Patterns for testing MAUI view-model UX flows without pulling the full MAUI app project into the test assembly"
domain: "testing"
confidence: "medium"
source: "earned"
---

## Context

Use this skill when the repo already has a lightweight test project that links selected MAUI app source files directly. It is especially useful for command gating, empty/loading/error states, and navigation behavior that should be proven without XAML runtime dependencies.

## Patterns

- **Link only the app logic under test:** Add `ViewModelBase`, the target view-models, and any pure controller/state files to the test project instead of referencing the MAUI app project.
- **Shim MAUI-only seams locally:** A minimal `MainThread` shim and a lightweight `IAppNavigator` test interface implementation are enough for view-model tests when the production logic only needs dispatcher invocation and navigation calls.
- **Prefer recording doubles over mocks:** Small fakes that capture start/get/reconnect/reset call counts and last commands make acceptance expectations obvious in test output.
- **Use scripted services for state transitions:** Queue catalog responses and lifecycle outcomes so one test can prove loading, empty, invalid, retry, and resume transitions deterministically.
- **Keep shell scaffold tests isolated:** If another engineer is validating XAML routes or app shell composition, leave those tests untouched and codify UX behavior at the view-model seam instead.

## Examples

- `tests\SquadScout.App.Tests\SquadScout.App.Tests.csproj`
- `tests\SquadScout.App.Tests\ViewModelTestDoubles.cs`
- `tests\SquadScout.App.Tests\ProjectSelectionViewModelTests.cs`
- `tests\SquadScout.App.Tests\ActiveSessionViewModelTests.cs`

## Anti-Patterns

- Do not pull the full MAUI app project into the unit-test assembly just to reach dispatcher or navigation abstractions.
- Do not hide acceptance expectations inside giant integration tests when the behavior is already encoded in view-model command gates and state projections.
- Do not mutate unrelated shell scaffold tests when parallel work is already using them as a merge lane.
