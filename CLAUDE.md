# Interview Coach

Windows desktop app (WPF, .NET 10) for practising job interviews with an LLM. Read `docs/HANDOFF.md` for the full picture and
`SPEC.md` for the original spec; **SPEC.md section 14 overrides the spec where they differ.** The handoff has current status,
architecture, decisions, measurements and traps. The code and tests are the truth; the handoff is the map.

## Commands

```
dotnet run --project src/InterviewCoach.App
dotnet test                                    # all tests (1212 at last count)
dotnet build InterviewCoach.sln -c Release     # use -c Release when the user has the Debug exe running
```

- A running app locks its DLLs (MSB3021). Do not kill the user's process; verify with `-c Release`.
- Migrations: from `src/InterviewCoach.Infrastructure`, `dotnet ef migrations add <Name> --startup-project ../InterviewCoach.App --output-dir Persistence/Migrations`.
- Screenshots of the screens in light and dark: set `ICOACH_SNAPSHOTS=<folder>`, run `dotnet test tests/InterviewCoach.App.Tests --filter VisualSnapshots`.

## Layout

`src/InterviewCoach.Core` (engines, models, prompt rendering; no UI or SDKs), `src/InterviewCoach.Infrastructure` (LLM, SQLite,
settings, documents, fakes), `src/InterviewCoach.App` (WPF, MVVM, DI host), `tests/*`, `tools/IconGenerator`.
Prompts are files in `src/InterviewCoach.App/Prompts/`.

## Rules for working here

- **Tests must pass.** Add or update tests with every behaviour change. App tests run real views and fail on binding errors.
- **Prompts are cache-sensitive.** In `coach.md` the text before `=== THE CANDIDATE AND THE QUESTION ===` has no variables and that
  header appears exactly once; per-call values go after `</candidate_resume>`. `PromptLibraryTests` guards both. Read handoff section 4
  before editing a prompt, and record prompt changes in SPEC.md section 14.
- **When a prompt asks the model to vary or avoid something and the log shows it does not, move the choice into code** (the resume focus, the first word, the near-duplicate check). Prose failed three times.
- **Every question type in `question_generator.md` needs an example of the right length.** Models obey examples and ignore bare limits (only the technical type had examples and only it was short).
- **Never put an example opening in a prompt.** Models copy it word for word (every coach answer began "Sure. Short version:" after the prompt said to open that way). `CoachText.WithoutOpeningFiller` is the safety net.
- **Do not use `RadioButton` groups** for choices (state is shared across view instances); bind to view model properties.
- **Styles on themed controls need `BasedOn`; custom templates need an explicit `Foreground`.** Colours come from Fluent theme
  `DynamicResource` keys, never hard-coded, so dark mode works.
- **No `ConfigureAwait(false)` in Core**; engine events must reach the UI thread.
- Keep user-visible wording plain (no milestones or internals).
- Update SPEC.md section 14 and `docs/HANDOFF.md` when behaviour or status changes.
- Tooling on this machine: in the Bash tool, heredocs containing apostrophes fail; write files with the file tool or a script file.
- The folder is a git repository. Commit one coherent change at a time: imperative subject (`<area>: <what>`, about 60 characters) and a body saying what and why. Do not push without the user's say-so.

## Current status

All seven milestones are done (4 Practice, 5 voice, 6 Mock Interview with its debrief, 7 History, resuming, notice bar, shortcuts). What is left is in HANDOFF section 25 (known limits) and the ideas listed there.
Practice and Mock Interview share `AnswerComposer` (the answer box with the microphone) and `Speaker` (the voice). Learn and Practice share `QuestionPicker`; both record to the Library (`LearnHistory`, `PracticeAttempts`).
The Concepts page (sidebar) runs Learn or Practice on chosen technologies at a difficulty with no resume or job description; a role's technologies are fetched once and saved (`RoleTechnologies`). See HANDOFF section 23.
