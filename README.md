# santa

Local semantic search, browse, and resume over your **Claude Code and Codex**
transcripts. Everything runs on-device — embeddings, reranking, and the vector
index never leave your machine.

You talk to a lot of agents. santa makes that history searchable: *"that postgres
migration we argued about"*, *"the auth refactor from last week"* — find the session,
read it, and resume it where you left off.

The main way to use santa is its full-screen **TUI**:

```bash
santa tui          # browse + search your whole history, resume with one key
```

Everything it does is also a plain command, for scripting or muscle memory:

```bash
santa query "the flaky test we kept fighting"   # one-shot hybrid search
santa recent                                     # recent sessions, grouped by project
santa refresh                                    # re-index (the TUI also does this on launch)
```

## Install

Needs the **.NET 10 SDK** (the build publishes a single-file binary; the runtime is
framework-dependent, so it's small).

```bash
./install.sh                         # GPU ONNX Runtime by default
santa models download
santa refresh
```

`santa models download` pulls two ONNX models (embedding + reranker) from HuggingFace
and the [`sqlite-vec`](https://github.com/asg017/sqlite-vec) extension from GitHub,
then works fully offline. CUDA is the default inference provider.

Before loading a CUDA model, Santa requires three consecutive idle readings from
`nvidia-smi` (at most 10% utilization and 25% VRAM in use). Santa and Shepherd share
`~/.local/state/agent-tooling/gpu-inference.lock`, so their inference passes cannot
compete. A busy or unavailable GPU defers the command; it never silently falls back
to CPU. Use an explicit CPU build and provider when wanted:

```bash
SANTA_ONNX_RUNTIME_FLAVOR=cpu ./install.sh
santa devices --provider cpu
santa refresh --provider cpu
```

CUDA embedding is memory-bounded: batches are limited by their padded token area,
the embedding allocator is capped at 7 GiB, and the reranker allocator at 2 GiB.
This keeps a single maximum-length transcript turn from padding a large batch and
exhausting a 12 GiB GPU.

If CUDA initialization fails, the command fails rather than silently continuing with
BM25. Use `--provider keyword-only` when BM25-only operation is what you intended.

## The TUI

`santa tui` is the main interface — a full-screen, keyboard-driven browser over your
whole session history. It runs a silent incremental index on launch, so it's always
current; you rarely need `santa refresh` by hand.

Two tabs, switched with `Tab`:

- **Browse** — every session as a scrollable list (date · cwd · turns · duration ·
  title), newest first. A green `●` marks sessions running *right now*; `✓` marks ones
  you've completed; `cx` tags Codex sessions. A detail pane shows the summary.
- **Search** — hybrid BM25 + on-device vector search with a reranker. Type a query,
  hit Enter; the match panel shows the snippet and the `bm25`/`vec`/`fused`/`rerank`
  scores.

### Keys

| Key | Browse | Search |
|-----|--------|--------|
| `↑` `↓` · `PgUp` `PgDn` · `Home` `End` | move selection | move through results |
| `Tab` | switch tab | switch tab |
| `/` | live filter (title · cwd · branch · summary) | edit the query |
| `Enter` | expand the detail pane | run the search |
| `s` | toggle the detail pane | — |
| `c` | mark session completed (hide from default search) | — |
| `r` | **resume** → cockpit if attached, else a new terminal | resume the hit |
| `t` | resume in a **new terminal** (ignore the cockpit handoff) | same |
| `v` | toggle stacked ↔ side-by-side layout (≥140 cols) | same |
| `Ctrl-O` | theme picker (live preview, `Enter` saves) | same |
| `Ctrl-Q` | quit | quit |

Resume is the payoff: land on a session, press `r`, and you're back in it — a fresh
terminal tab, or dropped straight into a [cockpit](https://github.com/OvenBaker/cockpit)
pane if you launched the TUI from there (`cockpit --santa`). Theme and layout choices
persist across runs.

Flags: `--provider cpu|cuda|keyword-only` · `--keyword-only` (compatibility alias
for BM25-only) · `--no-refresh` (skip the launch-time index) · `--device <id>`
(pick a GPU when using CUDA).

## Where state lives

Everything — the index DB, the downloaded models, and native libs — lives under
`~/.local/share/santa/` (XDG-respecting). Override the whole tree with `SANTA_HOME`.
The index is built from *your* transcripts and is never committed or shared.

> Upgrading from a `santa-claude` install? `install.sh` migrates the old
> `~/.local/share/santa-claude/` state dir automatically, and the binary still
> honours `SANTA_CLAUDE_HOME` as a deprecated alias for `SANTA_HOME`.

## Scriptable commands

Everything the TUI does, minus the UI — for piping, scripts, or muscle memory.

| Command | Does |
|---------|------|
| `santa tui` | The full-screen interface above. |
| `santa refresh` | Incrementally index new/changed sessions; CUDA inference when the GPU is idle. |
| `santa query <text>` | Hybrid semantic + keyword search across all history. |
| `santa recent` | Recently-touched sessions, grouped by project. |
| `santa related <id>` | Sessions semantically near a given one. |
| `santa show <id>` / `export <id>` | Read or dump a full transcript. |
| `santa resume <id>` | Re-open a session in its original CLI (Claude or Codex). |

## Companion tools

santa stands alone, but it's built to compose with two siblings:

- **[cockpit](https://github.com/OvenBaker/cockpit)** — a tmux control surface that
  resumes several sessions at once into one live grid. `santa resume` can hand a
  session straight to it.
- **[agent-fusion](https://github.com/OvenBaker/agent-fusion)** — a multi-agent
  harness that runs Claude + Codex on one task and fuses the output.

See the [agent-tooling](https://github.com/OvenBaker/agent-tooling) umbrella for how
the three fit together.

## Warts

Personal tooling, shared as-is. No tests, no CI. Built and run on **WSL + Linux**.
The build surfaces an `NU1903` advisory on a transitive `SQLitePCLRaw` native
package — noted, not yet bumped. PRs welcome; expectations modest.

## License

[The Unlicense](LICENSE) — released into the public domain. Do whatever you want.
