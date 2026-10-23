# WPF Application Constitution

## Core Principles

### I. Readability and Simplicity Are Non-Negotiable

- Code MUST be understandable and maintainable by another experienced C# developer without needing an explanation from its author.
- Use precise names, cohesive classes, focused methods, explicit control flow, and consistent conventions. Prefer ordinary C# over clever, implicit, reflection-heavy, or excessively generic solutions.
- Follow the repository's established conventions and `.editorconfig`. Use supported C#/.NET features appropriately; do not introduce competing styles for the same problem.
- Keep complexity proportional to the problem. Extract a method, class, or component when it creates a meaningful responsibility boundary, removes important duplication, or materially improves clarity or testability—not merely to reduce line counts.
- Comments MUST explain non-obvious intent, invariants, and constraints, not restate what readable code already says. Public documentation MUST describe actual contracts.
- Avoid speculative features, configuration, extension points, and defensive code for impossible states. Prefer the smallest complete solution that satisfies the current requirements.

**Rationale:** Long-term maintainability and ease of review take precedence over architectural novelty and minimal typing.

### II. Cohesive Modules and Explicit Boundaries

- Separate presentation concerns, application behavior, domain rules (when domain complexity warrants them), and external infrastructure. Dependencies MUST point toward stable business/application abstractions rather than UI or concrete infrastructure details.
- Each component MUST have a clear responsibility and a small, meaningful public surface. Dependencies between features MUST be deliberate; avoid cross-feature access to implementation internals.
- Keep business decisions out of views, code-behind, converters, and persistence adapters. Keep presentation-specific state and formatting out of application/domain services.
- Organize code by cohesive features or responsibilities where useful. Additional assemblies/projects MUST be justified by genuine deployment, reuse, ownership, dependency, or testing boundaries—not by a mandatory architecture diagram.
- New designs MUST fit existing architecture unless a targeted change is explicitly justified. Do not impose Clean Architecture, CQRS, MediatR, repositories, event buses, or other patterns by default.

**Rationale:** Modular boundaries should reduce change cost, not create ceremony.

### III. WPF Uses MVVM With Pragmatic View Boundaries

- Implement nontrivial screen state, user actions, validation, and presentation orchestration in ViewModels. Prefer XAML bindings, commands, templates, styles, and resources over imperative manipulation of controls.
- Views and code-behind MAY contain strictly view-specific behavior (focus, animations, visual-tree interactions, drag/drop plumbing, platform events). They MUST NOT own business rules, data access, or application workflows.
- ViewModels MUST NOT depend directly on concrete Window, Page, UserControl, MessageBox, or other WPF controls. Use a small UI-facing boundary only when an interaction (dialogs, navigation, clipboard, etc.) actually requires one.
- Make binding state observable and ensure command availability, loading, empty, success, and error states remain consistent. Treat new binding errors as defects.
- Reuse styles, templates, components, and design tokens for repeated UI patterns. Do not copy and diverge XAML unnecessarily; do not build a universal control framework for one screen.
- Preserve keyboard navigation, focus behavior, usability at common display scaling settings, and clear validation/error feedback for affected views.

**Rationale:** UI logic remains reviewable and testable while WPF-specific behavior stays where it belongs.

### IV. Abstractions Must Earn Their Cost

- Introduce interfaces or base classes when they protect a real architectural boundary, enable meaningful substitution, isolate external side effects for tests, or consolidate stable repeated behavior.
- Do NOT create an `IThing` for every `Thing`, generic wrappers around simple APIs, deep inheritance chains, service locators, or pass-through layers without a concrete benefit. A single implementation MAY justify an interface at an external boundary; implementation count alone is not the deciding factor.
- Prefer composition over inheritance and direct dependencies over indirection when a boundary adds no value. Keep constructor dependencies focused; many unrelated dependencies signal misplaced responsibilities.
- Use dependency injection at an explicit application composition root for stateful services and external collaborators. Avoid hidden global mutable state and resolving arbitrary services throughout the application.
- Before introducing a new abstraction, state what complexity it removes and what foreseeable change or test it enables. If that justification is weak, use the simpler design.

**Rationale:** Overengineering is a form of technical debt.

### V. Correct Asynchrony, Threading, and Lifetime Management

- The UI thread MUST remain responsive. Use asynchronous I/O, cancellation where useful, and explicit progress/loading state for potentially long operations. Do not use `Task.Run` as a default wrapper around I/O.
- Async operations MUST use `Task`/`Task<T>` and observable failure paths. Limit `async void` to framework-required event handlers; never use it for application workflows or ViewModel command implementations.
- Do not block the UI thread with `.Wait()`, `.Result`, synchronous waits on asynchronous work, or unnecessarily expensive computation. Marshal UI-bound updates to the WPF Dispatcher when required; do not mutate thread-affine UI state from background threads.
- Prevent unintended concurrent commands, duplicate submissions, stale results, and updates to closed/disposed views. Handle cancellation separately from actionable failures.
- Explicitly manage the lifetime of subscriptions, timers, streams, cancellation sources, and disposable resources. Unsubscribe or dispose when ownership ends; avoid retaining screens/ViewModels through event handlers.
- Errors MUST be observable through appropriate logging/diagnostics and understandable UI feedback. Never silently swallow unexpected exceptions or expose secrets in logs.

**Rationale:** Desktop applications must remain responsive, predictable, and stable during long-running work and navigation.

### VI. Test Behavior at the Right Boundary

- Every meaningful behavior change MUST have automated tests at the cheapest level that provides confidence. At minimum, cover significant business rules, ViewModel state transitions/commands, validation, and regression-prone error paths introduced or changed by the task.
- ViewModels and application logic MUST be testable without starting a WPF window, connecting to live external services, or depending on arbitrary sleeps. Inject time, I/O, or other nondeterministic collaborators when actually needed for deterministic tests.
- Prefer testing observable outcomes and contracts over private methods or implementation details. Use fakes/stubs when simpler than elaborate mock setups; avoid asserting incidental call sequences.
- Use integration tests for important infrastructure contracts and interactions. For complex WPF views, add a practical UI smoke or interaction test when supported by the project; unit tests alone do not validate XAML bindings, resource lookup, layout, or focus behavior.
- Tests MUST include relevant positive, negative, cancellation, and failure scenarios where risks justify them. Do not pursue an arbitrary coverage percentage or create low-value tests solely to satisfy a metric.
- A discovered bug MUST receive a regression test at the appropriate level whenever it can be reproduced automatically.

**Rationale:** Testing verifies behavior and supports safe refactoring instead of merely increasing test counts.

### VII. Preserve Compatibility and Prevent Technical Debt

- Every change MUST leave touched code at least as understandable, consistent, and maintainable as before. Remove obsolete paths and unnecessary duplication introduced or made obsolete by the change.
- Do not knowingly introduce workarounds, hidden TODOs, disabled diagnostics, brittle coupling, copy-paste implementations, or unsupported assumptions as a shortcut to completion.
- Existing technical debt outside the task's scope MAY remain. Do not perform unrelated rewrites. If a pre-existing defect blocks a correct solution, make the smallest justified supporting refactor and explain it.
- Preserve existing external contracts, persisted data, and user-visible behavior unless the task explicitly requires change. Document migration/compatibility implications where applicable.
- If a compromise is unavoidable, disclose its limitation, reason, impact, and concrete follow-up before treating the feature as complete; obtain approval for knowingly accepting new debt. A hidden compromise is never acceptable.
- Dependencies MUST be necessary, maintained, license-compatible, and consistent with the project's existing stack. Prefer established .NET/WPF facilities or existing approved packages before adding new dependencies.

**Rationale:** Delivery speed is not measured by how much cleanup is deferred to someone else.

### VIII. Verification Is Part of Implementation

- Before coding, inspect the relevant repository structure, conventions, existing components, test patterns, and feature requirements. Prefer targeted changes over broad redesign.
- Define or confirm acceptance criteria and identify affected behavior, error paths, data flow, threading, and resource ownership before implementation.
- Implement in small, coherent steps; update or add tests alongside the behavior. Do not claim success based only on plausible-looking generated code.
- Before declaring a task complete, run the applicable restore/build, automated tests, and configured formatting/analyzer checks using the project's supported Windows/.NET toolchain. New compiler warnings, analyzer violations, failing tests, and reproducible binding errors MUST be resolved.
- Perform a self-review specifically for unnecessary abstractions, duplicated logic, UI-thread blocking, incorrect binding/notifications, error handling, lifetime leaks, missing tests, and accidental behavior changes. Fix findings before handoff.
- If validation cannot run in the execution environment, report exactly which commands were not run, why, and what remains unverified. Never invent passing results or mark unverified criteria as verified.
- Deliver a concise summary of changed behavior, important design decisions, tests actually executed, and any known limitations.

**Rationale:** A feature is complete only when its implementation and evidence of correctness are both available.

## Architecture and Engineering Decision Rules

These are decision rules, not mandatory directory or project names.

1. **Start simple:** implement the smallest cohesive design that meets documented requirements and known constraints.
2. **Place logic deliberately:** View/XAML for appearance and view mechanics; ViewModel for screen state and interaction; application services/use cases for workflows; domain types/services for nontrivial business rules; infrastructure adapters for file system, network, persistence, OS, and other external dependencies.
3. **Add a seam only when needed:** create an interface/adapter for a genuine external or volatility boundary, a needed test substitute, or a supported alternative implementation—not merely for symmetry.
4. **Reuse before creating:** inspect existing utilities, styles, behaviors, components, and services; improve an existing cohesive abstraction instead of introducing a parallel one.
5. **Choose the smallest safe refactor:** when current code prevents correct implementation or testing, refactor the relevant boundary as part of the task, supported by tests.
6. **Prefer explicit trade-offs:** if simplicity, performance, testability, and compatibility conflict, explain the trade-off in the plan. Optimize only from an actual requirement or measured problem.
7. **Respect project decisions:** framework target, MVVM library, DI container, test framework, and UI component library are selected by project context or an explicit technical decision. The constitution does not mandate replacements.

## Quality Gates and Definition of Done

Every implementation plan and task completion MUST check the applicable gates below:

- **Requirements:** acceptance criteria met; edge cases, error states, and compatibility considered.
- **Design:** responsibilities and dependencies are clear; added abstractions are justified; no unnecessary frameworks, layers, or packages.
- **WPF behavior:** bindings, property notifications, command availability, loading/error states, thread affinity, navigation/lifetimes, and affected UI behavior have been reviewed and validated where possible.
- **Tests:** meaningful tests added or updated; relevant test suites pass; regressions guarded where appropriate.
- **Tooling:** applicable build, test, formatting, and analyzer commands executed successfully; no new warnings or binding errors.
- **Maintainability:** no new undocumented debt, abandoned code, unrelated refactors, secrets, or unexplained workarounds.
- **Handoff:** summary includes changes, validation evidence, important decisions, and explicit residual risks.

A task MUST NOT be reported as complete if a mandatory applicable gate is known to fail. An environmental inability to run a gate MUST be disclosed as unverified rather than passed.

## Governance

- This constitution governs specification, planning, implementation, code review, and completion of all project changes. More specific feature specifications MAY add constraints but MUST NOT silently weaken these principles.
- When principles conflict, prioritize correctness and safety, then maintainability and testability, then simplicity and delivery cost; record important trade-offs. Do not use a principle as justification for needless complexity.
- Amendments require an explicit reason, a review of impacts on current patterns/tests, and a documented version update. A planned exception MUST name its rationale, scope, and follow-up; it does not silently amend the constitution.
- Version the constitution using semantic versioning: MAJOR for incompatible governance changes, MINOR for new or materially expanded principles, PATCH for clarifications.
- At task planning and completion, review the proposed change against the applicable principles and quality gates. Noncompliance MUST be corrected or explicitly escalated rather than ignored.

**Version:** 1.0.0 | **Ratified:** 2026-10-23 | **Last Amended:** 2026-10-23
