# Hermes Handoff: Doc Sync to Round 172

> **From**: Codex review session (post-AGENTS.md sync, commit `2d6bb6b`)
> **Date**: 2026-06-07
> **Target**: Hermes (and sub-agents) — Codex will not action these; Hermes is source of truth
> **Status**: Pending

## Context

A Codex review pass synced `AGENTS.md` to Round 172 ground truth (commit `2d6bb6b`, 99+/68-). During the sync, 5 doc-related issues surfaced that Codex cannot responsibly fix itself (it has no source-of-truth on Hermes's own code/docs). Handing off as structured tasks below.

The new `AGENTS.md §7` introduces a "硬门禁 (历史峰值)" + "相对门禁 (±5%)" split — please read it first to understand the perf framing referenced in Task 5.

---

## Tasks (priority order)

### Task 1 — Sync `docs/architecture.md` to current code

- **Issue**: Says "12 阶段" but `FrameScheduler.RunWavePhase` is 13 logical phases (counted including `BeginFrame` as phase 1). SystemGroup / SystemRegistry pattern is undocumented. Pipeline diagram in §3 is stale.
- **Where**: `docs/architecture.md`
- **Action**:
  1. Verify phase count from `Core/FrameScheduler.cs:RunWavePhase` and the 12 `Core/*Group.cs` files
  2. Add a "SystemGroup 模式" subsection describing `SystemRegistry` (4-step: 属性 / CreateAll / WireDependencies / AssignToGroups) and the 12 group classes
  3. Update §3 pipeline diagram to match current ordering
- **Acceptance**:
  - [ ] Phase count matches `ls Core/*Group.cs | wc -l` (= 12) plus the orchestrator
  - [ ] `SystemRegistry` mentioned with file size (~726 行) and 4-step flow
  - [ ] Pipeline diagram matches `FrameScheduler.RunWavePhase` order

### Task 2 — Sync `docs/design-and-bugs.md` to Round 172

- **Issue**: Section "二、Bug 追踪" says "48 项 Bug 全部已修复" (likely pre-Round 100). Hundreds of new fixes have landed in CHANGELOG Round 100–172. The 11 项设计治理 table is also stale.
- **Where**: `docs/design-and-bugs.md`
- **Action**: Decide framing — keep "48 Bug" as historical, or evolve to Round-keyed table. Either way, the bug-count claim must be honest about what window it covers.
- **Acceptance**:
  - [ ] Bug count claim is bounded by a Round range (e.g., "48 项 pre-Round 100" or "N 项 total to Round 172")
  - [ ] Round 100+ incremental fixes summarized (link to CHANGELOG ranges is fine)

### Task 3 — Sync `README.md` to current bench / test numbers

- **Issue**: "性能基准" table shows mode 2=7687 / mode 4=3221 / mode 5=2895 (Round 171). CHANGELOG Round 172 shows 7400 / 3436 / 2675. 测试数量 696 → 946 also stale.
- **Where**: `README.md`
- **Action**:
  1. Update bench table to latest CHANGELOG row
  2. Update test count
  3. Decide whether to keep the 7-row "优化婕旇繘" history table (it documents perf evolution but is getting long)
- **Acceptance**:
  - [ ] mode 2/4/5 numbers match CHANGELOG latest (or N-1 if latest is unstable)
  - [ ] Test count matches latest CHANGELOG
  - [ ] Either trim history table or add a "see CHANGELOG for full history" pointer

### Task 4 — Add a doc-quality guardrail to the build pipeline

- **Issue**: Pre-sync `AGENTS.md` had 350 instances of U+FFFD (replacement char) — leftover from GBK→UTF-8 conversion of Chinese punctuation (`,` `（` `）`). The fix in commit `2d6bb6b` cleaned it, but Hermes's own file-write path could re-introduce it.
- **Where**: `tools/` (new) OR `Program.cs` exit-time check OR `.editorconfig` + pre-commit hook
- **Action**: Add a check that fails the build / commit if any `*.md` file under the repo contains U+FFFD.
- **Acceptance**:
  - [ ] A check exists: any commit touching `*.md` has 0 U+FFFD chars in the diff
  - [ ] Documented in `AGENTS.md §8` as part of the pre-commit checklist
  - [ ] Existing 350 chars (now fixed in AGENTS.md) cannot regress

### Task 5 — Confirm or reject the hard/relative gate split

- **Issue**: New `AGENTS.md §7` (commit `2d6bb6b`) splits performance gates into "硬门禁 (历史峰值)" and "相对门禁 (±5%)". CHANGELOG already uses ⚠️ markers when mode 2/4/5 regress >5% — this matches. Please decide whether to propagate.
- **Where**: `docs/architecture.md` (and possibly `README.md`)
- **Action**:
  1. Read `AGENTS.md §7` (commit `2d6bb6b`)
  2. If accepted: add a one-paragraph "Performance discipline" note in `architecture.md §6/§7` referencing the split
  3. If rejected: leave a comment below explaining why
- **Acceptance**:
  - [ ] Either `architecture.md` references the hard/relative gate split, OR a comment in this handoff doc explaining the rejection

---

## Out of scope (do not action)

- `CHANGELOG.md` — managed by your sub-agent, leave alone
- `docs/philosophy.md` — content is timeless, no sync needed
- `Research/tower_defense_knowledge.md` — knowledge base, separate maintenance
- `BattleSystemECS.csproj` / `BattleSystemECS.Tests.csproj` — .NET 6→8 upgrade suggestion from Codex; Hermes controls the SDK target

## Post-completion

After completing Tasks 1–5, append a completion block at the bottom of this file:

```
## Completion log
- Date: YYYY-MM-DD
- Tasks done: 1 ☑ 2 ☑ 3 ☑ 4 ☑ 5 ☑ (or list what was skipped + why)
- Commit hash(es): <hash1>, <hash2>, ...
- Test count after sync: NNN/NNN (should still be 946/946 minimum)
- Notes: <any caveat>
```

Then move/rename this file to `Research/handoff-doc-sync-round172.done.md` (or delete if the completion log itself is the only artifact needed).