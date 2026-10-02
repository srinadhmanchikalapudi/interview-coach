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
| 4 Practice mode (typed): composer, submit, coach with the answer, retry with comparison, follow-ups | **Not started** |
| 5 Voice: speech to text, text to speech (Azure, OpenAI, Windows), mic in composer, barge-in, timer, auto-listen | **Not started** (fakes and settings fields exist) |
| 6 Mock Interview: planner, interviewer loop, thread building, parallel coaching, debrief, Markdown export | **Not started** (prompts `planner.md`, `interviewer.md`, `debrief.md` exist, unused) |
| 7 History and polish | **Not started** |

Built beyond the spec (all in SPEC.md section 14): scenario question type; answer-length control and word-count readout;
JSON extraction that tolerates stray braces; thinking-effort setting; prompt caching with a reordered coach prompt;
background preparation of the next question; the technology bank (saved technical questions and general answers per technology
and seniority); By technology with an Other box; interviewer-style question length rules; full-time or contract role type with two
extra question types; scroll-wheel behaviour on Home; app icon; a full visual redesign.

Test status: **441 tests passing** (Core 178, Infrastructure 115, App 148). The last full verification was done with
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

Not stored yet (spec section 7 entities for later milestones): Session, Turn, QuestionThread, PracticeItem, Attempt. **Learn sessions are
not saved**; the History screen (milestone 7) will need writes added to the engines.

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

441 tests: Core 178, Infrastructure 115, App 148.

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

1. **Milestone 4, Practice (typed)**: composer with timer and word count; submit; coach with the answer (`MODE = practice`, `INPUT_METHOD`,
   `DURATION_SECONDS`, `WORD_COUNT`); retry passes `PREVIOUS_ATTEMPT`; "Answer a follow-up". The engine must **never call the coach before
   submit** (spec section 6.3). Needs Session, PracticeItem and Attempt tables and a migration. The coach output view already hides
   "How your answer landed" when there is no feedback and shows it when there is. Add the "Try it myself" button to Learn.
2. **Milestone 5, Voice**: `ISpeechToText` and `ITextToSpeech` implementations (Azure first, then OpenAI, then Windows offline TTS),
   composer mic with F2, barge-in, auto-listen, silence auto-submit, voice settings with Test mic and Test voice, "Read answer aloud".
   Interfaces, fakes and settings fields already exist.
3. **Milestone 6, Mock Interview**: planner, interviewer loop with time pacing and end conditions, thread building, parallel coaching (max 3),
   debrief, Markdown export. Add `{{EMPLOYMENT_TYPE}}` to `planner.md`, `interviewer.md` and `debrief.md` and make the round type aware of it.
4. **Milestone 7, History and polish**: History screen, resume unfinished mocks, error banners, shortcuts. Learn session persistence.
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
- **Not sent through OpenRouter:** thinking effort (`LlmService.OptionsFor` only sets it for Anthropic) and the cache markers
  (`BuildSystemMessage` only splits for Anthropic), because the OpenAI adapter cannot carry Anthropic's `cache_control`. OpenRouter caches
  automatically for some providers. A thinking model on OpenRouter will therefore think at its own default, so prefer a fast model.
- **Measured:** the live catalogue on 2 October 2026 had 465 entries; the parser keeps 377 (the rest are batch copies and image or audio
  generators), 20 of them free, 4 with no price. Examples, per million tokens in/out: `anthropic/claude-sonnet-5.5` $2.00/$10.00,
  `anthropic/claude-haiku-4.5` $1.00/$5.00, `google/gemini-3.5-flash-lite` $0.30/$2.50, `deepseek/deepseek-v4-flash` $0.028/$0.056.
  **Not yet measured:** real calls through OpenRouter (speed, whether each model returns valid JSON, actual spend). The user should try the
  question generator on a cheap model first and watch the debug log.
- **Tests:** `OpenRouterSettingsTests` (Core), `OpenRouterTests` (Infrastructure: parsing, HTTP stub, factory, key encryption, no Anthropic-only
  features), `OpenRouterSettingsUiTests` (App: provider swap, search, sort, use buttons, real view without binding errors). `FakeCatalog` is in
  `Fakes.cs`; `VisualSnapshots` also writes `5-settings-openrouter-*.png`.
