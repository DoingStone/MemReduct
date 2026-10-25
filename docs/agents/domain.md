# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Layout: multi-context

This repo's domain-doc layout was confirmed at setup time as **multi-context**: the subprojects do not share one vocabulary, so each context keeps its own `GLOSSARY.md`, and a root `GLOSSARY-MAP.md` points at them.

Neither `GLOSSARY-MAP.md` nor any per-context `GLOSSARY.md` exists yet. They are created lazily, the first time a real term actually gets written down — the root `GLOSSARY-MAP.md` then points at each context's `GLOSSARY.md`.

## Before exploring, read these

- **`GLOSSARY-MAP.md`** at the repo root if it exists: it points at one `GLOSSARY.md` per context. Read each one relevant to the topic.
- **`GLOSSARY.md`** inside the context you're about to work in, if that context has one — `src/MemReduct.Core/GLOSSARY.md`, `src/MemReduct.Cli/GLOSSARY.md`, `src/MemReduct.Gui/GLOSSARY.md`.
- **`docs/adr/`**: read ADRs that touch the area you're about to work in. In multi-context repos, also check `src/<context>/docs/adr/` for context-scoped decisions.

If any of these files don't exist, **proceed silently**. Don't flag their absence; don't suggest creating them upfront. The `/domain-modeling` skill (reached via `/grill-with-docs` and `/improve-codebase-architecture`) creates them lazily when terms or decisions actually get resolved.

## File structure

```
/
├── GLOSSARY-MAP.md
├── docs/adr/                          ← system-wide decisions
└── src/
    ├── MemReduct.Core/
    │   ├── GLOSSARY.md
    │   └── docs/adr/                  ← context-specific decisions
    ├── MemReduct.Cli/
    │   ├── GLOSSARY.md
    │   └── docs/adr/
    └── MemReduct.Gui/
        ├── GLOSSARY.md
        └── docs/adr/
```

## Use the glossary's vocabulary

When your output names a domain concept (in an issue title, a refactor proposal, a hypothesis, a test name), use the term as defined in `GLOSSARY.md`. Don't drift to synonyms the glossary explicitly avoids.

If the concept you need isn't in the glossary yet, that's a signal: either you're inventing language the project doesn't use (reconsider) or there's a real gap (note it for `/domain-modeling`).

## Flag ADR conflicts

If your output contradicts an existing ADR, surface it explicitly rather than silently overriding:

> _Contradicts ADR-0007 (event-sourced orders), but worth reopening because…_
