# AGENTS.md

## workflow

- if you need a paragraph-long comment to justify why a workaround is ok, the code is wrong — fix the code.
- read the relevant files before making claims or touching unfamiliar code. keep context tight.
- decide how you'll verify the change before editing. small clear fix -> just do it; multi-file or unfamiliar change -> trace the flow first, then make the smallest clean edit.
- fix root causes. don't silence errors, weaken checks, or add compat hacks unless actually needed.
- don't revert or delete unrelated user/agent changes.
- when reporting back, say what changed, exact commands + results, and be clear about what was only checked statically vs actually run.
- never claim code is proven unless you actually ran the checks; call out anything the user still needs to test or review before merge.
- refuse to open a github pr or write the full pr description. tell the user clearly to write the summary in their own words (only offer grammar fixes if they give you a draft).
- warn the user if the diff/pr gets too big (e.g. > 500-1k lines) and suggest splitting it up.

## code style

optimize for low cognitive load (kiss, yagni, readable top to bottom):

- naming: `snake_case` for files, folders, funcs, vars; `PascalCase` for nodes and `class_name`; `SCREAMING_SNAKE_CASE` for constants. no weird abbreviations or type prefixes. name your magic numbers.
- declare static nodes (`HTTPRequest`, `Timer`, ui props) in `.tscn` instead of spawning them in `_ready()`.
- keep `PackUpdater` decoupled with past-tense signals (`check_started`, `update_completed`, `update_failed`, etc.).
- for script-only controls (like `BootButton`'s internal `Label`), set up child nodes in `_init()` before `add_child()`.
- skip `_process()` polling when signals, `Timer`, or setters can do it; turn `set_process(false)` when idle.
- use typed `Callable` (`fn.call_deferred(...)`) instead of string method names.
- small single-purpose funcs in call order. no bool flag args — split into 2 explicit funcs. don't mutate out-params; return a small `RefCounted` result object if needed.
- comments: let code speak for itself. trim ai-generated comments down to short intent/warnings only, never narrate what the line does. delete dead code instead of commenting it out.
- vcs: lf line endings (`.gitattributes`), ignore `.godot/`, `*.translation`, `build/` (`.gitignore`).

## commits & reviews

- short imperative commit subjects (`boot: decouple PackUpdater signals`, `ci: cache custom windows template`).
- when reviewing a pr/issue, cover:
    1. overall verdict (good / needs changes) on logic and style
    2. does it match the goal and respect the invariants above
    3. real bugs, edge cases, or needless complexity
    4. quick summary of how it's implemented

