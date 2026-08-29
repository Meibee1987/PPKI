# PPKI MVP Rule Coverage

Generated deterministically from `Ppki.Application.RuleCoverageManifest`. Do not edit this table manually.

Target rules: 34; Implemented: 25; Partial: 0; Manual/non-automated: 9.

- **Implemented**: a registered deterministic validator covers the catalog requirement and has real automated tests.
- **Partial**: compiled validation covers only part of the official requirement; the requirement is not weakened.
- **Manual/non-automated**: no compiled validator is currently claimed; reviewer judgment remains required.

| RuleCode | ValidationKey | Status | Implementation version | Fixer capability | Test coverage |
|---|---|---|---|---|---|
| `PPKI-ABS-001` | `abstract.skripsi-language-pair` | Implemented | `1.0` | — | `Wave1AbstractValidatorTests` |
| `PPKI-ABS-003` | `abstract.skripsi-narrative-paragraph-count-one` | Implemented | `1.0` | — | `Wave1AbstractValidatorTests` |
| `PPKI-ABS-004` | `abstract.skripsi-word-count-max-200` | Implemented | `1.0` | — | `Wave1AbstractValidatorTests` |
| `PPKI-ABS-007` | — | Manual/non-automated | — | — | — |
| `PPKI-ABS-009` | — | Manual/non-automated | — | — | — |
| `PPKI-ABS-011` | `abstract.skripsi-single-spacing-zero-paragraph-spacing` | Implemented | `1.0` | `abstract-spacing-direct-paragraph@1.0` | `Wave1AbstractValidatorTests` |
| `PPKI-ABS-013` | `summary.thesis-dissertation-language-pair` | Implemented | `1.0` | — | `Wave1AbstractValidatorTests` |
| `PPKI-ABS-019` | `abstract-summary-single-spacing-zero-paragraph-spacing` | Implemented | `1.0` | `abstract-spacing-direct-paragraph@1.0` | `Wave1AbstractValidatorTests` |
| `PPKI-FIG-003` | — | Manual/non-automated | — | — | — |
| `PPKI-FIG-007` | — | Manual/non-automated | — | — | — |
| `PPKI-HDG-001` | `heading.chapter-number-upper-roman-no-period` | Implemented | `1.0` | — | `Wave1HeadingValidatorTests` |
| `PPKI-HDG-002` | `heading.maximum-depth-3` | Implemented | `1.0` | — | `Wave1HeadingValidatorTests` |
| `PPKI-HDG-003` | `heading.chapter-uppercase` | Implemented | `1.0` | — | `Wave1HeadingValidatorTests` |
| `PPKI-HDG-004` | `heading.chapter-bold` | Implemented | `1.0` | `chapter-bold-direct-heading-runs@1.0` | `Wave1HeadingValidatorTests` |
| `PPKI-HDG-005` | `heading.chapter-no-period-no-underline` | Implemented | `1.0` | `chapter-decoration-direct-heading-runs@1.0` | `Wave1HeadingValidatorTests` |
| `PPKI-HDG-006` | `heading.chapter-centered` | Implemented | `1.0` | `chapter-centered-direct-paragraph@1.0` | `Wave1HeadingValidatorTests` |
| `PPKI-HDG-007` | `heading.subheading-decimal-left` | Implemented | `1.0` | `subheading-left-direct-paragraph@1.0` | `Wave1HeadingValidatorTests` |
| `PPKI-HDG-008` | — | Manual/non-automated | — | — | — |
| `PPKI-HDG-009` | `heading.subheading-bold-no-period-no-underline` | Implemented | `1.0` | `subheading-decoration-direct-heading-runs@1.0` | `Wave1HeadingValidatorTests` |
| `PPKI-HDG-011` | `heading.subsubheading-decimal-left` | Implemented | `1.0` | `subsubheading-left-direct-paragraph@1.0` | `Wave1HeadingValidatorTests` |
| `PPKI-HDG-013` | `heading.subsubheading-regular-no-period-no-underline` | Implemented | `1.0` | `subsubheading-decoration-direct-heading-runs@1.0` | `Wave1HeadingValidatorTests` |
| `PPKI-LAY-003` | `section.page-size-a4` | Implemented | `1.0` | `section-page-size-a4@1.0` | `SectionPageLayoutFixProviderTests` |
| `PPKI-LAY-005` | `body.font-times-new-roman-12` | Implemented | `1.0` | `body-font-direct-run@1.0` | `BodyFontSizeFixProviderTests` |
| `PPKI-LAY-008` | `section.margin-left-4cm` | Implemented | `1.0` | `section-margin-direct@1.0` | `SectionPageLayoutFixProviderTests` |
| `PPKI-LAY-009` | `section.margin-right-3cm` | Implemented | `1.0` | `section-margin-direct@1.0` | `SectionPageLayoutFixProviderTests` |
| `PPKI-LAY-010` | `section.margin-top-3cm` | Implemented | `1.0` | `section-margin-direct@1.0` | `SectionPageLayoutFixProviderTests` |
| `PPKI-LAY-011` | `section.margin-bottom-3cm` | Implemented | `1.0` | `section-margin-direct@1.0` | `SectionPageLayoutFixProviderTests` |
| `PPKI-LAY-017` | `body.line-spacing-single` | Implemented | `1.0` | `body-line-spacing-direct-paragraph@1.0` | `ParagraphFormatFixProviderTests` |
| `PPKI-LAY-018` | `body.first-line-indent-1cm` | Implemented | `1.0` | `body-first-line-indent-direct-paragraph@1.0` | `ParagraphFormatFixProviderTests` |
| `PPKI-LAY-019` | `body.justified` | Implemented | `1.0` | `body-justified-direct-paragraph@1.0` | `ParagraphFormatFixProviderTests` |
| `PPKI-STR-001` | — | Manual/non-automated | — | — | — |
| `PPKI-STR-021` | — | Manual/non-automated | — | — | — |
| `PPKI-STR-022` | — | Manual/non-automated | — | — | — |
| `PPKI-TBL-012` | — | Manual/non-automated | — | — | — |
