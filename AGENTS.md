## work flow

- if you need a paragraph-long comment to justify why the workaround is ok, the code is wrong — fix the code.

- use the available code-navigation, research, build, and inspection tools proactively. gather evidence before making claims or changing unfamiliar code.
- define a concrete verification step before implementing. after code changes, perform static review and report verification that was not run.
- fix root causes. do not suppress errors, weaken checks, or add compatibility code without a demonstrated need.
- for a small, well-scoped change, implement it directly.
- for unfamiliar or multi-file work, first inspect the relevant code, state the data flow and success criterion, then make the smallest correct change.
- keep explanations concise and distinguish static code evidence from runtime or network verification.
- preserve existing user and agent changes. never revert or delete unrelated work.
- report what changed, the verification command and outcome, and any limitation that prevents verification.

## research

- keep context focused: inspect only the files and documentation needed for the task.
- use `research-tools` proactively for current external behavior, api documentation, browser flags, installer syntax, and network-service changes.
- before changing `browserarguments()` or `corewebview2environmentoptions.additionalbrowserarguments`, use `microsoft_docs_search` and `microsoft_docs_fetch` to read the current official microsoft guidance for webview2 browser flags.

## application architecture

- this is a `net8.0-windows` winforms app that hosts webview2. keep lifecycle, network interception, and ui-thread work explicit.
- preserve the webview2 static-loader setup unless a clean build and published output verify the change.
- asset-manifest failures must leave original requests unmodified. async webview2 handlers must complete deferrals correctly.
- keep released application versions aligned between the project and installer.

## execution safety

- never build, test, run, install, publish, launch an application, or invoke a project script.
- do not execute `dotnet`, `iscc.exe`, npm package scripts, installers, or project executables. limit verification to static code and diff review.
