# Santa vision

- **Category:** Strategy
- **Status:** Current
- **Last reviewed:** 2026-09-16

## Purpose

Santa is the local memory and retrieval layer for a person's Claude Code and Codex
work. It turns transcripts already stored on that person's machine into a private,
searchable history so they can find prior reasoning, understand what happened, and
return to the right session without reconstructing context by hand.

Santa succeeds when an old agent session is easy to find from an approximate memory,
its useful context is quick to inspect, and continuing the work takes one deliberate
action. The primary experience is a terminal UI, with equivalent commands for scripts
and focused workflows. Transcript contents, derived metadata, models, and indexes stay
on the user's machine apart from summarisation or classification performed through the
configured agent CLI backends.

## Work Santa owns

Santa owns work that makes local agent-session history usable:

- **Ingest and project session history:** read supported Claude Code and Codex transcript
  formats, preserve progress across incremental refreshes, and turn noisy event streams
  into useful sessions, turns, and searchable chunks.
- **Retrieve and browse:** provide keyword and on-device semantic search, related-session
  discovery, recent-session views, and a fast terminal browser over the indexed history.
- **Add retrieval metadata:** derive titles and summaries, track session and usage
  metadata, and apply user-selected classification recipes when those results improve
  finding or managing sessions.
- **Manage the session lifecycle:** show and export transcripts, mark sessions complete,
  and resume a selected session in its original agent CLI or through a supported handoff.
- **Operate the local index:** own Santa's database schema, local model acquisition,
  embedding and reranking behavior, refresh workflow, and clear failure or fallback
  behavior for supported inference providers.
- **Maintain narrow companion contracts:** keep deliberate integrations with tools such
  as cockpit stable where they help select or resume a session, without absorbing those
  tools' broader responsibilities.

This ownership is about the capability, not a promise that every possible transcript
source, model runtime, interface, or integration belongs in Santa. New work should
strengthen the path from local session history to finding, understanding, or resuming a
session.

## Boundaries

Santa does not own:

- running or coordinating agents, allocating work among them, or supervising active
  multi-agent execution;
- project source control, worktrees, code review, delivery tracking, or deployment;
- a canonical project knowledge base, requirements system, or general-purpose document
  search engine;
- cloud transcript storage, cross-user sharing, account management, or device sync;
- a general model-serving platform beyond the local inference needed by Santa's own
  retrieval workflow; or
- rich IDE, web, or desktop interfaces unless a concrete Santa retrieval or resume need
  justifies them.

Cockpit and agent-fusion may consume or hand off Santa results, but live session control
and multi-agent orchestration remain their work. The agent CLIs remain the source of the
transcripts and the authority for actually resuming a session.

## Product decisions

When deciding whether Santa should claim a piece of work, prefer work that:

1. shortens the path from a fuzzy recollection to the right session;
2. improves the fidelity, freshness, or inspectability of the local history;
3. makes resuming or closing a session safer and more predictable; and
4. preserves local ownership of the index and explicit behavior when dependencies are
   unavailable.

Work that mainly coordinates active agents, manages project delivery, or creates shared
remote knowledge should live in the tool responsible for that job, with only the smallest
integration surface added to Santa.
