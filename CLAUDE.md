# CLAUDE.md

Project instructions for the BarberShop app. These override default behavior.

## Workflow

- Before applying edits, show me a concise summary of all proposed changes (which files, what changes) for review. Do not edit files directly until I approve.

## Approach to any task/fix

- For any task, first look at the best/traditional industry-standard way to solve it, then assess whether it's feasible to implement in this project. Recommend that approach (or explain why it doesn't fit) rather than defaulting to the first thing that works.
- When fixing something, search the codebase for other instances of the same code/logic/pattern and check whether the same problem exists there too. Report the other occurrences.
- Enumerate the cases and edge cases of the workflow in question, then judge which are actually worth fixing (by impact/likelihood) rather than blindly fixing or ignoring all of them. Surface the ones you're skipping and why.
- When I ask you to check, review, or audit a category of thing ("all inputs", "the toasts", "every endpoint"), treat it as exhaustive: run a systematic search for EVERY instance across the codebase before answering — don't sample, and don't rely only on the files already open in context. Report the full count and list, and state explicitly that the pass is complete (or name what you couldn't cover and why). Never present a partial check as if it were the whole, and if you realise a check was incomplete, say so up front rather than waiting to be asked.
- Blast radius of a change. When a change touches something shared — a DB column or constraint, a model field, a function signature, an endpoint or request/response contract, an enum — treat it as having a blast radius. Before calling it done, search the codebase for and update EVERY site that produces or consumes it, not just the path being edited. Specifically: adding a required/NOT-NULL/unique column → every insert path must set it; renaming or retyping a field → every reader and writer; changing a signature or contract → every caller and both sides of the wire. Enumerate these by searching (e.g. every `new X`, every caller, every `fetch`/endpoint), and verify by exercising the affected paths — a clean build is NOT proof, since many of these fail only at runtime.
- User-flow lens. For anything user-facing (auth, redirects, navigation, checkout, error/empty states), reason from the user's point of view FIRST: trace what they actually see and do, step by step — screen by screen, and across tabs/devices where relevant — and judge whether that sequence is good UX. Technical correctness ("it's stateless / secure / it works") is a SEPARATE axis; never let it stand in for a flow answer. When I ask "is this how it's usually done," compare the user-facing behavior to how mainstream products behave, not just whether the code is right. If both lenses matter, give both and lead with the experiential one.

## Communication Style

- Keep explanations concise. Skip verbose code walkthroughs unless I ask for one.
- Don't propose low-value "nice to have" fixes (e.g. RowVersion, speculative guards). Judge whether a follow-up is worth it and say so, rather than reflexively offering it.

## Project Setup / Dev Servers

- This project has a separate frontend and backend. Confirm which one I mean before starting dev servers.
- The backend runs on its configured port — do not guess ports (e.g. 5199).

## Version Control

- When I ask you to commit, commit EVERYTHING that's uncommitted, including changes unrelated to what we just worked on — don't leave anything behind.
- Split it into separate commits, one per concern, rather than one large commit. If some files contain changes belonging to more than one concern, say so and tell me how you grouped them.

## Security

- Never write real or real-looking credentials into `.env.example` or any committed file. Use placeholder values only.

## Verification

- After confirming edits, review the changed code for bugs or mistakes in project logic and flow.
