# AgriForecast

## Read this first

**Project knowledge lives in [`AGENT_KNOWLEDGE_RESCUED.md`](AGENT_KNOWLEDGE_RESCUED.md)** (1,021
lines) — stack, repo layout, architecture, the data corpus, what is built versus roadmap, the
security audit register, test invariants, and nine recorded contradictions. Read it before acting.

⚠ **It is a rescue, not a survey.** Everything in it was extracted from the old per-project agent
definitions and is dated **2026-06-23 to 2026-07-09**. None of it was re-verified against the code
when it was written down (2026-08-25). Treat it as a strong starting hypothesis, not as current
truth: check anything you are about to rely on, and correct this file when you find it stale.

## Agents

**This project uses the portable agent fleet** (`~/.claude/agents/`), the same team used on every
project: `orchestrator` · `architect` · `backend-engineer` · `frontend-engineer` · `data-engineer` ·
`qa-engineer` · `code-reviewer` · `security-engineer` · `devops-engineer` ·
`ai-integration-engineer` · `document-verifier` · `ml-engineer` · `data-analyst` · `client-liaison`.
They preload the shared `team-charter` skill and carry portable craft as global skills; each keeps
its project memory at `.claude/agent-memory/<agent>/`, which stays here and never travels.

The nine `agri-*` definitions in `.claude/agents/` are **superseded**. They were retired from the
global agent set on 2026-08-25 because they mixed engineering craft (now in the fleet) with project
facts (now in the rescued file). They are kept here only as the historical record they were —
**do not route work to them, and do not treat their contents as current**; several carry claims
their own bodies contradict.

## Known traps carried forward from the rescue

- **The frontmatter of the old ML and backend agents advertises MLflow and a three-model
  ensemble that does not exist.** The real stack is a file registry with Model A (pooled XGBoost)
  only; Prophet and LSTM are roadmap. This mattered because the *wrong* description was the
  machine-readable one.
- **The repo root is here** (`Projects/Agri_Forecast/Project/Agri_Forecast`), verified by
  `src/` and the git root. The outer `Projects/Agri_Forecast/` is a wrapper folder. An older
  iCloud path with a curly apostrophe is retired — if it reappears it is an artifact, not work.
- **Secrets:** an earlier belief that no secrets were git-tracked is recorded as **false**. See the
  security section of the rescued file before assuming anything about credentials.
- **One contradiction is genuinely unresolved** (whether a `_build_X` duplication was fixed) and
  needs checking against the code, not against notes.
