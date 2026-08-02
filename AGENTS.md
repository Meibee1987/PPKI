# Coding guidance

- Preserve the original uploaded DOCX. Every mutation must produce a new `DocumentVersion`.
- A rule must show its source, actual value, expected value, location, severity, and fix mode.
- Keep validators deterministic. Do not add generative AI to mechanical formatting rules.
- Add golden DOCX fixtures for every parser or fixer change.
- Never log thesis content or complete paragraph text.
- Treat `rules/ppki-ipb-2019/rules.json` as source data; validators remain compiled code selected by `ValidationKey`.
