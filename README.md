# santa

Local semantic search, browse, and resume over your **Claude Code and Codex**
transcripts. Everything runs on-device — embeddings, reranking, and the vector
index never leave your machine.

You talk to a lot of agents. santa makes that history searchable: *"that postgres
migration we argued about"*, *"the auth refactor from last week"* — find the session,
read it, and resume it where you left off.

```bash
santa refresh                # index ~/.claude/projects + ~/.codex/sessions
santa query "the flaky test we kept fighting"
santa recent                 # what you worked on lately, grouped by project
santa                        # interactive TUI: browse → resume
```

## Install

Needs the **.NET 10 SDK** (the build publishes a single-file binary; the runtime is
framework-dependent, so it's small).

```bash
./install.sh                 # dotnet publish → ~/.local/bin/santa
santa refresh                # first run downloads the embed + rerank models
```

On first `refresh`, santa pulls two small ONNX models (embedding + reranker) from
HuggingFace and the [`sqlite-vec`](https://github.com/asg017/sqlite-vec) extension
from GitHub, then works fully offline. A CUDA build of onnxruntime ships in the box
and is used automatically if a GPU is present; otherwise it falls back to CPU.

## Where state lives

Everything — the index DB, the downloaded models, and native libs — lives under
`~/.local/share/santa/` (XDG-respecting). Override the whole tree with `SANTA_HOME`.
The index is built from *your* transcripts and is never committed or shared.

> Upgrading from a `santa-claude` install? `install.sh` migrates the old
> `~/.local/share/santa-claude/` state dir automatically, and the binary still
> honours `SANTA_CLAUDE_HOME` as a deprecated alias for `SANTA_HOME`.

## Commands

| Command | Does |
|---------|------|
| `santa refresh` | Incrementally index new/changed sessions. |
| `santa query <text>` | Hybrid semantic + keyword search across all history. |
| `santa recent` | Recently-touched sessions, grouped by project. |
| `santa related <id>` | Sessions semantically near a given one. |
| `santa show <id>` / `export <id>` | Read or dump a full transcript. |
| `santa resume <id>` | Re-open a session in its original CLI (Claude or Codex). |
| `santa` | Interactive TUI — browse, search, resume from the keyboard. |

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
