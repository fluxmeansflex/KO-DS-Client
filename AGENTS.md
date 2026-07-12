## work flow

- if a workaround needs a paragraph-long comment, simplify the code instead.
- inspect the relevant markup, script, and assets before editing. fix root causes and make the smallest correct change.
- define a concrete verification step before implementation. report static evidence separately from browser or network verification.
- preserve existing user and agent changes. never revert, delete, rename, or restyle unrelated work.
- keep important behavior local and explicit. do not add frameworks, generators, helpers, or compatibility layers without a demonstrated need.

## site architecture

- this project is the static GitHub Pages site for `ko-client.online`. the entry point is `index.html`; there is no application build step or package manager.
- `index.html` contains the page structure, Tailwind utility classes, and inline JavaScript for the preview carousel, skin viewer, dialogs, and Discord widget.
- Tailwind CSS is loaded through the browser CDN. use existing utility classes and do not introduce a CSS build pipeline or standalone stylesheet unless explicitly requested.
- `<model-viewer>` is loaded from its CDN and reads local models from `assets/guns` and textures from `assets/compressed`.
- preview media lives in `assets/preview`. when files are added, removed, or renamed, update slide titles, controls, lazy-loading paths, and JavaScript collections together.
- use root-relative repository paths consistently and verify every changed local asset reference exists with matching case.

## behavior

- keep carousel state, timers, and lazy loading in one visible flow. clear or restart timers only when the requested interaction requires it.
- keep network calls visible. the Discord widget must load only when its dialog opens and retain a usable fallback link when the request fails.
- download links must target an existing GitHub Release asset. verify release/tag changes with `gh` before changing a versioned URL.
- external links opened in a new tab must use `rel="noreferrer"`. interactive controls need an accessible name and keyboard-usable native elements.
- preserve the current desktop presentation and the intentional small-screen fallback unless responsive behavior is part of the request.

## research

- use MDN for current HTML, CSS, JavaScript, dialog, media, and browser API behavior.
- use official `<model-viewer>`, Tailwind, GitHub Pages, GitHub CLI, or Discord documentation when changing those integrations.
- do not infer current CDN, API, release, or browser behavior from memory when it can be verified from the primary source.

## verification

- do not invent or invoke npm scripts, bundlers, project executables, installers, or application builds on this branch.
- do not claim browser rendering, animation timing, CDN availability, Discord responses, downloads, or deployment are verified unless they were checked directly.
