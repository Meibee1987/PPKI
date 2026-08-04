# Audit scoring contract

S3-T01 defines a deterministic calculator but deliberately configures no
production scoring policy. The repository contained an undocumented worker
formula with hard-coded Error/Warning weights. That formula was not a formal,
versioned PPKI policy, so new completed audits now persist `score = null` and
the audit summary reports `NotConfigured` instead of inventing a number.

## States

- `Calculated`: a completed audit was evaluated with a valid explicit policy.
- `NotConfigured`: no formal policy snapshot/version is available.
- `InvalidConfiguration`: the policy, applicable count, or finding snapshot is
  invalid.
- `NotApplicable`: a completed audit has zero applicable rules.
- `AuditIncomplete`: the audit is not `Completed` (including validator failure
  or unsupported/invalid validation that caused the audit to fail).

Counts remain available for every state. A non-calculated state always has a
null numeric score. Existing legacy database values are retained for schema
compatibility but are not treated as a valid score by the new audit summary.

## Versioned calculator contract

`AuditScoringPolicy` requires every input explicitly: version, minimum and
maximum score, maximum penalty (the denominator), Error/Warning/Info weights,
decimal places, and midpoint-rounding mode. There are no default weights.
Supported rounding is `ToEven` or `AwayFromZero`, and calculations use
`decimal` arithmetic.

For a supplied valid policy only, the aggregation contract is **PerFinding**:
each persisted semantic finding contributes exactly one configured severity
penalty. The calculator does not group by rule code. Two findings for the same
rule at different locations are two valid violations and are both scored.

This is not a second deduplication stage. Sprint 02 removes exact semantic
duplicates before persistence using rule code, compact location, property, and
normalized actual value. Consequently the calculator accepts persisted finding
rows as its already-deduplicated input contract. `DistinctViolatedRules` in the
breakdown is explanatory only; `ScoredFindingCount` and the penalty sum drive
the score. The calculator then applies the policy's explicit normalized
contract:

```text
boundedPenalty = min(sum(persisted-finding penalties), maximumPenalty)
score = maximumScore
      - (boundedPenalty / maximumPenalty)
        * (maximumScore - minimumScore)
```

The result is rounded exactly as the policy says and clamped to its configured
range. Info has no penalty only when the policy explicitly sets its weight to
zero. The calculator consumes only audit status, applicable-rule count,
persisted rule-code/severity snapshots, and the supplied policy. It has no
live-rule, time, random, parser, HTTP, or storage dependency.

A production policy must not be applied retroactively from live configuration.
Before enabling numeric scores, a future separately scoped change must persist
the policy version (or immutable policy snapshot) on the audit and resolve that
exact version. S3-T01 makes no migration and does not alter the resolved-rule
set or its hash.

Not included: score UI, export/blocking semantics, auto-fix, or any PPKI policy
weights that have not been formally approved.
