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
| 4 Practice mode (typed): composer, submit, coach with the answer, retry with comparison, follow-ups | Done (attempts are saved to the Library) |
| 5 Voice: speech to text, text to speech (Azure, OpenAI, Windows), mic in composer, barge-in, timer, auto-listen | **Not started** (fakes and settings fields exist) |
| 6 Mock Interview: planner, interviewer loop, thread building, parallel coaching, debrief, Markdown export | **Not started** (prompts `planner.md`, `interviewer.md`, `debrief.md` exist, unused) |
| 7 History and polish | **Not started** |

Built beyond the spec (all in SPEC.md section 14): scenario question type; answer-length control and word-count readout;
JSON extraction that tolerates stray braces; thinking-effort setting; prompt caching with a reordered coach prompt;
background preparation of the next question; the technology bank (saved technical questions and general answers per technology
and seniority); By technology with an Other box; interviewer-style question length rules; full-time or contract role type with two
extra question types; scroll-wheel behaviour on Home; app icon; a full visual redesign.

Test status: **725 tests passing** (Core 319, Infrastructure 167, App 239). The last full verification was done with
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

725 tests: Core 319, Infrastructure 167, App 239.

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

1. ~~Milestone 4, Practice (typed)~~: done, see sections 19 to 21.
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
(21 to 30, mean about 25) and a dash lead-in in all 13; 10 of 13 began "At Acme, you...". The user's current client is Acme, so many Acme questions
are natural, but the model had no reason to spread out: it asked the same messaging-broker comparison twice in one session even though the first question
was in the avoid list (13 entries), and asked about authentication middleware and data isolation three times in seven minutes. Prompt prose to vary or to
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
authentication-middleware pair (different questions on one topic) shares four of 15 and is not flagged. In `GenerateQuestionAsync` a repeat gets one retry as a
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

---

## 21. Second Practice log, and saving Practice to the Library

**The second Practice log** (2 October 2026, SQL Server; four answers including one Try again and one follow-up). Findings:

- The delivery fix worked: `delivery` was null for every typed answer.
- **Try again with an unchanged answer.** The retry was word for word the first answer (123 words). It was sent, and the coach did not say nothing had changed (it ignored
  the rule that the first point is about what changed). Now `SubmitAsync` returns `SubmitResult.Unchanged` for an identical answer on a retry (whitespace and case ignored,
  retries only) and the box explains; nothing is sent. The first-point-about-change behaviour is therefore still **not observed on a real changed retry**.
- **Follow-up transcript worked:** the prompt carried "Interviewer: ... / You: ..." for the earlier question, and the coach judged the one-line SQL answer correctly.
- **The string "null" as a quote.** Three feedback points had `"quote": "null"` (the string), not a JSON null; shown as is it would read as something the candidate said.
  `CoachText.CleanOptional` now maps null, none, n/a, nil, undefined and empty text to nothing; `ForPractice` applies it to every quote and the delivery, and
  `CoachOutputViewModel` applies it again.
- All other quotes were grounded (verbatim or elided fragments of the answer). Replies were 5.8 to 7.7 s with 790 to 950 output tokens.

**Practice answers in the Library.** New table `PracticeAttempts` (migration `AddPracticeAttempts`), `IPracticeHistory` with `PracticeHistoryRepository`,
`InMemoryPracticeHistory` and `RoutingPracticeHistory` (Demo mode), `PracticeRecord` (Core). `PracticeEngine` records through `RecordAttemptAsync` (best effort,
exceptions swallowed) once the feedback is on screen, so every attempt is a row and nothing is kept for a failed call or an unchanged answer. It is a separate table from
`LearnHistory` on purpose: a learned question is one upserted entry per question and answer kind, a practice answer is always a new row, and the existing library keys and
the carry-over migration stay untouched.

`LibraryViewModel` now works on `LibraryItem` (a neutral record built from either source; `Key` is "L12" or "P7" because the two tables number separately). New: `KindFilters`
(All, Learned, Practised; shown by `HasKindFilter` only once something was practised), a Practice tag, a "Your answer" detail card (`DetailAnswer`, `DetailAnswerMeta`), the
feedback cards for practice entries (the detail uses `ForPractice()`, learned entries still use `ForLearning()`), search over the answer text, and delete routed to the right
repository. Follow-up lookup picks the saved follow-up of the same kind as the open entry, then the newest, from either table. `LibraryViewModel` takes `IPracticeHistory`
as an optional fourth constructor argument; without it the library behaves as before. Not done: no export, no "practise this question again" button from the library, no chart
of how retries improved.

## 22. Voice (milestone 5)

**What exists.** Core: `ISpeechToText`, `ITextToSpeech`, `ISpeechFactory`, `SpeechReadiness` (Abstractions.cs); `Speech/DictationText.Insert` (a dictated phrase goes in at the cursor with spaces only where needed), `AnswerInputTracker` (typed, voice or mixed; a hand edit of dictated text makes it mixed, clearing the box starts over), `SilenceDetector` (fires once per listening session, setting on, at least 10 words, N seconds since the last speech), `SpeechText.ForSpeaking` (strips markdown, brackets, arrows before a voice reads text), `SpeechException` (message is written for the user). Infrastructure `Speech/`: `AzureSpeechToText` (live partials, SDK 1.52.0), `AzureTextToSpeech` (SSML, voice and rate, escaped), `OpenAiSpeechToText` (NAudio records a 16 kHz WAV; on stop it is sent to whisper-1; no partials), `OpenAiTextToSpeech` (tts-1, WAV, voice and 0.5 to 2 speed), `WindowsTextToSpeech` (System.Speech), `NAudioRecorder` and `NAudioPlayer`, and `SpeechFactory` (picks the service from the saved settings each time, caches the speaker until a relevant setting changes so Stop reaches the voice that is speaking, gives fakes in Demo mode; Demo dictation rotates through three sample answers). The OpenAI classes take an optional endpoint so tests point them at a local `HttpListener`.

**Practice** (`PracticeViewModel.Voice.cs`). Mic button and **F2** toggle listening; starting the mic stops the voice (barge-in); the first mic use starts the timer; partial words show in a grey line and only final phrases enter the box (at `CaretIndex`, reported by the view through `SetCaret`, and the view moves the caret after an insert through `CaretRequested`); Submit stops the mic first so the last words are in the answer, then sends `INPUT_METHOD` voice, mixed or typed (duration goes to the coach only for non-typed). A new question is spoken when `SpeakQuestions` is on and the voice is ready; when it was heard to the end and `AutoListen` is on and the box is empty, the mic opens (silently skipped when dictation is not ready). **Repeat** says it again and explains why when it cannot. Silence auto-submit is checked in `Tick()`. **Read aloud** on the model answer is a toggle (`CoachOutputViewModel.ReadAloudCommand`, `IsReading`); Learn mode passes no command so it has no button. Speech events arrive on SDK threads and are marshalled through the captured `SynchronizationContext` (`OnUi`). Every failure becomes `SpeechMessage` under the box; text is kept and typing still works.

**Settings.** Speech card: providers, Azure key and region, Voice picker with **Load voices**, speed, **Test voice**, **Test microphone** (click to listen, click again to see what was heard); Behavior: `SpeakQuestions`, `AutoListen`, silence auto-submit. The speech services read the *saved* settings, so a test with unsaved speech changes says "Click Save first."

**Not verified here.** No microphone, speakers or keys were available, so Azure, NAudio, System.Speech and the real OpenAI endpoint are untested live; the OpenAI request shape (path, bearer, multipart, JSON body) is tested against a local stand-in, and everything else through fakes. First thing to check on a real machine: Azure dictation with a key, the Windows voice, and the OpenAI record-then-transcribe path. Known gaps: the microphone choice (`MicrophoneDeviceId`) is not used yet (default device only); the OpenAI TTS plays only after the whole file arrives; Mock Interview (milestone 6) will reuse these services for the live interviewer.

## 23. Technology concepts (the Concepts page)

**Why.** Learn and Practice both start from a saved profile with a resume and job description. Drilling one technology by its concepts needs neither, so a separate page was added instead of changing the Home flow.

**What exists.** Core: `Difficulty` (Beginner, Medium, Advanced) with `ToSeniority` (Junior, Mid, Senior; the question bank already keys questions by `Seniority`, so no bank change was needed and the difficulty reaches the prompts as `{{SENIORITY}}`, which `question_batch.md` and `question_generator.md` already match the difficulty to), and `ConceptSession.Profile(role, difficulty)`, which makes the throwaway `CandidateProfile` the engines need (`JobDescription` and `ResumeText` hold notes, `ConceptSession.IsConceptProfile` recognises it by its resume note). `TechBank.RoleKey` (lower case, whitespace collapsed), `GetSavedRoleTechnologiesAsync` (database only, never the model), `GetRoleTechnologiesAsync(role, refresh)` (saved list unless `refresh`; asks `role_technologies.md`, tidies the answer: trimmed, de-duplicated ignoring case, at most 24 of 60 characters each; an empty reply is not saved so it is asked again next time). Persistence: table `RoleTechnologies` (`RoleKey` primary key, `Role`, `TechnologiesJson`, `CreatedAt`), migration `AddRoleTechnologies`, on `ITechBankRepository` (`GetRoleTechnologiesAsync`, `SaveRoleTechnologiesAsync`), `TechBankRepository`, `RoutingTechBankRepository` and `InMemoryTechBankRepository` (Demo mode keeps its own). `TechBank.ClearAsync` does not touch role lists. Prompt `role_technologies.md` (variables `JOB_ROLE`, `MAX_TECHNOLOGIES`; user message "List the technologies."; the question generator model role). `AppSettings.ConceptRole` and `ConceptDifficulty` remember the last choices.

**App.** `ConceptsViewModel` and `ConceptsView` (four cards: role, technologies with Other, difficulty, how to practise) are reached from a **Concepts** sidebar item. Start raises `LearnRequested` or `PracticeRequested` with `LearnSessionRequest(ConceptSession.Profile(...), [TechnicalConcept], answerWords, technologies, FullTime)`, so the existing `QuestionPicker` "technologies given and nothing else asked" path does the rest (saved questions at the difficulty's level, error with Retry rather than a substitute, no model-written resume questions). `MainViewModel` takes the page as its last optional constructor argument and remembers where a session started (`_sessionOrigin`), so leaving a Learn or Practice session returns to Concepts, or to Home when it started there. `LearnViewModel.CanTailor` hides **Tailor to my resume** for a concept profile. Opening the page (`ConceptsViewModel.SuggestRole`) fills the role from the selected Home profile only when none was chosen, and shows an already saved list without any model call. A change of role clears the shown list but keeps ticks the next list also has. Typing in Other needs no role.

**Tests.** `ConceptTests` (Core: role fetch once however typed, saved read, refresh, tidy and cap, empty not saved, failure saves nothing, difficulty mapping, session profile, engine at the right level with no resume in any call, reuse across sessions), `RoleTechnologiesTests` (Infrastructure: SQLite round trip, restart, replace, unreadable row, Clear keeps lists, Demo isolation), `ConceptsTests` (App: the view model, navigation and return, the real view and its bindings). A `BankScript` marker (`RoleTagsMarker`) tells the role-list call apart.

**Known limits.** The model's list for a role is not checked against anything (the Ask again button is the remedy). A typed technology is used for the session but not added to the role's saved list. Difficulty reuses the three seniority levels, so a Senior resume session and an Advanced concepts session share saved questions for the same technology. There is no Concepts history of its own: answers appear in the Library like any other (a practice answer carries the profile name "Concepts: role").

## 24. Mock Interview (milestone 6)

**What exists.** Core: `RoundType` (six rounds, labels, descriptions; durations 15, 30, 45, 60), the JSON shapes `InterviewPlanDto`, `InterviewerTurnDto`, `DebriefDto` (+ focus ratings with a nullable 1 to 4 rating, top fixes), `MockTurn`, `MockThread`, `MockText.BuildThreads` (a `main_question` starts a thread; `follow_up`, `hint`, `clarification` and every candidate answer join it; `smalltalk`, `candidate_questions` and `closing` end it and are left out; a thread with no answer is `NotAnswered`), `HireSignals` (label, tone, rating text), `DebriefMarkdown`, `MockRecord` and `IMockHistory`. `MockEngine` is the state machine (Planning, InterviewerThinking, InterviewerSpeaking, CandidateAnswering, Ending, Debriefing, Done, Failed) and it knows nothing about speech: the screen calls `FinishedSpeakingAsync` when a line has been spoken. Requests follow SPEC 6.1: the first user message is `[app context] The candidate has joined the call. Elapsed 0 min of N min.`, assistant turns are the interviewer's JSON repeated as it was, each answer is followed by `[app context] Elapsed m min of N min. Answer took s s via typed|voice|mixed.`, past N plus 5 minutes the message also carries `[app context] Time is up. Wrap up now.` and the next interviewer turn is treated as the closing whatever it says. The message list is passed to the model as a copy. **Nothing is coached while the round runs.** When it ends (closing spoken, End interview, which skips the closing line), `EndRoundAsync` builds the threads, starts one Coach call per answered thread (`MODE mock`, the whole exchange as `CANDIDATE_ANSWER`, `DURATION_SECONDS` only for voice or mixed, three at a time through a semaphore) and, without waiting for them, the Debrief call; `Done` is reached when the debrief returns and `Changed` fires as each card finishes. A failed plan, interviewer turn or debrief leaves `Failed` with `RetryAsync` repeating only that step; a failed thread card has `RetryThreadAsync`. `Cancel()` (leaving the screen) makes `CanEnd` false and every later call a no-op.

**Persistence.** Table `MockSessions` (migration `AddMockSessions`): profile name, role, round, length, start, end, elapsed, finished, plan, turns, debrief, threads (with their coaching), hire signal, the parts as snake_case JSON. `MockHistoryRepository`, `InMemoryMockHistory`, `RoutingMockHistory` (Demo mode). The engine adds the row when the round ends and updates it when the debrief and when the last card finish; failures are swallowed. An interview that is abandoned is not recorded.

**App.** `AnswerComposer` (new) is the answer box with the microphone, extracted from Practice: text, caret, dictation lifecycle (start, stop that waits for the last words, failure, marshalling to the UI thread), typed/voice/mixed tracking, silence auto-submit. `Speaker` (new) says text one thing at a time. `PracticeViewModel` now owns an `AnswerComposer` and forwards the same property names. `MockViewModel` (status pill, clock, interviewer line or "(audio only)", Repeat, End interview, conversation list) speaks every new interviewer line, opens the microphone after it when auto-listen is on, lets the microphone open during the line (barge-in stops it), and hands over a `DebriefViewModel` when the engine reaches Done. `DebriefViewModel` (summary, badge, ratings, strengths, fixes, practice next, `ThreadCardViewModel` per question, export via `IDialogService.PickSaveFile`). Home: `SessionMode.Mock`, `RoundOptions`, `DurationOptions` (`SelectOption<T>`), `ShowQuestionText` (default from `ShowQuestionTextDefault`), `MockRequested`; the question types and answer length are hidden in Mock mode; `AppSettings.MockRoundType` and `MockDurationMinutes` remember the choices. `MainViewModel` navigates (mock to debrief, debrief to Practice and back, `_sessionOrigin`) and, in `OnCurrentPageChanged`, abandons a mock or stops Practice's voice when another page opens. Demo mode: `FakeLlmService` answers the planner, a scripted interviewer (small talk, two main questions, a follow-up, candidate questions, the close; the close also when the message says time is up), the debrief and the mock coach.

**Tests.** `MockEngineTests` and `MockModelsTests` (Core), `MockHistoryTests` (Infrastructure, including a whole Demo interview), `MockTests`, `DebriefTests` and extended `ViewSmokeTests` (App, with `SpeechDoubles`).

**Not verified here.** A real interviewer model: the demo interviewer is scripted, so how well the prompts keep one question per turn, react to answers and respect time is untested; the same goes for voice (see section 22). The first live run should check: the opening line, that follow-ups follow the answer, that the closing comes when the clock says, and that the debrief quotes the transcript.

**Known limits.** The interview is not resumable and is not saved if abandoned (milestone 7). The clock only checks the overtime limit when an answer is sent, so a candidate who goes silent past it is told by the screen ("Over time") but the interviewer is told only at the next answer. There is no History screen yet. Round and length are not part of the profile. A thread is coached with the Coach prompt in mock mode but without the answer-length control.

## 25. History and polish (milestone 7)

**Mock interviews are written as they go.** `MockEngine.RecordAsync` runs after every interviewer turn and every answer (serialised through a semaphore; the first write adds the row, later ones update it; a failed first write is retried as a first write). `MockRecord` gained `ProfileId` and `Employment` (migration `AddMockSessionProfile`), `IsUnfinished` and `NeedsDebrief`; `Finished` now means the round has ended (`HasEnded`), not that the debrief exists. Each `MockTurn` carries `ElapsedSeconds` (the interview's own clock) so a resumed interview can send the same context lines. `Cancel()` (leaving the screen) keeps the row, unfinished.

**Resuming.** `MockEngine.ResumeAsync(profile, record)` restores the plan and turns (`MockRecords.Restore`), rebuilds the model's message list (the joining line, each interviewer turn as stored, each answer with its context line and, past the overtime limit, the time-up line), sets the clock to the stored elapsed time (time away does not count) and continues: a left interviewer line is said again; an answer whose reply never came asks the interviewer again; a closing line ends the round after it; a record with `Finished` but no debrief runs the end of the round again (threads coached again, the debrief written) and updates the same row. A record that cannot be read leaves `Failed` with a message. `MockRecords.Restore` also turns stored threads back into cards (a thread whose coaching never finished shows as failed, since nothing will finish it).

**History.** `HistoryViewModel` (rows with `Open`, `Resume`, `Delete` as async commands; `Notice`, `Error`), `HistoryView`, `DebriefSource` (everything a debrief shows, from a live engine or a stored record) so `DebriefViewModel` serves both; for a stored one `Engine` is null (no Try again on a failed card), and with no profile (deleted) Practise buttons and the coach's follow-up buttons are not offered. The profile is found by id, or by name for rows written before ids were stored. `MainViewModel.ShowDebrief` wires a debrief for both routes (Back goes Home or to History, Practice from it returns to it). `HomeViewModel` gets an optional `IMockHistory` and shows `PendingMock` (Resume or Write the debrief, Discard with a confirmation); `MainViewModel.OnCurrentPageChanged` refreshes Home and History when they are shown.

**Notice bar.** `MainViewModel.Notice` is a reported problem (`ReportError`, used for unhandled exceptions once the window is loaded; before that a dialog) or else the setup notice (`AppSettings.HasLlmCredentials` false, not Demo, not on Settings, not dismissed). `RunNoticeActionCommand`, `DismissNoticeCommand`. A database that cannot be opened at start-up still ends in a dialog and shutdown: there is no window to show a bar in.

**Shortcuts.** Window: Ctrl+1 to Ctrl+5, F1 (list), Esc. Pages: F2 and Ctrl+R (Practice, Mock), Ctrl+N (Learn, Practice), Ctrl+Enter was already there. The list is an overlay in `MainWindow`; navigating closes it.

**The SPEC section 11 list, and where each item is tested.** Prompts render with a full variable set and a missing variable throws: `PromptLibraryTests`. JSON samples for each schema (question, coach, plan, interviewer turn, debrief), fenced JSON and leading prose, the repair retry: `JsonResponseParserTests`, `LlmServiceTests`. Practice never coaches before Submit: `PracticeEngineTests`. Learn coaches with an empty answer: `LearnEngineTests`. Mock ends on `end_interview` and on the time limit; threads group follow-ups; retry passes the previous attempt: `MockEngineTests`, `MockModelsTests`, `PracticeEngineTests`. Composer: partial then final text, starting the mic stops the voice, the text survives a failed submit: `PracticeVoiceTests`, `MockTests`, `PracticeTests`.

**Known limits.** History lists mock interviews only; learned questions and practised answers live in the Library and are not grouped into sessions (there is no session table; the data model of the spec was simplified to `LearnHistory`, `PracticeAttempts` and `MockSessions`). A resumed interview cannot recover a reply that was mid-flight when the app died. The overtime limit is only checked when an answer is sent. Nothing yet exports the History as a whole. A real model and real voice have still not been tried (sections 22 and 24). Open ideas, not requested: a code-level retry for long resume questions, a licence, renaming the folder to `interview-coach`, microphone choice (`MicrophoneDeviceId` is stored but not used).

## 26. First real mock interview log (3 October 2026)

**The run.** Voice mock, Mixed round of 15 minutes, Contract, planner and debrief on GPT-5 Mini, interviewer on Haiku 4.5 (OpenRouter). Planner 10.7 s, five interviewer turns of 1.4 to 2.2 s each (input 6.3k to 7.3k tokens, the plan, job description and resume are re-sent every turn and OpenRouter does no prompt caching), one coach call 10.4 s, debrief 11.4 s. The candidate ended the round after about 4.5 minutes. Everything worked end to end: the plan's phases added up to 15, the interviewer reacted to what was said, the question threads were built (one answered, one not), and only the answered one was coached.

**What the log showed, and what changed.**

- **Leading questions.** Two follow-ups listed the answers inside the question ("a queue, a cache, or something else?", "a lock, a semaphore, or a custom queue?") and the candidate then repeated one ("I used a semaphore"), so the answer was the interviewer's, not theirs. `interviewer.md` now says: ask open questions, never list the possible answers. It also limits recaps to a few words (the replies were three or four sentences retelling the answer).
- **A placeholder spoken aloud.** The plan's `opening_line` was "Hi, I'm [Interviewer Name], senior engineering manager"; the interviewer happened to rewrite it, but the text is spoken as written. `planner.md` and `interviewer.md` now forbid names and placeholders. The planner's `source` also came back as "jd | resume" (it copied the schema); it is now asked for the single best one.
- **A short round judged as a full one.** The debrief blamed the candidate for React, CI/CD and security "not being discussed", gave a backend rating of 4 and a `lean_yes` on 4.5 of 15 minutes, and its summary and fixes treated the unasked topics as gaps. The debrief is now given `{{ROUND_FACTS}}` (planned length, time used and as a percentage, who ended it, how many main questions were asked and answered, whether it ran over) and the prompt says: a topic that never came up is "not covered", not a weakness; below about half the planned time the signal is at most lean_yes or lean_no and the summary says it was a short round. This is a prose rule with the facts supplied by code; check it on the next short round, and if it is ignored, cap the signal in code.
- **Mis-heard words counted as mistakes.** Azure's transcript had "estate" (state), "cube" (queue), "weigh up" (wired up), "pipelinewith.net"; the coach quoted "cube pattern" as imprecise wording. `coach.md` and `debrief.md` now say a word that makes no sense in context is a mis-heard word, never a mistake and never evidence. Filler ("uh") is kept in the transcript and still counts, correctly.
- **A total read as one answer.** The three answers took 65, 52 and 50 s (167 s in total) and the coach wrote "the original answer was 2:47, aim for 60 to 90 seconds". The coach's copy of the exchange now labels each spoken answer with its own time (`You (52s): ...`; `MockThread.CoachTranscript`; the debrief and the export keep the plain transcript) and `coach.md` says the duration of a mock exchange is a total.
- **No length target in Mock.** The mock coach got `Requested model answer length: (none)` and wrote a 215-word answer to "walk me through what you have been doing" (the usual range is 130 to 200). It now gets the range for the question type, as in Practice.

**Not changed, worth knowing.** The model answer in the coach reply added claims the candidate had not made ("targeted caching and telemetry", an alert on queue depth) with the metric left as a bracketed placeholder; the existing "stay true to the candidate" rules did not stop it, and the next log should say whether it recurs. The coach's follow-up hints are phrased "Say you keep retries per message...", which can read as putting claims in the candidate's mouth.

**Idea from this log, not built.** Speech recognition quality is the weakest link in a technical interview. Azure supports a phrase list (`PhraseListGrammar`) and Whisper takes a prompt, so the technologies of the job description (already cached), the employers on the resume (already cached) and the plan's focus areas could be passed to the recognizer to bias it towards ".NET", "gRPC", "Kafka" and the employer's name. It needs a small change to `ISpeechFactory.CreateSpeechToText` and its fakes.

## 27. Speech phrase list

**Why.** The first real voice interview (section 26) had "estate" for state, "cube" for queue and "pipelinewith.net" for pipeline with .NET. General recognition is good; technical vocabulary and employer names are what it gets wrong, and those are the words an interview is made of.

**What it does.** `ISpeechToText.SetPhrases(IReadOnlyList<string>)` is a default interface method that does nothing, so only the two real recognizers change. `AzureSpeechToText` adds the terms to a `PhraseListGrammar` on the recognizer it creates; `OpenAiSpeechToText` sends them as Whisper's `prompt` ("A technical job interview. Terms that may be spoken: ...", cut at a term boundary at 600 characters; none is sent when there are no terms). `AnswerComposer.Phrases` is handed to every recognizer it starts. Both `PracticeViewModel` and `MockViewModel` set it.

**Where the terms come from** (`SpeechPhrases`, `SpeechPhraseSource` in Core, in priority order): the technologies picked for the session (Concepts, By technology); the technologies already read from the job description (`TechBank.GetSavedTechnologiesAsync`, repository only); the terms on the resume's own skills lines (`Skills:`, `Technologies:`, `Environment:`, `Languages:` and the like, found with a regular expression, versions stripped: "SQL Server 2012" becomes "SQL Server"); the employers and projects already read from the resume (`GetSavedResumeTopicsAsync`, repository only); the job role; in Practice the technology of the question on screen; in a mock interview the plan's phase topics and focus areas, which come first once the round is planned ("order import pipeline", "checkout migration"). Terms are tidied (trimmed, de-duplicated ignoring case, at most four words and 40 characters, at least one letter, at most 100 in all). **It never calls the model**: if the job description or resume has not been read yet, those terms are simply absent until a session has read them.

**Limits.** It helps vocabulary, not mis-hearings of ordinary words ("estate" for "state" is not a listed term). The terms are fixed when the microphone starts, so a technology that only appears mid-session (a follow-up topic) is not added until the microphone is started again. Azure's phrase list is applied to live recognition only; nothing is stored. Whether it helps in practice is untried against the real services: compare the next voice log with the one of 3 October.

## 28. Releasing: the exe, the installer, GitHub Releases

**What there is.** `tools/publish.ps1` runs the tests, then `dotnet publish` for `win-x64` as one self-contained single-file exe (`PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract` for the Speech SDK's native libraries, compressed; about 93 MB; no trimming, which does not suit WPF and EF Core), and zips it with the `Prompts` folder. With `-Installer` it then runs Inno Setup on `installer/InterviewCoach.iss` and writes `dist/InterviewCoach-Setup-<version>.exe` (87 MB). `.github/workflows/ci.yml` builds and tests every push and pull request on `windows-latest`. `.github/workflows/release.yml` runs on a pushed tag `v*`: it takes the version from the tag, makes sure Inno Setup is there (Chocolatey if not), runs `publish.ps1 -Installer` and creates a GitHub release with the setup file and the zip attached and generated notes.

**The installer.** Per-user by default (`PrivilegesRequired=lowest`, into `%LOCALAPPDATA%\Programs\Interview Coach`, no administrator prompt; the wizard can switch to all users), Start Menu entry, optional desktop shortcut, optional launch at the end, an entry in Windows' Installed apps, `MinVersion=10.0.19041`, 64-bit only. The `AppId` GUID must never change, or an upgrade would install beside the old version. A new version closes a running app and replaces it. User data lives outside the install folder (`%LOCALAPPDATA%\InterviewCoach`), so upgrades keep it; an uninstall asks whether to delete it too, defaulting to No, and never asks when silent.

**Tested here** (Inno Setup 6.7.3 installed with winget for the purpose): the setup compiles; a silent per-user install into a temporary folder put in the exe and all nine prompts, a Start Menu shortcut and the Installed apps entry; the installed exe started and showed its window; a silent uninstall removed the files, the shortcut and the entry and left the database alone. **Not tested here**: the two workflows (they run only on GitHub; the first push of `ci.yml` and the first tag will show whether the .NET 10 SDK setup, the WPF tests on a runner and `choco install innosetup` behave), the wizard's pages by eye, upgrading over an older installed version, and an all-users install.

**Known limits.** Not code-signed, so SmartScreen warns on first run from the internet ("More info", "Run anyway"); signing needs a certificate (SignPath Foundation signs open-source projects for free, an application is needed). There is no auto-update (the app does not check for new releases; people reinstall from the Releases page). x64 only; `-Runtime win-arm64` works for the exe but the installer script allows x64-compatible only. The repository has no licence file yet, which anyone installing from a public repository will notice.

**Release notes.** `tools/release-notes.ps1 -Tag vX.Y.Z` (called by the release workflow, which checks out with `fetch-depth: 0` so the tags are there) writes the notes: "What's new in X.Y.Z" as the subjects of the commits between the previous tag (by version order) and this one, at most 40, then the install paragraph and the SmartScreen note; `gh release create` still adds `--generate-notes` for the compare link. It exists because commits go straight to `main`, so GitHub's generated list (merged pull requests) was always empty. Consequence: **commit subjects are the release notes**, so write them to read well to a user ("Settings: an Update now button once a newer version is found"). Not filtered (docs and test commits appear too); the first release lists the last 40 commits. Tried locally for v1.1.1, v1.1.0 and v1.0.0; not yet run in the workflow on GitHub. Notes of releases published earlier are unchanged (edit with `gh release edit vX.Y.Z --notes-file ...`).

## 29. Licence, auto-update, signing

**Licence.** MIT (`LICENSE`, 2026, holder `srinadhmanchikalapudi`, the git user name; change it to a legal name if wanted). `THIRD-PARTY-NOTICES.md` lists the direct dependencies and their licenses (read from the NuGet metadata of every package in the build: 76 MIT, 5 Apache-2.0, NAudio's own MIT text, and one exception). **The exception is `Microsoft.CognitiveServices.Speech`** (Azure speech): it is under Microsoft's Software License Terms, copied to `licenses/`. Those terms let the SDK's redistributable code be shipped in an application but require that recipients agree to terms that protect Microsoft at least as much, an indemnity from the distributor, and a notice that the SDK may send usage data to Microsoft. The installer shows the MIT license, installs `LICENSE`, `THIRD-PARTY-NOTICES.md` and `licenses/` next to the program (the zip carries them too), and the notices say the speech feature is under Microsoft's terms and can be avoided (OpenAI or Windows voices, or typing). This is not legal advice: if the project becomes more than a hobby, read section 2 of that license or drop the Azure option. The project file carries `Copyright`, `Company`, `PackageLicenseExpression`.

**Auto-update** (milestone-free feature, tested). `GitHubUpdateChecker` (Infrastructure) reads `/repos/{owner}/{repo}/releases/latest` (public, no key, never a draft or pre-release), takes the tag as the version (`UpdateVersions`: "v1.2.3", "1.2.3-beta+abc" and "1.2" are versions, "nightly" is not; comparison on the first three numbers), the asset `InterviewCoach-Setup-*.exe`, GitHub's `digest` (sha256) when present and the release page; it never throws for network trouble (a failed result with a sentence). `UpdateInstaller` downloads over HTTPS only from github.com or githubusercontent.com (a look-alike host is refused), reports progress, deletes a file that is cut short or does not match the checksum, and starts it with `/SILENT /NORESTART /CLOSEAPPLICATIONS /RESTARTAPP=1` (a visible progress window; the installer script has a second `[Run]` entry that starts the program again when `/RESTARTAPP=1` was passed). `InstallationInfo` tells an installed copy from an unzipped or built one by the installer's uninstall entry (key from the fixed `AppId`; a test pins the two together); only an installed copy is updated in place, others get a Download button that opens the release page. `UpdateService` (App) runs the check on start when `AppSettings.CheckForUpdates` (default on) allows, not from a debugger, not when `ICOACH_NO_UPDATE_CHECK=1`, at most every 24 hours (`LastUpdateCheck`, saved only after a successful check), and exposes `Phase`, `Available`, `Progress`, `Error`, `StatusText`. `MainViewModel.Notice` shows an update bar (Available: Update now or Download; Downloading: the percentage, no button; Failed: error with Open download page) that outranks the missing-key bar and is not shown on Settings; dismissing hides that version until the next start. Update now asks first (`IDialogService.Confirm`). Settings has an About and updates card (version, the checkbox, Check for updates now, Open the releases page). Dev builds do not nag: the check is skipped under a debugger.

**Verified for real** (installers of 1.0.0 and 1.0.1 built with `publish.ps1 -Version`): installing 1.0.0 silently into a folder, starting it, then running the 1.0.1 setup with exactly the arguments above and without `/DIR` replaced the program in place (version 1.0.1), closed the old process, found the existing install by itself, left a single uninstall entry, started the program again, and kept the user data. **Not verified**: the check against the real GitHub API (no release exists yet), the update bar and the confirmation dialog on screen in a running app, an update of an all-users install (it will raise a UAC prompt), and the checksum field (GitHub added `digest` to release assets in 2025; if it is ever missing the file is accepted over HTTPS without it).

**Not signed, and how to change that** (needs an account only the owner can create). Unsigned programs get SmartScreen's warning on first run, and an installer that downloads and starts a file is exactly what SmartScreen is suspicious of. Options, in the order I would try them: (1) **SignPath Foundation** gives free code signing to open-source projects: apply at signpath.org (an OSI license is required, which the MIT license now satisfies; mention that Azure Speech is a proprietary dependency), then a GitHub Action submits the built exe and installer for signing in the release workflow; the certificate names SignPath Foundation as publisher. (2) **Azure Artifact Signing** (formerly Trusted Signing), a few dollars a month, identity verification of the publisher, usable from GitHub Actions; availability for individuals depends on country. (3) A conventional **OV code-signing certificate** from a certificate authority (about 200 to 400 dollars a year; the private key must now be on hardware or a cloud signing service, so signing from GitHub Actions needs one of those). Signed programs still start with little SmartScreen reputation and the warning fades as downloads accumulate. Wiring is small once an account exists: sign `InterviewCoach.App.exe` before Inno Setup packs it and sign the setup file afterwards, in `.github/workflows/release.yml`; and have `UpdateInstaller` verify the downloaded setup's Authenticode signature before running it.

**Other gaps.** No update channel for pre-releases; no delta updates (every update downloads the whole 87 MB); no "skip this version" that survives a restart.

## 30. Answer rules

**What.** `CandidateProfile.AnswerRules` (up to `MaxAnswerRulesLength` = 1,500 characters, migration `AddAnswerRules`, default "") is the candidate's own instruction for how answers should be written. Home has a card below Resume (`HomeViewModel.AnswerRules`, `AnswerRulesStats`, `AddAnswerRuleCommand` and the `AnswerRuleExample` buttons; dirty, revert and save work as for the other fields). Sessions use the saved profile, so the rules take effect after Save.

**Prompts.** `PromptVars.ForProfile` adds `ANSWER_RULES` (`PromptVars.AnswerRules`: trimmed, cut to the limit, the closing tag `</candidate_rules>` removed so rules cannot close their own block, null when blank, rendered as "(none)"); `PromptVars.Generic` leaves it null. Only two templates use it: `coach.md` (a fixed section "THE CANDIDATE'S OWN RULES" before the dynamic details, so the cacheable prefix stays fixed, and the `<candidate_rules>` block after `<previous_attempt>`) and `debrief.md` (a rule in the Rules list and a block after the round facts). The section says the rules win over the length targets, the type structures, "never sound like STAR", the no-labels rule and the word lists; that a rule with labels means plain words, never markdown; that a rule with a length replaces the requested length; that feedback names where the answer departs from the rules; and that the JSON shape, the no-invention rule and honest feedback never bend. Contradicting rules: the later one wins. The interviewer, planner, question generator, question batch and the tag and topic prompts do not carry the rules (a test pins this).

**Learn and the shared bank.** Saved general answers are shared and written with `PromptVars.Generic`. With rules, `LearnEngine` does not read a saved answer, writes the general answer with the rules (`TechBank.WriteGeneralAnswerAsync(..., answerRules)`) and does not save it, so the bank holds only rule-free answers. Personalize (resume-tailored) and Practice and Mock coaching use `ForProfile`, so they carry the rules. Concepts sessions use a made-up profile with no rules. Answers saved or in History before the rules existed are left alone; there is no regenerate button.

**Limits.** The model follows the rules by instruction, not by code: the on-screen word count and range still come from the length setting, so a rule that gives a different length makes the count and range disagree with the answer. No per-question-type rules and no detection of conflicting rules. Not tried with a real model: how well each provider obeys a rule that fights the built-in guidance (try "use STAR" on a concept question, and "under 60 words" on a behavioral one, and compare).

**Tests.** `AnswerRulesTests` (Core: the variable, which prompts carry it, Learn with and without rules, Personalize), `AnswerRulesUiTests` (App: shown, dirty, save, revert, session request, buttons, limit, counter), the repository round trip, and `PromptLibraryTests` with the new variable. 1212 tests in all.

**Fix after 1.1.0 (found by use).** "Check for updates now" in Settings only reported "Version X is available." and offered no way to install it, because the update bar is hidden on Settings. Settings now shows an **Update now** button (**Download the update** for an unzipped copy) once a version is known: `UpdateService.InstallWithConfirmAsync` (the confirmation plus install, shared with the bar), `UpdateService.HasUpdateToInstall`, `SettingsViewModel.InstallUpdateCommand`, `CanInstallUpdate`, `InstallUpdateText`. Five new tests; 1212 in all. The button was not seen on screen in a real update.

## 31. Backlog (not started)

**Coding practice page (LeetCode style).** Asked about on 5 October 2026; deferred by the owner ("nothing for now"). Idea: a **Code** page next to Concepts. Pick a language (SQL, C#, Python), a difficulty (Easy, Medium, Hard) and optionally a topic; get an original problem with examples, a code editor, Run and Submit, a three-step hint ladder (nudge, approach, nearly the solution; reveals are recorded), and feedback with three parts: correctness against tests, a code review (complexity, edge cases, readability, naming) and room for improvement (a cleaner or faster alternative). Attempts go to the Library like Practice answers.

Design notes from the discussion. The existing "Coding talk-through" question type only judges an explanation; nothing runs code. The key choice is whether code runs. Model-judged only works for every language and is cheapest but can wrongly say "correct". Running code: **SQL** on in-memory SQLite (already a dependency; result-set comparison with a reference query; dialect differs from T-SQL), **C#** through Roslyn scripting (adds about 10 to 20 MB; separate process with time limit), **Python** through an installed interpreter (not bundled; model-only feedback when absent). Make problems trustworthy by having the model write a problem **and a reference solution** and deriving the expected outputs by running the reference, never from the model's stated outputs; add a "report a bad problem" button; write original problems, not copies of real LeetCode ones; save problems per language and level like the question bank. A new engine (state machine like Practice), prompts, DTOs and tables would follow the existing patterns. Code goes to the model provider (update the privacy text). Rough effort: a first version (editor, generated problems, hints, model-judged review, Library) one to two days, then half a day to a day and a half per language for real execution (SQL, then C#, then Python).

Recommended order: first version for all three languages with model-judged feedback labelled "not run", then real execution for SQL, C#, Python. Open questions for the owner: model-judged first or real execution from day one; a syntax-highlighting editor package (AvalonEdit, MIT) or a plain monospace box; tailored to the profile's role and job description, or standalone like Concepts.

**macOS version.** Asked about on 5 October 2026; planned in detail in section 32, not started. Summary: WPF is Windows-only, so this is a port with an Avalonia UI for macOS next to the WPF app (shared view models), a small set of platform adapters (Keychain for keys, the system voice and audio capture, paths), a signed and notarized `.dmg` built by CI, and a Mac update path. Only 4 Infrastructure files use Windows APIs and the view models are already WPF-free, so the work is mostly the 12 views (about 2,900 lines of XAML), the platform adapters, tests and packaging. Estimate four to six weeks of focused work (first usable text-only build in about two to three weeks). Needs a Mac for testing and an Apple Developer account (about $99 a year) for signing. Decisions to make first are in 32.2.

## 32. macOS port plan

**Status:** planned, not started. Asked about on 5 October 2026; the owner asked for a detailed plan and a backlog entry (section 31). Nothing here has been tried: there was no Mac available, and every Mac-specific claim below is from documentation and experience, marked **verify** where it matters.

**Goal.** A macOS version with the same features (Learn, Practice, Mock Interview, Concepts, Library, History, Settings, answer rules, voice with Azure, OpenAI or the system voice, updates from GitHub Releases), signed and notarized, downloadable from the same GitHub release as the Windows installer. The Windows app keeps working throughout; no step may reduce it.

### 32.1 Inventory (measured from the code on 5 October 2026)

| Part | State | What it means |
|---|---|---|
| `Core` | `net10.0`, no UI or SDKs | Runs on a Mac unchanged (engines, prompts, models, ports, about 500 tests) |
| `Infrastructure` | targets `net10.0-windows10.0.19041.0`, but **only 4 files** use Windows APIs | `SettingsStore` (DPAPI `ProtectedData`), `NAudioDevices` (NAudio recorder and player), `WindowsAndFactory` (System.Speech voice and the speech factory), `UpdateInstaller` (registry `InstallationInfo`, Inno Setup launch). Everything else is portable: `LlmService`, `ChatClientFactory`, parsers, EF Core SQLite (native SQLite ships for macOS), PdfPig, OpenXml, the OpenAI calls, the GitHub checker, the Azure Speech SDK (**verify** it ships macOS runtimes for arm64 and x64) |
| Audio ports | `IAudioRecorder`, `IAudioPlayer` already exist | Only NAudio implements them: a Mac needs its own |
| `App` view models | about 4,400 lines; **WPF-free** in practice (the only `System.Windows` use is `ICommand`, which is portable) | Can be shared as they are |
| `App` services | `WpfDialogService` is the only WPF service. `IDialogService` is synchronous (`Confirm`, `PickFile`, `PickSaveFile`) | Avalonia dialogs are async: 11 call sites in `src` and about 19 test references change |
| `App` views | 12 XAML files, about 2,900 lines, plus `App.xaml` (308) and 3 controls (`HighlightedTextBlock`, `PasswordBoxBinder`, `WheelScrolling`), code-behind about 200 lines | The real porting work |
| Look | Fluent theme `DynamicResource` keys, `Segoe UI`, **32 distinct Segoe Fluent Icons glyphs**, `BoolToVis` converters | Re-map to Avalonia theme resources, replace the icons with an icon set, use bound `IsVisible` |
| Shortcuts | Ctrl+1..5, Ctrl+N, Ctrl+R, Ctrl+Enter, F2, F1, Esc | Cmd on Mac; F2 needs Fn on laptop keyboards |
| Tests | Core 496 portable; Infrastructure 280 mostly portable (DPAPI and Windows voice tests are not); App 436 run real **WPF** views (Windows only) | Avalonia headless view tests needed |
| Packaging | Inno Setup, `publish.ps1`, workflows on `windows-latest` | New: `.app` bundle, signing, notarization, `.dmg`, a macOS CI job |

### 32.2 Decisions to make first (recommendation first)

| # | Decision | Recommendation | Why |
|---|---|---|---|
| D1 | UI strategy | **Avalonia app for macOS next to the WPF app**, sharing view models; keep WPF until the Avalonia app reaches parity; decide later whether to retire WPF | Lowest risk to Windows users; the views are thin because view models hold all state. Cost: two view layers to keep in step. Replacing WPF outright would give one UI codebase but changes the Windows look at the same time as the port. |
| D2 | CPU | Apple Silicon (`osx-arm64`) first, Intel (`osx-x64`) as a second asset once the pipeline works | Same code, one more build and download |
| D3 | Distribution | Direct download (`.dmg` on GitHub Releases), signed and notarized. Not the Mac App Store | The store needs the app sandbox and review; out of scope |
| D4 | Apple Developer Program (about $99 a year) | Needed for a Developer ID certificate and notarization. Without it builds are unsigned and users must right-click Open (or clear the quarantine flag): acceptable for a beta only | Gatekeeper |
| D5 | A Mac | Needed for the microphone, Keychain, signing checks and a clean-machine test. GitHub's `macos-latest` runners can build and run headless tests but cannot test the microphone or a person's Gatekeeper experience | |
| D6 | Updates on Mac | Version 1: **Download the update** (opens the release page), as for any copy not installed by Setup. Version 2: replace the `.app` in place after the program quits | In-place needs a writable location and handling of App Translocation |

### 32.3 Target structure

```
Core                       net10.0                       unchanged
Infrastructure             net10.0                       portable adapters (was Windows-targeted)
Platform.Windows           net10.0-windows10.0.19041.0   DPAPI, System.Speech, NAudio, registry install info, Inno Setup launch
Platform.Mac               net10.0                       Keychain, say and afplay, audio capture, bundle install info
Presentation               net10.0                       view models and UI services (moved out of App), no WPF or Avalonia types
App                        WPF, Windows                  views, theme, host (existing)
App.Avalonia               net10.0                       views, theme, host for macOS (also run on Windows in CI)
```

New or changed ports: `ISecretProtector` (protect and unprotect a string), `IAppPaths` (data folder, logs folder, install folder), `ISystemVoice` (or a platform `ITextToSpeech` registered per OS), `IClipboard` if a view model needs it, `IDialogService` made async, and the existing `IAudioRecorder`, `IAudioPlayer`, `IInstallationInfo`, `IProcessLauncher`. Registration moves into a per-platform `AddPlatformServices()`.

### 32.4 Workstreams

Effort is focused working days for one developer who is also testing; ranges, not promises.

**P0. Seams on Windows, no behaviour change (about 3 days).**
1. Add `ISecretProtector`; `SettingsStore` takes it (Windows implementation: DPAPI). Keep the `dpapi:` marker; add a `keychain:` marker for Mac.
2. Add `IAppPaths`; replace `Environment.SpecialFolder.LocalApplicationData` uses (`SettingsStore.AppDataDirectory`, `Database.DefaultPath`, logs). Reason: on macOS .NET returns `~/.local/share` for that folder, which is not where a Mac app belongs; Mac path is `~/Library/Application Support/InterviewCoach` (logs `~/Library/Logs/InterviewCoach`).
3. Move `NAudioDevices`, the Windows voice and the registry `InstallationInfo` into `Platform.Windows`; retarget `Infrastructure` to `net10.0`; keep the Windows tests green.
4. Add a speech provider value `System` (the operating system's voice); treat the stored `Windows` as an alias on load so existing `settings.json` files still work.
5. Make `IDialogService` async (11 source call sites, about 19 test references).
6. Generalise the update checker to per-OS asset patterns (`InterviewCoach-Setup-*.exe` for Windows, `InterviewCoach-<version>-osx-<arch>.dmg` for Mac, chosen with `RuntimeInformation`).
*Exit:* Windows app and all 1,212 tests unchanged in behaviour; `Infrastructure` builds for `net10.0`.

**P1. Presentation project (about 1 day).** Move view models and the services that are not WPF (`UpdateService`, `AnswerComposer`, `Speaker`, converters of logic) from `App` to `Presentation`; `App` references it. Keep namespaces so tests do not change.
*Exit:* tests green; `Presentation` has no `System.Windows` reference.

**P2. Avalonia app (about 8 to 11 days).**
1. Project, host and DI (`App.Avalonia`), `ThemeVariant` following the system (light and dark), the window with sidebar, notice bar, update bar, shortcut overlay.
2. Theme: map the Fluent resource keys the WPF styles use to Avalonia's Fluent theme resources; define the shared styles (`Card`, `Chip`, `ChipRadio`, `ModeCard`, ...) again; system UI font.
3. Icons: replace the 32 Segoe glyphs with one icon set (an Avalonia icon package or SVG path data) so the same names work on both.
4. Port the views in order of value: Main, Home (the largest, 447 lines), Learn, Coach output, Practice, Settings (385), Mock, Debrief, Library, History, Concepts. Bindings stay the same (`IsVisible` bound to bools replaces `BoolToVis`).
5. Controls: `HighlightedTextBlock` (inlines), `PasswordBoxBinder` (a `TextBox` with a password character and a two-way binding needs no helper), `WheelScrolling` (re-check on a trackpad; may be unnecessary).
6. Shortcuts: Cmd in place of Ctrl; the microphone toggle gets a Mac-friendly key (F2 stays as a second binding); a native menu (About, Settings with Cmd+comma, Quit with Cmd+Q).
*Exit (M1):* the app starts on a Mac in Demo mode and Home, Learn and Settings work; all screens by the end of P2.

**P3. Mac platform adapters (about 4 to 5 days).**
1. `MacSecretProtector`: API keys in the **Keychain** (service `InterviewCoach`, account = setting name) through the `security` command or Security.framework calls; `settings.json` stores a `keychain:` marker, never the key. Keys do not roam between Macs.
2. System voice: `say` (voice list from `say -v '?'`, rate), cancellation by ending the process; WAV playback (OpenAI voice) through `afplay`.
3. Dictation: Azure through the SDK's default microphone (**verify** the macOS native libraries load and the microphone works); OpenAI path needs a recorder for 16 kHz mono WAV: evaluate PortAudio bindings, miniaudio-based libraries and OpenAL; pick one in a one-day spike.
4. Microphone permission: `NSMicrophoneUsageDescription` and the audio-input entitlement; a clear message when permission is denied.
5. `MacInstallationInfo`: installed when the app runs from `/Applications` or `~/Applications` (not from a mounted `.dmg` or an App Translocation path).
*Exit (M2/M3):* text features with a real model and Keychain keys; then voice.

**P4. Tests and CI (about 3 to 4 days).** Avalonia headless view tests replacing `WpfHost` for the new views (a binding error must fail a test: hook Avalonia's binding log); a snapshot harness for light and dark; platform-specific tests marked so DPAPI and Windows-voice tests run only on Windows and Keychain tests only on macOS; a `macos-latest` job in `ci.yml` that builds everything and runs Core, Infrastructure and the Avalonia tests (the WPF tests stay on Windows).
*Exit:* CI green on both runners.

**P5. Packaging, signing, notarization, release (about 3 days plus waiting for the Apple account).**
1. `tools/publish-mac.sh`: `dotnet publish -r osx-arm64 --self-contained` (not single file: extracting native libraries at run time interferes with signing), assemble `Interview Coach.app` (`Info.plist` with bundle id and version, `AppIcon.icns` made from the existing icon with `iconutil`, `Prompts` inside the bundle).
2. Sign every Mach-O file and the bundle with a **Developer ID Application** certificate and the hardened runtime; entitlements for .NET: `allow-jit`, `allow-unsigned-executable-memory`, `disable-library-validation` (third-party native libraries), `device.audio-input`.
3. Notarize with `xcrun notarytool submit --wait`, staple the ticket, build the `.dmg` (`create-dmg` or `hdiutil`), sign and notarize it, name it `InterviewCoach-<version>-osx-arm64.dmg`.
4. `release.yml` gets a macOS job (secrets: certificate as base64 `.p12` and its password, Apple team id, an App Store Connect API key or app-specific password) that attaches the `.dmg` to the same release; the Windows job is unchanged. Release notes gain a Mac install paragraph (drag to Applications; first run).
*Exit (M4):* a tag produces both installers; `spctl --assess` accepts the app on a clean Mac.

**P6. Updates on Mac.** v1 (about 0.5 day): the checker picks the Mac asset and the button reads **Download the update**. v2 (about 2 days): download and verify the checksum, quit, a small helper script replaces the `.app` and relaunches; handle a read-only location and App Translocation by telling the user to move the app to Applications.

**P7. Documentation and polish (about 1 to 2 days).** README (Mac install steps and the Gatekeeper note), docs set (platform section, new ADRs), troubleshooting (Keychain prompts, microphone permission, "app is damaged" and quarantine), test counts, release checklist.

**Total:** about 24 to 31 working days, roughly **four to six weeks** of focused work. The earlier figure of one to two weeks covered only a text-only UI port without tests, packaging or Mac verification.

### 32.5 Milestones

| | What works | Rough point |
|---|---|---|
| M1 | Starts on a Mac in Demo mode; Home, Learn, Settings | after about 2 weeks |
| M2 | All screens, real model, Keychain keys, unsigned build | after about 3 to 4 weeks |
| M3 | Voice: Azure and OpenAI dictation, system voice, microphone permission | after about 4 weeks |
| M4 | Signed, notarized `.dmg` built and attached by CI | after about 5 weeks |
| M5 | In-place update | after about 6 weeks (optional) |

### 32.6 Mac-specific things to remember

- **Data folder:** do not use `LocalApplicationData` on macOS (it is `~/.local/share`); use `IAppPaths`.
- **Secrets:** Keychain, not a file; the first access may prompt; deleting the keychain item removes the key.
- **Gatekeeper and quarantine:** a downloaded `.dmg` is quarantined; notarized and stapled apps open normally; an unsigned build needs right-click Open.
- **App Translocation:** an app run straight from Downloads or a mounted image runs from a read-only random path, so in-place update cannot work there.
- **Keyboard:** Cmd replaces Ctrl; F2 needs Fn; reserve Cmd+Q, Cmd+comma, Cmd+W for the system meaning.
- **Voices:** `say` voices differ from Windows voices; the voice list and the default must be re-chosen on first run.
- **Fonts and icons:** the system font is San Francisco; Segoe fonts are not there.
- **Both CPUs:** an Intel Mac needs the `osx-x64` build until the Apple-Silicon-only decision is made.
- **Case:** the default Mac file system ignores case; a Linux CI runner does not (file names and resource names must match exactly).

### 32.7 Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Avalonia theme and control differences take longer than planned | Schedule | Port Home and Learn first as a vertical slice; keep the shared styles small |
| Azure Speech SDK does not work on macOS as expected | No Azure dictation | Spike in P3; fall back to the OpenAI path or system dictation |
| No good cross-platform audio capture library | No OpenAI dictation | One-day spike; fallback: record with `ffmpeg` or `sox` if installed, or ship Azure only on Mac |
| Async dialogs ripple through view models and tests | Regression | Do it first, on Windows, in P0 where the existing tests catch mistakes |
| Notarization or hardened-runtime entitlements are fiddly | Release delay | Start with an unsigned build; get the signing pipeline working on a tiny sample early |
| No Mac available | Cannot verify | Borrow or rent one before M2; mark unverified items in the docs |
| Two view layers drift apart | Maintenance | Keep views thin; share view models and tests; decide on retiring WPF after M4 |
| Package size grows | Download size | Self-contained arm64 build is about the same as Windows; measure |

### 32.8 Out of scope

Mac App Store, iOS, Linux (Avalonia would make it cheap to add later: one more RID, a packaging format and a secret store), a universal binary (two downloads instead).

### 32.9 Definition of done

A tag builds a Windows installer and a signed, notarized macOS `.dmg`; the Mac app does everything the Windows app does except where listed in the known limits; keys are in the Keychain; CI is green on Windows and macOS; the Mac install and update steps are in the README; the Windows app and its 1,212 tests were never broken along the way.
