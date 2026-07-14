# ADR-0017: Hand-rolled specification pattern instead of Ardalis.Specification

- **Status**: accepted
- **Date**: 2026-07-14

## Context

The original plan used Ardalis.Specification (MIT, free, well-maintained) for domain query objects. But it would have been the **only third-party dependency in the Domain layer**, in a solution whose philosophy is hand-rolled, dependency-free Core code (hand-rolled CQRS handlers, Result, strongly-typed IDs) for AI-legibility and zero license exposure. The user confirmed the hand-rolled direction.

## Decision

`XTemplate.SharedKernel.Specifications.Specification<T>` — ~80 lines: `Where` (AND-composed), `OrderBy`/`OrderByDescending` (chained as ThenBy), `Paginate`, plus `ApplyTo(IQueryable<T>)` (expression-preserving, EF-translatable) and `Evaluate(IEnumerable<T>)` for unit tests.

Deliberately absent:

- **Includes** — aggregates always load whole; child navigations use EF `AutoInclude` in entity configurations. A partial aggregate is a bug, not a feature.
- Projections, composition operators, caching — YAGNI at template scale.

## Consequences

- Domain is 100% BCL + SharedKernel; nothing in Core can be commercialized out from under template consumers.
- If a project outgrows the minimal shape (projection specs, spec composition), Ardalis.Specification (MIT) shares the same usage pattern and is the documented upgrade path — swapping the base class is mechanical.
- The reflection-based OrderBy plumbing in `ApplyTo` is the one non-obvious spot; it is commented and covered by unit tests.
