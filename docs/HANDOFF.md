# Interview Coach: project handoff

Written 2 October 2026 at the end of a long build session, so that a new session (or a new person) can continue without the
original conversation. If you are an AI assistant reading this: read sections 1 to 4 and 9 first, then `SPEC.md` section 14, then
the code you are about to change. Do not trust this file over the code; the code and the tests are the truth, this is the map.

Contents: 1 Status, 2 Run/build/test, 3 Architecture, 4 LLM layer and prompts, 5 Data, 6 Behaviour reference, 7 Decisions,
8 Measured facts, 9 Traps and lessons, 10 Tests, 11 Open items, 12 Continuing with Claude, 13 Documentation still to write.

---

## 1. Where things stand

A Windows desktop app (WPF, .NET 10) for practising job interviews. The user supplies a job description, a resume, a role and a
seniority; an LLM writes questions and coaches the answers. The original specification is `SPEC.md`. **Section 14 of SPEC.md lists
every change made after the spec was written and overrides the spec where they differ.**

| Milestone (spec section 12) | State |
|---|---|
| 1 Skeleton: solution, DI host, settings with DPAPI keys, demo mode, prompts, LLM service, Test connection | Done |
| 2 Profiles: CRUD, JD and resume paste or file load (PDF, DOCX, TXT, MD), SQLite and migrations | Done |
| 3 Learn mode (text): question generator, coach, coach cards | Done |
| 4 Practice mode (typed): composer, submit, coach with the answer, retry with comparison, follow-ups | Done (attempts are not saved yet) |
| 5 Voice: speech to text, text to speech (Azure, OpenAI, Windows), mic in composer, barge-in, timer, auto-listen | **Not started** (fakes and settings fields exist) |
| 6 Mock Interview: planner, interviewer loop, thread building, parallel coaching, debrief, Markdown export | **Not started** (prompts `planner.md`, `interviewer.md`, `debrief.md` exist, unused) |
| 7 History and polish | **Not started** |

Built beyond the spec (all in SPEC.md section 14): scenario question type; answer-length control and word-count readout;
JSON extraction that tolerates stray braces; thinking-effort setting; prompt caching with a reordered coach prompt;
background preparation of the next question; the technology bank (saved technical questions and general answers per technology
and seniority); By technology with an Other box; interviewer-style question length rules; full-time or contract role type with two
extra question types; scroll-wheel behaviour on Home; app icon; a full visual redesign.

Test status: **670 tests passing** (Core 293, Infrastructure 159, App 218). The last full verification was done with
`-c Release` because the user had the Debug build running (see section 2).

**Version control.** The folder is a git repository (branch `main`). History is written to be read; see "Commit conventions" in `README.md`.

Packages in use (versions at the time): Anthropic 12.53.0, Microsoft.Extensions.AI 10.10.0, Microsoft.Extensions.AI.OpenAI 10.10.1,
EF Core + Sqlite 10.0.12, PdfPig 0.1.16 (package id `PdfPig`, namespace `UglyToad.PdfPig`), DocumentFormat.OpenXml 3.5.1,
CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.Hosting 10.0.12, System.Security.Cryptography.ProtectedData 10.0.12, xUnit.
Default model for every role: `claude-sonnet-5-5`.

---

## 2. Run, build, test

```
dotnet run --project src/InterviewCoach.App          # run the app (Debug)
dotnet test                                          # all tests
dotnet test tests/InterviewCoach.Core.Tests          # one project
dotnet build InterviewCoach.sln -c Release           # verify while the Debug exe is running (see below)
```

- **A running app locks its DLLs.** If `dotnet build` fails with MSB3021/MSB3027 "file is locked by InterviewCoach.App", the user
  has the app open. Do not kill their process. Use `-c Release` for builds and tests; it writes to different folders.
- **EF migrations.** `dotnet ef` cannot load the Windows-targeted Infrastructure project alone. From `src/InterviewCoach.Infrastructure`:
  `dotnet ef migrations add <Name> --startup-project ../InterviewCoach.App --output-dir Persistence/Migrations`.
  Migrations are applied automatically at startup (`Database.Migrate`).
- **Icon.** `dotnet run --project tools/IconGenerator src/InterviewCoach.App/Assets` redraws `app.ico`, `logo-64.png`, `logo-256.png`.
- **Screenshots of every screen, light and dark.** Set `ICOACH_SNAPSHOTS=<folder>` and run
  `dotnet test tests/InterviewCoach.App.Tests --filter VisualSnapshots`. Unset, that test does nothing.
- **Where the app keeps things:** `%LOCALAPPDATA%\InterviewCoach\` holds `settings.json` (API keys encrypted with DPAPI, current user),
  `app.db` (SQLite, WAL mode so `app.db-wal` and `-shm` appear), and `logs\llm-yyyymmdd.log` (only when Debug logging is on).
- **Key fallbacks** when a key field is empty: `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `OPENROUTER_API_KEY`, `AZURE_SPEECH_KEY`, `AZURE_SPEECH_REGION`.
- **Demo mode** (Settings) uses a fake LLM, fake speech and an in-memory technology bank, so the UI runs with no keys and nothing
  made up reaches the real database.
- **Debug logging** (Settings) writes full prompts and replies (resume and JD included) plus timing, token counts, cache reads and
  writes, and a REPAIR RETRY marker per call. It is the main tool for diagnosing slowness or cost. Turn it off afterwards.

---

## 3. Architecture

```
InterviewCoach.sln
  src/InterviewCoach.Core            net10.0                     domain, engines, interfaces, prompt rendering. No UI, no SDKs.
  src/InterviewCoach.Infrastructure  net10.0-windows10.0.19041   LLM, settings, SQLite, documents, fakes. References Core.
  src/InterviewCoach.App             net10.0-windows10.0.19041   WPF, MVVM, Generic Host DI. References both.
  tests/*.Tests                      Core, Infrastructure, App (App.Tests references the WPF project and runs real views)
  tools/IconGenerator                not in the solution
```

**Patterns.** MVVM with CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`). The host is built in `App.xaml.cs`
(`ServiceRegistration.ConfigureInterviewCoach`). Demo mode is implemented by decorators resolved in place of the real services:
`RoutingLlmService` (real `LlmService` or `FakeLlmService`) and `RoutingTechBankRepository` (SQLite or in-memory), switched by
`AppSettings.DemoMode`. Pages are swapped by `MainViewModel.CurrentPage` and the `DataTemplate`s in `App.xaml`; a **new view is created
every time a page is shown**, so view state must live in view models.

**Key types.**

| Project | Type | Role |
|---|---|---|
| Core | `LearnEngine` | The Learn state machine: choose a question, get the coach output, follow-ups, back, retry, tailor, prefetch |
| Core | `TechBank` | Technologies from a JD, saved technical questions per technology and seniority, general answers; talks to `ILlmService` and `ITechBankRepository` |
| Core | `PromptRenderer`, `PromptVars`, `PromptName` | Fills `{{VARS}}` in a single pass, throws on a missing key, renders blank as `(none)` |
| Core | `QuestionTypes`, `EmploymentTypes`, `AnswerLength`, `CoachText`, `TextTools` | Type ids and labels, word-range tables, placeholders and shape parsing, fingerprints and question normalisation |
| Core | `AppSettings`, `CandidateProfile`, DTOs (`QuestionDto`, `CoachOutput`, `FollowUp`, `TechTagsDto`) | Data shapes; LLM JSON is snake_case |
| Infrastructure | `LlmService`, `ChatClientFactory`, `JsonResponseParser` | `IChatClient` wrapper, provider selection, JSON handling, logging |
| Infrastructure | `SettingsStore` | `settings.json` with DPAPI-encrypted key fields |
| Infrastructure | `AppDbContext`, `ProfileRepository`, `TechBankRepository`, migrations | SQLite persistence |
| Infrastructure | `ResumeTextExtractor` | PDF (PdfPig, line and paragraph heuristics by font size), DOCX (OpenXml, bullets and tables), TXT, MD |
| Infrastructure | `PromptLibrary` | Loads prompt files from disk next to the exe, falls back to embedded copies |
| App | `HomeViewModel` | Profiles, editor, Start card: mode, role type, question types, By technology and Other, answer length |
| App | `LearnViewModel`, `CoachOutputViewModel` | Learn screen state; display model of one coach reply |
| App | `SettingsViewModel`, `MainViewModel` | Settings form and test connection; navigation and demo banner |
| App | Controls: `HighlightedTextBlock`, `PasswordBoxBinder`, `WheelScrolling` | Placeholder highlighting, bindable password box, page-first mouse wheel over multi-line text boxes |

**A Learn session, end to end.** Home Start builds a `LearnSessionRequest` (saved profile, ticked types, answer words, technologies,
employment type) and raises `LearnRequested`; `MainViewModel` calls `LearnViewModel.Begin`, which creates a `LearnEngine` and calls
`StartAsync`. `NextAsync` produces a question in one of two ways (see section 6), shows it, then fetches the model answer from the
coach (a general answer from the bank, or a tailored one from the resume). Each operation takes an operation id and a cancellation
token; a result that arrives after the user moved on is discarded (`IsStale`). When a main question is ready the engine starts
preparing the next one in the background (`PrefetchNext`, on in the app, off by default in tests). Engine events fire on the caller's
context because the Core never uses `ConfigureAwait(false)`; this is deliberate so the UI thread receives them.

---

## 4. LLM layer and prompts

**Roles and models.** `LlmRole`: Planner, Interviewer, QuestionGenerator, Coach, Debrief; each has its own model setting.
The tag extraction call uses the QuestionGenerator role. Timeouts: Interviewer 30 s, Coach and Debrief 90 s, the rest 60 s.
Max output tokens 4096.

**`LlmService.GetJsonAsync<T>`** builds the system message, sends it with the single user message from `PromptNames.UserMessage`,
extracts JSON, and on a parse failure makes **one** repair call (appends the bad reply and the spec's "That wasn't valid JSON..." turn).
`JsonResponseParser.ExtractJson` strips code fences and takes the **first balanced object or array** (strings and escapes respected),
not "first brace to last brace": a stray closing brace or commentary after the JSON must not cost a repair call.

**Thinking effort.** Settings option (Model default, Low, Medium, High) sent as `ChatOptions.Reasoning.Effort` for Anthropic models
whose id does not contain "haiku". Haiku predates adaptive thinking and would reject it. Newer Sonnet models think by default, which
dominated latency before this setting.

**Prompt caching.** `LlmService.BuildSystemMessage` splits the system prompt into text blocks and puts
`.WithCacheControl(Ttl.Ttl5m)` on the first one or two (Anthropic provider only, switchable in Settings):

1. at the `=== THE CANDIDATE AND THE QUESTION ===` header (end of the coach's fixed guidance, identical for every call), and
2. just after `</candidate_resume>` (job description and resume, identical within a profile).

The adapter records the marker as `AdditionalProperties["anthropic:cache_control"]`; `GetCacheControl()` is documented but not
accessible, so tests check that key. Anthropic ignores a marker on a block below its minimum size, so this is always safe to send.
Cache reads and writes show in the debug log.

**Prompt catalogue** (files in `src/InterviewCoach.App/Prompts/`, copied next to the exe and also embedded in Infrastructure):

| File | Used by | Notes |
|---|---|---|
| `question_generator.md` | Learn, and the bank when writing a question | Per-type length rules, employment-type rule, focus technology |
| `coach.md` | Learn (and later Practice and Mock) | Fixed guidance first, request details last; length rule; employment section |
| `tech_tags.md` | `TechBank.GetTechnologiesAsync` | Up to 8 technologies from a JD |
| `planner.md`, `interviewer.md`, `debrief.md` | Mock Interview (milestone 6) | Present and unused; need `{{EMPLOYMENT_TYPE}}` when built |

**Variables.** Always: `JOB_ROLE`, `SENIORITY`, `JOB_DESCRIPTION`, `RESUME`. Generator: `QUESTION_TYPES`, `ALREADY_ASKED`,
`FOCUS_TECHNOLOGY`, `EMPLOYMENT_TYPE`. Coach: `MODE`, `QUESTION`, `QUESTION_TYPE`, `EMPLOYMENT_TYPE`, `TRANSCRIPT`, `ANSWER_LENGTH`,
`CANDIDATE_ANSWER`, `PREVIOUS_ATTEMPT`, `INPUT_METHOD`, `DURATION_SECONDS`, `WORD_COUNT`. A blank or null value renders as `(none)`;
a key missing from the dictionary throws.

**Rules that keep caching and behaviour intact (guarded by tests in `PromptLibraryTests`):**

- In `coach.md` everything before `=== THE CANDIDATE AND THE QUESTION ===` must contain **no variables** and the header text must
  appear **exactly once** (the split uses the first occurrence; the intro sentence deliberately does not quote it).
- In `coach.md` and `question_generator.md`, anything that changes per call or per session comes **after** `</candidate_resume>`.
- Editing the fixed text of `coach.md` invalidates the cache once; that is only a one-off cost.
- The wording of the prompts was changed from the spec on purpose in several places; section 14 of SPEC.md explains each change.

**Answer length.** `{{ANSWER_LENGTH}}` is the requested word count ("about 120 words (roughly 55 sec spoken)"); if none was requested it
is the range for the question type taken from `AnswerLength.Expectation` ("between 60 and 150 words (...)"), the same table the
screen uses for "Interviewers typically expect..."; `(none)` only for an unknown type. This was added after the model wrote 250 to
300 words for one-line concept questions when told nothing.

---

## 5. Data

**SQLite (`app.db`).** Tables and keys:

- `Profiles`: Id, Name, JobRole, Seniority (stored as text), JobDescription, ResumeText, CreatedAt, UpdatedAt (UTC). Sorted by
  UpdatedAt descending in memory (SQLite cannot order a DateTimeOffset).
- `TechQuestions`: TechnologyKey (lower-case), Technology (display), Seniority, QuestionKey (normalised text), Question, Focus, CreatedAt;
  unique on (TechnologyKey, Seniority, QuestionKey).
- `TechAnswers`: TechQuestionId (cascade delete), AnswerWords, CoachJson, CreatedAt; unique on (TechQuestionId, AnswerWords).
  **AnswerWords = -1 means "interviewer norm"** (`TechBank.InterviewerNormKey`). It was 0 before the coach was told the length range;
  rows saved under 0 are left in place but never matched, so fresh right-sized answers replace them automatically.
- `JdTechnologies`: Fingerprint (SHA-256 of the whitespace-normalised JD), TechnologiesJson, CreatedAt.
- Migrations: `InitialCreate`, `AddTechBank`.

Not stored yet (spec section 7 entities for later milestones): Session, Turn, QuestionThread, PracticeItem, Attempt. Learn questions and
answers **are** kept since the Library was added (`LearnHistory`, section 16); mock interviews and practice attempts are not built yet.

**`settings.json`** (enums written as names): Provider, AnthropicApiKey, OpenAiApiKey, OpenAiBaseUrl, OpenRouterApiKey, the five model ids, the five OpenRouter model ids (`OpenRouter*Model`), ThinkingEffort,
PromptCaching, **EmploymentType**, ReuseGeneralAnswers, SpeechToText, TextToSpeech, AzureSpeechKey, AzureSpeechRegion, Voice,
SpeakingRate, MicrophoneDeviceId, AutoListen, SilenceAutoSubmit, SilenceSeconds, ShowQuestionTextDefault, DemoMode, DebugLogging.
Secret fields (`AnthropicApiKey`, `OpenAiApiKey`, `OpenRouterApiKey`, `AzureSpeechKey`) are stored as `dpapi:<base64>`; the computed `Effective*` properties
are `[JsonIgnore]` (an earlier version leaked plaintext keys through them; a test guards this). A corrupt file falls back to defaults.

---

## 6. Behaviour reference (as built)

**Profiles.** One profile per job you are preparing for (name, role, seniority, JD, resume). Switching profile or clicking New with
unsaved edits asks first. Start is disabled until the profile is saved and has a role, a JD and a resume, because a session uses the
saved copy. Text over 40,000 characters shows a warning and a Trim button. Loading a file over existing text asks first.
Extraction failures (scan with no text, `.doc`, corrupt file, over 20 MB) produce friendly messages.

**Where each Learn question comes from.** There are two sources and a share rule.

- The **technology source**: a saved general technical-concept question for a technology and seniority (or one the model writes when all
  saved ones for that pair were already seen this session), with a general answer written without the resume or JD
  (`TechBank.WriteGeneralAnswerAsync`, role "software engineer"). Reuse costs no tokens and survives resume and JD edits.
- The **model source**: the model writes the question from the resume and JD.
- With types ticked, the technology source counts as one share and each other ticked type as one share (1 in N). Technical concept and
  By technology together are still one share. "Any" means the model's types plus one technology share. While the technology source is
  serving, the model is not offered `technical_concept`.
- By technology alone is 100% technology source; if it cannot produce a question the error is shown with Retry instead of falling back.
  With other types ticked it falls back to the model. With the saved bank switched off the model writes the technology question from the
  resume (generator `FOCUS_TECHNOLOGY` set).
- Technologies come from the **saved** profile's JD (one cheap call per distinct JD, remembered by fingerprint), or from the user typing
  names under **Other** (comma, semicolon or newline separated, max 8, 60 characters each). The same technology is not asked twice in a row.

**Answers.** General answers (from the bank) show a note and a **Tailor to my resume** button (a normal resume-based call, not saved).
Follow-ups of a general answer are general too and are not saved. Back returns to the main question. Learn mode drops any feedback or
delivery text the model invents (`CoachOutput.WithoutFeedback`).

**Role type (full-time or contract).** Chosen in the Start card, remembered in settings. Sent as `{{EMPLOYMENT_TYPE}}` to the generator
and the coach; the bank always uses `(none)`. Adds the type `motivation_fit` (full-time) or `engagement` (contract) to the offered
types; "Any" in the prompt means any type that fits the employment type.

**Question phrasing.** `question_generator.md` gives per-type length rules: a technical-concept question is one direct sentence of about
6 to 18 words ("What's the difference between checked and unchecked exceptions?"), one ask only. Questions saved in the bank before this
rule keep their old wording until the user clears the bank in Settings.

**Screens.** Home (profiles list and editor cards, Start a session card), Learn, Settings (cards: Language model, Speed and cost, Saved
technical questions, Speech, Behavior, Other). The sidebar highlights Home while a Learn session is open. Mock Interview and Practice
show as "Coming soon". The Learn mode card is bound to the view model (always selected for now).

**Design system** (`App.xaml`): colours come from the Fluent theme via `DynamicResource` (`CardBackgroundFillColorDefaultBrush`,
`TextFillColorSecondaryBrush`, `AccentFillColorDefaultBrush`, and so on) so light and dark follow Windows. Shared styles: `Card`,
`InsetCard`, `Chip`, `AccentChip`, `ActionBar`, `NavItem`, `ChipCheckBox`, `ChipRadio`, `ModeCard`, `ProfileItem`, `PageTitle`,
`CardHeading`, `CardTitle`, `FieldLabel`, `Hint`, `Glyph` (Segoe Fluent Icons), `BrandGradient` (indigo to violet, same as the icon).

---

## 7. Decisions and why

1. **Core has no SDK or UI references**, so a different UI or provider can replace WPF or Anthropic without touching engines.
2. **`IChatClient` from Microsoft.Extensions.AI** over the Anthropic SDK's adapter, with an OpenAI-compatible alternative and OpenRouter (which is OpenAI-compatible at a fixed address).
3. **Keys encrypted with DPAPI (current user)** in `settings.json`; environment variables as fallback.
4. **Prompts are files, not strings**, so they can be edited without a rebuild; embedded copies are the fallback.
5. **One repair retry** for bad JSON, with the first balanced JSON value extracted first (a measured Haiku failure shape).
6. **Thinking effort setting** because default thinking made Sonnet answers about 21 s; Low roughly halved it.
7. **Prompt caching with a reordered coach prompt**: fixed guidance first so it is cached for everyone; variable parts last.
8. **Prefetching the next question** because generation time is real and cannot be shortened; this hides it. It costs one extra pair of
   calls per session that is discarded if the session ends.
9. **Technology bank with general answers** because the coach call is about 90% of cost and a general answer needs no resume tokens and
   is reusable; the Tailor button preserves personalisation on demand.
10. **Bank keyed by technology and seniority, never by profile or resume**, so it survives the user changing their resume and JD often.
11. **JD fingerprint (whitespace-insensitive)** so editing the resume, or re-pasting the same JD, costs nothing.
12. **Range sent to the coach when no word count is requested**, from the same table the screen shows, so what the model aims for and
    what the screen claims cannot disagree.
13. **Employment type is a session choice remembered in settings**, not a profile field: it rarely changes between sessions, and a
    profile edit would force a Save before Start.
14. **Radio groups avoided** for the Learn card and Role type: group state is shared between view instances (see section 9).
15. **Demo mode via decorator services** so the real database and API are never touched.
16. **Snapshot test harness** so design can be reviewed by rendering the real views in both themes without clicking through the app.
17. **Theme colours via `DynamicResource`, never hard-coded**, for correct dark mode.
18. **User-visible wording avoids milestones and internals.**

---

## 8. Measured facts

From the debug log of the build session (prices are an assumption: Sonnet $3 in / $15 out and Haiku $1 in / $5 out per million tokens,
cache read about 0.1x, cache write about 1.25x; the user's balance drop was consistent with this but it is unconfirmed).

- Sonnet with default thinking: Coach 21.1 s, 2,444 output tokens for about 760 tokens of visible text. At thinking effort Low: 8.8 to
  11.0 s, 909 to 1,084 output tokens.
- Generation speed about 100 to 115 tokens/s for Sonnet and about 76 to 82 for Haiku. Haiku was **not** faster in practice: it wrote
  more (feedback it was told to omit, commentary after the JSON) and once returned invalid JSON, adding a repair call.
- A Learn answer takes about 9 s end to end for the coach call, 1 to 2 s for a question call. Prefetch hides this after the first question.
- Cache confirmed working: question call on Haiku read 4,245 of 5,144 input tokens from cache; tailored coach call on Sonnet read 6,492 of
  9,874; general answer read 3,486 of 3,679. The reordered coach prompt was not yet measured on a tailored call.
- Estimated cost per question: originally about $0.067 (Sonnet everything, no bank, no cache); saved technical question, cache warm, about
  $0.017; tailored question about $0.028 and perhaps $0.019 after the reorder; a reused saved question about $0. Output tokens are about
  85% of a general question's cost, so shorter answers and fewer follow-up hints are the remaining lever.
- Before the range fix, concept answers ran 250 to 300 words against an expected 60 to 150; with a requested "about 80 words" they ran
  92 to 124. The effect of the range fix on the live model was **not yet measured**.

---

## 9. Traps and lessons (things that actually went wrong)

- **`RadioButton` group state is shared across view instances.** A page's view is recreated on every navigation; the Learn mode card came
  back unselected. Bind selection to the view model instead (`IsLearnMode`, `EmploymentOption.IsSelected`). Regression tests exist.
- **A `Style` on a themed control must use `BasedOn`** or it loses the Fluent template (dark mode showed white text boxes). **Custom
  control templates must set `Foreground`** or text falls back to a default that is wrong in dark mode.
- **`pack://application:,,,/Assets/x` fails in the test host.** Use `pack://application:,,,/InterviewCoach.App;component/Assets/x`.
- **Cache split header uniqueness.** An earlier intro sentence quoted the header and would have moved the split into the intro. Guarded.
- **WPF reports `VerticalOffset` only after a layout pass**, and `ItemsControl` items materialise after the first pass. Tests that look at
  the visual tree must flush the dispatcher (`DispatcherFrame` at `ContextIdle`) and re-measure.
- **A `ListBox` inside a `ScrollViewer` swallows the mouse wheel**; avoid it for small choices. Multi-line text boxes are handled by `WheelScrolling`.
- **`Assert.DoesNotContain` on prompt text can fail on the prompt's own examples** (it quotes "between 60 and 150 words"); assert on the
  specific context line instead.
- **Static text in coach prompt rules must not name variables.** The `QUESTION_TYPE`/`ANSWER_LENGTH` context lines are the only per-call text.
- **`[ObservableProperty]` fields must not be used directly** (analyzer MVVMTK0034); assign through the property or the generated hooks.
- **XML comments cannot contain `--`** (it broke a csproj comment mentioning `--project`).
- **A missing `DynamicResource` key fails silently.** Probe keys with `Application.Current.TryFindResource` in a test before relying on them.
- **Saved bank entries are not versioned.** After changing question wording the old saved questions persist; the user clears them in
  Settings. (Answers were handled by changing the norm key to -1.)
- **No `ConfigureAwait(false)` in Core** is intentional (events must reach the UI thread).
- **Tooling note for AI assistants on this machine:** in the Bash tool, heredocs containing apostrophes fail to parse; write files with the
  file-writing tool, or write a script file and run it. Backslashes in inline Python strings get halved; use forward slashes in paths
  inside csproj edits.

---

## 10. Tests

670 tests: Core 293, Infrastructure 159, App 218.

- **Core.Tests**: prompt rendering, engine behaviour (`LearnEngineTests`, `LearnEngineBankTests`, `LearnEngineTechnologyTests`,
  `EmploymentTypeTests`), bank service, answer length, question types, text helpers. Helpers in `TestDoubles.cs`:
  `ScriptedLlmService` (a model whose replies the test controls, can be made to wait or fail), `BankScript` (tells tag extraction, bank
  question, model question and general or tailored answers apart by the prompt). `PromptLibrary` with a missing directory forces the
  embedded prompts, so tests also check the real prompt text.
- **Infrastructure.Tests**: JSON parsing, LLM service (retry, cache split, options), settings store (DPAPI round trip, no plaintext),
  SQLite repositories on real temp files (restart-style: new factory per call), file extractors on generated PDF and DOCX files, prompt
  content and cacheability guards, demo mode isolation.
- **App.Tests**: view model logic and **real views** on a shared STA dispatcher (`WpfHost`): XAML loads, bindings produce no errors
  (a trace listener collects them), wheel behaviour, regression tests above, and the `VisualSnapshots` development aid.
- Conventions: test names are sentences describing behaviour; fakes live in Infrastructure/Fakes when the app also uses them.

---

## 11. Open items and next steps

1. ~~Milestone 4, Practice (typed)~~: done, see section 19. Still open from it: Practice attempts are not saved (no Session, PracticeItem or Attempt
   tables), so the Library does not show them.
2. **Milestone 5, Voice**: `ISpeechToText` and `ITextToSpeech` implementations (Azure first, then OpenAI, then Windows offline TTS),
   composer mic with F2, barge-in, auto-listen, silence auto-submit, voice settings with Test mic and Test voice, "Read answer aloud".
   Interfaces, fakes and settings fields already exist.
3. **Milestone 6, Mock Interview**: planner, interviewer loop with time pacing and end conditions, thread building, parallel coaching (max 3),
   debrief, Markdown export. Add `{{EMPLOYMENT_TYPE}}` to `planner.md`, `interviewer.md` and `debrief.md` and make the round type aware of it.
4. **Milestone 7, History and polish**: the Library of Learn questions is done (section 16); still to do: history of mock interviews, resume unfinished mocks, error banners, shortcuts.
5. Known limits and ideas not done: technology questions are technical-concept style only; the coach is not cached when set to Haiku
   (fixed part is below Haiku's minimum cache size); no structured-output (JSON schema) mode is used; no streaming of the answer as it is
   written (would make the first question feel instant, needs partial-JSON parsing); no installer or packaging; no localisation; no
   telemetry (by design); the live effect of the answer-length range fix and the reordered coach prompt should be checked in the debug log.

---

## 12. Continuing with Claude

Open the repository folder in Claude Code; `CLAUDE.md` is loaded automatically. Useful first message in a new session:

> Read docs/HANDOFF.md and SPEC.md section 14, then run `dotnet test -c Release` and tell me the result. I want to continue with
> milestone 4 (Practice mode, typed). Follow the conventions in CLAUDE.md. Before changing a prompt, read the rules in section 4 of the handoff.

If a new chat cannot see the files, attach `docs/HANDOFF.md`, `SPEC.md` and `CLAUDE.md`. They are written to be sufficient on their own for
orientation; the source then gives the detail.

About the person you are working with (inferred from the session): cost-conscious (a small prepaid API balance, reads the debug log),
checks claims against evidence, wants things verified rather than asserted, prefers short direct explanations with numbers, likes a
professional finished look, and is preparing for real interviews (a .NET and React background, both full-time and contract roles).

---

## 13. Documentation still to write

Planned for later; this handoff is the raw material. Suggested set, in `docs/`:

| Document | Contents | Draw from |
|---|---|---|
| High-level design | Context, goals and non-goals, component diagram, data flow, deployment view, quality attributes, risks | Sections 1, 3, 7 |
| Low-level design: Core | Engine state machines (Learn phases, operation ids, prefetch, routing shares), class responsibilities, sequence diagrams | `LearnEngine`, `TechBank`, section 6 |
| Low-level design: Infrastructure | LLM service pipeline (caching split, options, JSON handling, retry, logging), persistence, settings, extractors | Section 4, 5 |
| Low-level design: UI | MVVM structure, navigation, view model contracts, design system, theming rules, controls | `App.xaml`, section 6 |
| Data model and migrations | ER diagram, table definitions, keys, versioning rules for the bank | Section 5 |
| Prompt catalogue | Each prompt: purpose, variables, output schema, stability rules, change history, evaluation notes | Section 4, SPEC section 8 and 14 |
| LLM integration and cost model | Providers, models per role, thinking effort, caching, token and cost measurements, how to read the debug log | Sections 4, 8 |
| Security and privacy | What is sent where, key handling (DPAPI), logs, local data, threats and mitigations | Sections 2, 5 |
| Test strategy and report | Layers, doubles, regression catalogue, how to add tests, coverage gaps | Section 10 |
| Build, run and operations guide | Setup, configuration, troubleshooting (locked exe, migrations, keys), upgrading models | Sections 2, 9 |
| Architecture decision records | One short ADR per entry in section 7 | Section 7 |
| User guide | Setup, profiles, Learn mode, options, tips, FAQ | Section 6 |

When these are written, keep them in step with `SPEC.md` section 14 and update this handoff's status table.

---

## 14. OpenRouter provider

Added after the first push. `LlmProvider.OpenRouter` uses the OpenAI client pointed at `https://openrouter.ai/api/v1` with the user's
OpenRouter key; the model id (`maker/model`, for example `anthropic/claude-sonnet-5.5`) picks the model.

- **Separate model set.** OpenRouter ids differ from Anthropic's, so `AppSettings` keeps five more model fields (`OpenRouter*Model`,
  default `anthropic/claude-sonnet-5.5`). `ModelFor(provider, role)` and `SetModel(provider, role, id)` read and write either set;
  `ModelFor(role)` follows the current provider. Anthropic and OpenAI-compatible still share the original set. In `SettingsViewModel`
  the five boxes show the set of the provider on screen; `OnProviderChanged` stores the visible values under the old provider and loads
  the new one, and `Load`/`ToSettings` use a private `_models` copy of both sets. `_loading` stops that swap while loading.
- **Model browser.** `IOpenRouterCatalog` (Core) and `OpenRouterCatalog` (Infrastructure) download the public `GET /models` list (no key,
  nothing about the user). `Parse` keeps models that take text and answer only in text and drops `:batch` copies; prices are converted from
  dollars per token to dollars per million; a negative price ("varies") or a missing one is shown as "Price not listed" and sorts last.
  The catalog is kept for the run, a failure is not cached, and errors become `LlmException` with a plain message. The list loads only when
  the user clicks **Load models**, so opening Settings never touches the network. The screen has search (every word must match the name or the
  id), **Cheapest first**, and three buttons that copy the selected id into the Question generator, the Coach or every role. Nothing is saved
  until **Save**.
- **Thinking effort goes through OpenRouter's own field.** OpenRouter's documentation says it takes a `reasoning` object
  (`"reasoning": {"effort": "low"}`) and not OpenAI's `reasoning_effort`. The OpenAI client has no setting for that, so
  `OpenRouterReasoningPolicy` (a per-call pipeline policy) adds the field to each chat request body when the user chose Low, Medium or
  High; with "Model default" nothing is added. `ChatClientFactory` includes the effort in its cache key so a change takes effect at once, and
  takes an optional endpoint so tests can point it at a local stand-in server that records the request (path, bearer key, body).
  `ChatOptions.Reasoning` is deliberately not set for OpenRouter (that would send the OpenAI-style field). **Verified:** what is sent.
  **Not verified live:** that OpenRouter accepts it for every model; models that support only some efforts (the catalogue lists
  `supported_efforts` per model, for example DeepSeek V4 lists only high and xhigh) may reject Low, which Test connection would show.
  Reasoning tokens count as output tokens and, on most providers, against `max_tokens` (the app caps replies at 4096).
- **Cache markers are still Anthropic-only** (`BuildSystemMessage`), because the OpenAI adapter cannot carry `cache_control`. OpenRouter
  caches automatically for some providers.
- **Measured:** the live catalogue on 2 October 2026 had 465 entries; the parser keeps 377 (the rest are batch copies and image or audio
  generators), 20 of them free, 4 with no price. Examples, per million tokens in/out: `anthropic/claude-sonnet-5.5` $2.00/$10.00,
  `anthropic/claude-haiku-4.5` $1.00/$5.00, `google/gemini-3.5-flash-lite` $0.30/$2.50, `deepseek/deepseek-v4-flash` $0.028/$0.056.
  **Not yet measured:** real calls through OpenRouter (speed, whether each model returns valid JSON, actual spend). The user should try the
  question generator on a cheap model first and watch the debug log.
- **Tests:** `OpenRouterSettingsTests` (Core), `OpenRouterTests` (Infrastructure: parsing, HTTP stub, factory, key encryption, no Anthropic-only
  features), `OpenRouterSettingsUiTests` (App: provider swap, search, sort, use buttons, real view without binding errors). `FakeCatalog` is in
  `Fakes.cs`; `VisualSnapshots` also writes `5-settings-openrouter-*.png`.

### Recommended setups (added with the OpenRouter work)

`ModelRecommendations.For(provider)` (Core) returns `RecommendedSetup`s: a model for each of the five roles, the thinking effort (Low for all),
a summary, and whether it was `Tested` with this app. Anthropic has one; OpenRouter has two; OpenAI-compatible has none (its model names cannot
be known). `SettingsViewModel.SetupCards` turns them into cards with a **Use this setup** button; applying fills the five boxes and sets
Thinking effort, and nothing is saved until Save. For OpenRouter each row shows the live price from the loaded catalogue, or says the model is
not in OpenRouter's current list (and the status line names any such models when the setup is applied), so a retired model id cannot go unnoticed.

How the choices were made, so they can be revisited:

- Question generator = the small model (`claude-haiku-4-5-20251001`, OpenRouter `anthropic/claude-haiku-4.5`); Coach, Planner and Debrief =
  the strong one (`claude-sonnet-5-5`, `anthropic/claude-sonnet-5.5`); Interviewer = the fast one. These are the models the prompts were written
  and measured against (see section 8). **Haiku was not faster than Sonnet in practice**; it is recommended for the question generator because it is
  cheaper and its replies there are short.
- Why Low effort is part of every setup: the live OpenRouter catalogue (2 October 2026) says `anthropic/claude-sonnet-5.5` has mandatory reasoning with
  default effort **high**, the setup that made this app's Sonnet coach call take about 21 s instead of about 10 s at Low.
- The **Lower cost** OpenRouter setup is Haiku for the question generator and interviewer and `openai/gpt-5-mini` for the rest. It was first
  suggested with `google/gemini-3.5-flash-lite` for questions, chosen from the catalogue only; the real log (section 15) showed that was a
  mistake and it was changed. GPT-5 Mini as coach was tried on 7 questions (see section 15). DeepSeek V4 models are far cheaper but list only
  high and xhigh efforts and think by default, so they were left out of the suggestions.
- Ids drift. If an id leaves OpenRouter's list the card says so after Load models. Update `ModelRecommendations` when models are retired, and
  keep `OpenRouterSuggestions` in `SettingsViewModel` in step.

---

## 15. Findings from the first OpenRouter run (debug log, 2 October 2026)

The user ran Learn mode on OpenRouter with the "Lower cost" setup as first suggested (Gemini 3.5 Flash Lite for questions, GPT-5 Mini for the
coach), By technology = C#, Senior, 70 logged calls in all. What it showed:

| Role and model | Calls | Avg seconds | Avg output tokens | Notes |
|---|---|---|---|---|
| Question generator, Haiku (Anthropic direct) | 16 | 1.6 (1.0 to 1.5 for bank-style questions) | 96 (66 to 74 for bank-style) | no thinking |
| Question generator, Gemini 3.5 Flash Lite (OpenRouter, effort Low) | 7 | 2.6 (2.3 to 3.0) | 556, of which 466 to 557 were thinking | for a question of about 25 tokens |
| Coach, Sonnet 5.5 (Anthropic direct, effort Low, general answers) | 5 | about 7.1 (5.7 to 8.2) | 635 to 758 | 8.3 s average over all 21 coach calls incl. default effort |
| Coach, GPT-5 Mini (OpenRouter, effort Low) | 7 | 6.5 (5.4 to 8.5) | 724, of which 128 to 320 thinking | cache read 2,944 of 3,077 input tokens |

Estimated cost per question (generator plus coach, warm cache, OpenRouter list prices: Haiku $1/$5, Sonnet 5.5 $2/$10, Gemini Flash Lite
$0.30/$2.50, GPT-5 Mini $0.25/$2.00 per million in/out, cache reads at the listed cache-read price): Haiku + Sonnet at Low about $0.0095;
Gemini + GPT-5 Mini about $0.0034; **Haiku + GPT-5 Mini about $0.0029**. The saving is almost all in the coach (about $0.008 to $0.0016).
Note Sonnet 5.5 is listed at $2/$10 on OpenRouter, lower than the $3/$15 assumed in section 8; the direct Anthropic price was not checked.

Conclusions and what was changed:

1. **Every coach answer began with a warm-up** ("Sure. Short version:", "Yeah, so", "Short answer:"): 30 of 32 logged answers, from both Sonnet and
   GPT-5 Mini. Cause: `coach.md` told the model to "open the way people actually open: Yeah, so... Sure. Short version is..." and its own example
   began "Yeah, so". Fixed in the prompt, and `CoachText.WithoutOpeningFiller` is a safety net that also cleans answers already saved in the bank
   (they were written with the old prompt). Lesson recorded in CLAUDE.md: never put an example opening in a prompt.
2. **Gemini 3.5 Flash Lite as question writer was a bad choice**: 90% of its output was thinking, it took about twice as long as Haiku and cost
   more per question. Cause: a thinking effort sent to an OpenRouter model that thinks only when asked starts thinking (its own default is
   "minimal"). Fixed two ways: the Lower cost setup now uses Haiku for questions, and `LlmService.ForRole` sends no effort for the question
   generator and interviewer on OpenRouter. (Not verified: whether Haiku on OpenRouter would also have started thinking under an effort.)
3. **GPT-5 Mini is a good coach at about a fifth of Sonnet's cost** and about as fast as Sonnet at Low: answers 102 to 117 words (target 60 to
   150), three follow-ups each, content accurate on the samples read (LOH compaction, static abstract members). It also wrote bracketed placeholders
   correctly. Quality over many topics is not yet judged; the user should read more.
4. **Duplicate lines in the already-asked list** (items 8 to 14 repeated 1 to 7): `TechBank` joined every saved question with this session's list and
   the two overlapped. Harmless but wasteful; now de-duplicated with `TextTools.SameQuestion`.
5. **Open: the questions drift into obscure trivia.** The 11 distinct C# questions of the day were, in order: ValueTask vs Task; interface vs abstract
   class; struct vs class; the async keyword; IAsyncEnumerable vs Task of IEnumerable; ref struct restrictions; covariance and contravariance;
   static abstract members; LOH fragmentation; Span vs Memory; ConditionalWeakTable. The first four are classic senior screens; later ones get
   rarer, and ConditionalWeakTable almost never comes up. Cause: the bank-question prompt must avoid every saved question for that technology and
   level plus everything asked this session, and "vary the topic" pushes the model off the common ground once the staples are used up. Also by
   design the bank ignores the job description and resume, so questions are general language-feature questions only and every one is the
   same style ("What is the difference between X and Y?"). Proposals, not yet done: (a) tell the generator to prefer questions that come up often in
   real screens and, when the common ones are used up, to move to another area of the technology (memory and GC, async, generics, collections,
   LINQ, DI, testing, exceptions) rather than an obscure API; (b) offer a seed list of common questions per technology so the bank starts with the
   staples; (c) let a share of By technology questions be scenario style ("What happens if you await inside a lock?") using the job description;
   (d) tick more than one technology, since the user's job also lists .NET, SQL Server and React.
6. **Speed as the user sees it.** First question: about 2.6 s generation plus 6.5 s coach, roughly 9 to 10 s. After that prefetch hides it if the
   user spends 10 s or more reading; clicking Next within seconds (as at 17:54) shows the wait. Saved bank questions with saved answers are instant.
   JSON handling was reliable: 1 repair retry in 70 calls (a Haiku reply, earlier in the day), and Gemini's fenced ```json replies parsed fine.

---

## 16. Second OpenRouter log, question batches and the Library

**What the second log window showed** (about 33 more calls, 2 October 2026, Haiku via OpenRouter for questions, GPT-5 Mini for the coach):
questions took 1.1 to 3.0 s (the first cold one 3.0 s), general coach answers 3.8 to 8.6 s, tailored (resume) answers about 6.7 k input tokens,
6.2 to 8.6 s and 190 to 220 words. One GPT-5 Mini reply was invalid JSON (1 in about 28 coach calls) and the repair retry fixed it for 4 s more.
The 6 technology questions all began "What's the difference between", and 5 of 6 questions of the other types (resume, design, engagement) were 24 to
48 words with a second ask, against a rule of one short question. The technical type has examples in its prompt and obeys; the others had none
(and the engagement example itself had two asks, which the model copied).

**Decisions taken on the proposals in section 15 item 5:**

1. *Common over obscure, and varied form*: done in two places. The single-question prompt now says so; and the bank asks for a **batch** (below).
2. *A seed of common questions per technology*: done as a model-written batch instead of a hand-made list, because the technologies come from job
   descriptions and cannot be listed in advance. When the saved questions for a technology and level have all been seen (including the first
   time), `TechBank.NextQuestionAsync` makes one `question_batch.md` call for 10 questions (`BatchSize`), saves them all, serves the first, and the
   rest come free. Batch prompt rules: most common first; at most two from one area; at most three "What's the difference between"; at least three
   situational; 6 to 18 words; one ask; nothing already saved or asked (the avoid list is de-duplicated). On failure or no new questions it falls
   back to the old single question. Cost: about 1.2 k input and 450 output tokens once per ten questions, less per question than ten single calls.
3. *Scenario style and the job description*: scenario-style questions ("What happens if...") are part of every batch. Job-description grounding is
   deliberately **not** added to bank questions, because the bank is keyed by technology and level so it survives resume and job-description edits;
   job-description scenarios are what the existing Scenario-based type is for.
4. *Examples for every type*: `question_generator.md` has examples and word limits for resume deep-dive, system design and engagement, and a
   stronger one-ask rule. Not yet measured live: whether Haiku now keeps these to one short sentence; check the next debug log.

**The Library.** Table `LearnHistory` (migration `AddLearnHistory`): QuestionKey (normalized), Question, QuestionType, Technology, Seniority, Source,
IsFollowUp, ParentQuestion and ParentKey, IsGeneral, ProfileName (empty for general answers), CoachJson, FirstSeenAt, LastSeenAt, TimesSeen. Unique on
(QuestionKey, ParentKey, IsGeneral, ProfileName), so the same question seen again updates its row, and a general and a tailored answer, or a follow-up
under two parents, are separate. The migration also copies bank questions that have a saved answer into the table (`INSERT OR IGNORE ... SELECT`,
preferring the answer saved with AnswerWords = -1, keeping the original dates); a test builds a database at the previous migration and checks it.

- Recording: `LearnEngine.RecordShownAsync` (best effort, exceptions swallowed so it can never interrupt practice) runs when an item and its answer are
  on screen, in three places: a prefetched question when it is shown (not while it is prepared), a saved bank question with a saved answer, and after
  `LoadAnswerAsync` (fresh questions, follow-ups, tailored answers, retries). `Back` does not record. `ILearnHistory` is the contract;
  `LearnHistoryRepository` (SQLite), `InMemoryLearnHistory` and `RoutingLearnHistory` (Demo mode) implement it.
- Screen: `LibraryViewModel` and `LibraryView`. Type chips are view-model bound (`LibraryTypeFilter`, single choice done in the view model, no radio
  groups); search matches question, technology, type label and answer text; sorts are newest (last seen), oldest (first seen), question, type; the
  detail pane reuses `CoachOutputViewModel`/`CoachOutputView` with the answer passed through `CoachOutput.ForLearning()` so old entries lose their
  warm-up. A follow-up button opens that follow-up's saved entry (clearing the filter if it was hidden) or says there is none. Stored times are UTC and
  shown local (today, yesterday, or a date). The list is a `ListBox` in a card that does not scroll, with the detail pane in its own `ScrollViewer`, so
  the mouse-wheel trap in section 9 does not apply.
- Navigation: the **Revisit** card on Home (a `ModeCard` with a command, enabled; the page is recreated on return so its checked look is harmless)
  and a **Library** item in the sidebar, both handled in `MainViewModel`, which reads the library fresh each time it is opened.
- Limits and ideas: only questions seen from now on are recorded (plus the carried-over bank questions); there is no export, no "practise this
  again" button and no tags; "Remove" does not remove the question from the bank; the 4-card mode row on Home wraps "Mock Interview" onto two lines.
- Screenshots: `VisualSnapshots` renders the library (`6-library-*`). Its capture was changed to paint the window at its own scale; the earlier
  brush stretched the bounds of every descendant (including scrolled-away content) into the picture and rescaled it.

---

## 17. Third log window: the batch, the filler fix, and long questions that stayed long

Checked on 2 October 2026 after the batch and Library work (20 new calls, Haiku via OpenRouter for questions, GPT-5 Mini for coaching).

- **Filler openers: fixed.** 0 of 11 raw coach answers began with a warm-up (30 of 32 before), and every answer was inside its requested word range
  (for example 109 words for a 60 to 150 concept answer, 179 to 219 for 150 to 280 resume answers). That is the model output before the code
  safety net, so the prompt change alone worked.
- **Batch: works.** One batch call (ASP.NET Core): 3.7 s, 762 input and 442 output tokens (about $0.003 at Haiku's listed prices), valid JSON first
  time, 10 questions of 11 to 16 words in 8 areas (dependency injection twice, within the limit), mixed forms (one "What's the difference between",
  "What happens if", "How would you track down", "When would you choose"), none repeated. Flaws: 2 of 10 had a second ask ("How does async/await
  actually work in C#, and why can't you use it in static constructors?", "What does [ApiController] do, and when would you omit it?"), one mixed up
  MapGet with attribute routing, and the async question overlaps one already saved under C# (the avoid list is per technology, so near-duplicates
  across technologies are possible).
- **Length rules ignored for the other types.** With the new prompt confirmed in the logged system text, 6 of 7 resume, design and engagement
  questions were still 24 to 33 words, 3 had a second ask, the design question began "Walk me through" at 25 words (limit 15), and the engagement
  question still asked two things. Stated limits and positive examples did not move Haiku. Change made: worked **Too long / Right** pairs (taken
  from these real outputs, with the technologies changed so a user's resume is not echoed), a hard limit of 20 words for resume deep-dives, a rule
  against joining a resume lead-in to the question with a dash, and one pair in the batch prompt. **Not yet verified live.** If it still fails the
  next step is code, not prose: measure the question (over 20 words, or "and" followed by a new request) and make one cheap retry asking for the
  short form, or accept it; both cost a call.
- Not addressed: near-duplicates across technologies in the bank.

---

## 18. Fourth log window: spreading resume questions and catching near-repeats

**What the log showed** (13 generated questions, all resume-based plus two contract-engagement ones): 0 filler openers in 14 coach answers (the
fix holds), 0 of 13 questions with a second ask (the "Too long / Right" pairs fixed that), but 0 of 11 resume questions at or under 20 words
(21 to 30, mean about 25) and a dash lead-in in all 13; 10 of 13 began "At CPF, you...". The user's current client is CPF, so many CPF questions
are natural, but the model had no reason to spread out: it asked RabbitMQ versus Service Bus twice in one session even though the first question
was in the avoid list (13 entries), and asked about JWT, middleware and PII isolation three times in seven minutes. Prompt prose to vary or to
shorten had now failed three times, so the choice moved into code.

**Resume focus** (`ResumeFocusPicker`, `TechBank.GetResumeTopicsAsync`, table `ResumeTopics`, migration `AddResumeTopics`, prompt `resume_topics.md`):
see SPEC section 14 for the rules. Design notes:

- Topics are read once per resume text with one cheap QuestionGenerator-role call and stored by `TextTools.Fingerprint(resume)`; a blank result is
  stored too so it is not asked again. Saved as JSON of `ResumeTopic(Employer, Project, Highlight)`. Clearing the saved technical questions keeps them.
- Selection is two-level so an employer with many highlights does not crowd out the others: least-used employer first, then least-used highlight
  within it. `Pick` changes nothing; `Commit` is called by `LearnEngine.GenerateQuestionAsync` only when the model returned a `resume_deep_dive`
  question. The picker lives for one session (made in `StartAsync`).
- It needs the saved bank (it stores the topics); with "Reuse saved general answers" off there is no focus and no extra call. The first resume question
  of a new resume costs one extra call (about 2 s, a few tenths of a cent); later sessions with the same resume cost nothing extra.
- The focus text is deliberately concrete, names the first word, and forbids "At <employer>, you" and a list of technologies. The word list is
  Why, How, What, When, Which, never the same twice in a row. **Not yet measured live:** whether Haiku now keeps to one short sentence starting with
  the given word, and whether the spread across employers shows in the next log.
- A tidy-up of the model's reading: employer trimmed (blank becomes "Other work"), at most 8 employers and 5 highlights each, blanks and repeats dropped.

**Near-duplicates** (`TextTools.IsNearDuplicate`): topic words are the normalised words minus a filler list; the same question when at least four
topic words are shared and they are at least half of the shorter question's topic words. Four, not three: three flagged "Redis concept question 1"
against "...2" in tests and would flag "How does garbage collection work in .NET?" against "...in Go?". The real RabbitMQ pair shares eight; the real
JWT/middleware pair (different questions on one topic) shares four of 15 and is not flagged. In `GenerateQuestionAsync` a repeat gets one retry as a
three-turn conversation (the first request, the model's own question as the assistant turn, then the "too close" note); a second repeat is accepted
rather than looping. The batch writer uses the same test against the bank and the session. Bank questions served from the saved bank still use the exact
check, so the same bank question is not blocked by a similar one from another technology. Limits: it compares words, not meaning, so a paraphrase with
different vocabulary passes; the topic-word list is English only.

---

## 19. Practice mode (typed), milestone 4

**What was built.** `PracticeEngine` (Core), `PracticeViewModel` and `PracticeView` (App), a Practice choice on Home (`SessionMode`, `IsPracticeMode`,
`SelectPracticeCommand`, `StartCommand` replacing the old `StartLearnCommand`, `PracticeRequested`), navigation in `MainViewModel`, and a "Try it myself" button
on the Learn screen (`LearnViewModel.TryItMyselfRequested`). Rules and flow are in SPEC section 14; this section is how it is put together.

- **Shared question source.** `QuestionPicker` was extracted from `LearnEngine` first (a pure refactor; all 610 tests passed unchanged). It owns the
  session's asked list, technologies, resume topics and focus rotation and returns the next question without any answer. `LearnEngine` adds the saved general
  answer to a saved technical question, the coach, follow-ups, tailoring and library recording; `PracticeEngine` adds the answering flow. A saved model
  answer carried by a picked question is dropped in Practice (`AsQuestion`).
- **The rule that matters.** Phase `Answering` never calls the Coach. `PracticeEngine.SubmitAsync` is the only entry (besides `RetryAsync` for a failed
  submit); it returns `SubmitResult.Empty` for blank text and `NotReady` when not in `Answering` (so a double click during `Coaching` sends nothing). The
  pending submission (answer, method, seconds, word count) is kept until feedback arrives, so a failure never loses the answer: `RetryAsync` re-sends it,
  `EditAnswer` reopens the box (`PendingAnswer` is the text to put back).
- **Follow-ups and retries.** `TryAgain` sets `_previousAttempt` to the latest answer and increments `AttemptNumber`; `AnswerFollowUp` appends the
  question and answer to `_thread` (transcript lines "Interviewer: ... / You: ...", spec section 9), clears `_previousAttempt`, resets the attempt count and
  adds the follow-up to the picker's asked list. Neither calls the model. `NextAsync` clears the thread. Stale replies are dropped by operation id as in Learn.
- **Coach output.** `CoachOutput.ForPractice()` keeps feedback and delivery and strips an opening warm-up from the model answer (Learn uses `ForLearning()`, which
  also drops feedback). The coach prompt already supported practice mode, so no prompt change was needed. `CoachOutputViewModel` gained `FollowUpPrompt` ("Click
  one to answer it." in Practice).
- **Screen.** Question card as in Learn; an answer card (`TextBox` with `AcceptsReturn`, `WheelScrolling.PassToPage`, a Ctrl+Enter key binding, live word count, a
  timer); after a submit a "Your answer" card with the words and time, then the Coach cards; an action bar with Back to Home, Try again (feedback only) and
  Next question. The timer lives in the view model (`Tick()` is called once a second by a `DispatcherTimer` in the view; the clock is injectable for tests);
  it starts at the first non-empty text, freezes at submit and resets on a new question or try. `TimerLevel` is amber from 150 s and red from 210 s for
  `behavioral` and `resume_deep_dive` only. The box is focused when it appears. Switching to the next question raises `QuestionChanged` twice (when the old
  one goes and when the new one arrives); harmless.
- **Home.** The mode cards are bound to `Mode`, not a radio group (see the trap in section 9); `IsChecked` is one-way with a command, and the three cards are one
  automatic group in a `UniformGrid`, which is why clicking one unchecks the others visually until the view model catches up. The Start card is shared by both
  modes. The existing card test now expects one disabled card (Mock Interview).
- **Tests.** 28 engine tests (`PracticeEngineTests`, with a rig that holds or fails the coach), 28 App tests (`PracticeTests`: box, counters, timer colours, empty
  and spaces-only submits, feedback, try again, follow-up transcript, failures, `BeginFrom`, real views without binding errors, Ctrl+Enter, Home mode switching, navigation
  and Try it myself). `StubLlm` lives in `Fakes.cs`; `VisualSnapshots` renders `7-practice-answering-*` and `8-practice-feedback-*`.
- **Not done, on purpose.** No persistence of attempts (spec section 7 has Session, PracticeItem, Attempt; they belong with History), so the Library does not
  list Practice. No voice (milestone 5): `INPUT_METHOD` is always `typed`, and `AnswerInputMethod` already has `Voice` and `Mixed`. No prefetch of the next
  question while the user is answering (a question takes about 1 to 2 s). **Not yet measured live:** how the real coach's feedback reads against real answers; send a
  debug log after a Practice session and check it quotes the answer and that Try again's first point is about what changed.

---

## 20. First Practice log (2 October 2026, SQL Server questions, four typed answers)

**How Practice did on real answers.** Four coach calls with GPT-5 Mini, 7.8 to 8.5 s each (about 6.7 k input tokens, 5.9 to 6.5 k of them cached, 990 to 1,160
output tokens including 256 to 384 thinking), about $0.0025 each at list prices. The wait after Submit is about 8 s, longer than Learn's 5 to 6 s because the reply
also carries feedback and a delivery comment. What the feedback did:

- It was specific and mostly grounded: of 10 quotes, 6 were verbatim from the answer and the other 4 were verbatim fragments joined with "..." (all from the
  answer). It caught a real technical error in a 155-word answer ("ROW_NUMBER assumes a random student": it is deterministic only with an ORDER BY) and gave
  concrete misses (covering indexes, deadlock graph via Extended Events, PARTITION BY syntax). It handled a five-word "I dont know the answer" well: honest, but
  push to attempt something. It ignored typos, as it should. Model answers were 106 to 119 words against a 60 to 150 target and no answer opened with a warm-up.
- **Flaw found and fixed: delivery read typing time as speaking time.** The timer's seconds were sent as `DURATION_SECONDS`; for typed answers that is typing time. The coach
  said a 5-second typed answer took "1 to 2 seconds" and treated 38 s for 27 typed words as a spoken length. Fix: typed answers send "(none)" (only voice and
  mixed send a duration), and `coach.md` now says typed answers get no pace or seconds comment and only a one-sentence length comment in words and spoken time (about
  130 words a minute) when clearly far too short or too long. The timer stays on screen for the user's own pacing. Not yet re-measured live.
- **Not exercised yet:** Try again (first feedback point about what changed) and follow-ups (transcript in the prompt): all four calls had `previous_attempt` "(none)".
  Send a log after using them.

**The SQL Server batch** (3.7 s, 10 questions of 8 to 16 words across ten areas, valid JSON). Flaws: one second ask ("When would you denormalize a schema, and what are the
trade-offs?", despite the example of that exact pattern in the prompt), one product mismatch ("What's the difference between a view and a materialized view?":
SQL Server has indexed views, not materialized views; the user got this question and answered "I don't know", which was not their fault), one odd question ("What
happens if you update a column used in a WHERE clause mid-transaction?"), and four of ten in the "difference between" family ("What's the difference between"
three times, "Explain the difference between" once; the limit is three "What's the difference between"). Changes: the batch prompt now says to ask only about things that
exist in the technology as named, and has a third Too long / Right pair for the denormalize question. A saved question that is wrong stays in the bank until the
user clears it in Settings.
