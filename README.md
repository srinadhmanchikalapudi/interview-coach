<p align="center">
  <img src="docs/images/logo.png" alt="Interview Coach logo" width="96">
</p>

<h1 align="center">Interview Coach</h1>

<p align="center">
  A Windows desktop app that helps you prepare for a specific job interview.<br>
  Give it a job description and your resume; an LLM writes the questions an interviewer would ask and shows you how to answer them.
</p>

<p align="center">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4">
  <img alt="WPF" src="https://img.shields.io/badge/UI-WPF%20%28MVVM%29-0078D4">
  <img alt="Tests" src="https://img.shields.io/badge/tests-1040%20passing-2EA043">
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey">
  <img alt="Status" src="https://img.shields.io/badge/status-Learn%20mode%20complete-blue">
</p>

---

## Contents

1. [What it does](#what-it-does)
2. [Screenshots](#screenshots)
3. [Status and roadmap](#status-and-roadmap)
4. [Getting started](#getting-started)
5. [Using the app](#using-the-app)
6. [How it works](#how-it-works)
7. [Architecture](#architecture)
8. [Prompts](#prompts)
9. [Speed and cost](#speed-and-cost)
10. [Data, settings and privacy](#data-settings-and-privacy)
11. [Testing](#testing)
12. [Repository layout](#repository-layout)
13. [Development guide](#development-guide)
14. [Documentation](#documentation)
15. [Commit conventions](#commit-conventions)

---

## What it does

Most interview practice is generic. Interview Coach is built around **one real job**: you save a *profile* (job role, seniority,
job description, resume) and every session is generated from it.

**Learn mode** (built) picks a question an interviewer could plausibly ask for that job, then shows:

- **What they are testing**, the signal the interviewer is looking for at your level.
- **A strong answer, said out loud**, written the way a person speaks, with `[bracketed placeholders]` wherever the answer needs a
  detail only you know (a number, a name, a decision). Nothing is invented on your behalf.
- **The shape of it**, the answer's structure as a short chain of steps.
- **Where they'll go next**, likely follow-up questions; click one to see how to answer it.
- **A word count and speaking time**, next to the range interviewers typically expect for that kind of question.

Things that make it more than a question generator:

| Feature | What it gives you |
|---|---|
| **Profiles** | One per job you are preparing for. Resume and job description can be pasted or loaded from PDF, DOCX, TXT or MD. |
| **Question types** (eight per interview) | Tell me about yourself, resume deep-dive, technical concept, system design, coding talk-through, behavioral, scenario-based, plus one of *Motivation and fit* (full-time) or *Availability and engagement* (contract). |
| **Full-time or contract** | The two interview styles differ, so the role type changes which questions are offered and how they are written and coached. |
| **By technology** | Reads the technologies out of the job description (or lets you type your own under **Other**) and asks questions about the ones you pick. |
| **Technology concepts** | A **Concepts** page for drilling one technology at a time, with no resume or job description: type a job role, tick the technologies it uses (found once by the model and saved), or type your own, then pick **Beginner**, **Medium** or **Advanced**. Works in Learn and Practice. |
| **Saved technical questions** | Technical-concept questions and their general answers are saved per technology and seniority, so repeats cost nothing and survive edits to your resume. When a technology runs out of saved questions the model writes ten common ones in a single call (most common first, spread over different areas, mixed phrasing) instead of one at a time. |
| **Questions spread across your resume** | Resume questions take turns between your employers and projects (the least used first, then a different highlight each time) instead of always asking about the current job, and each starts with a different word. A question that repeats an earlier one in other words is sent back once. |
| **Mock Interview** | A planned round (recruiter screen, technical, system design, behavioral, hiring manager or mixed; 15 to 60 minutes) with a live interviewer who speaks, listens, follows up on what you say and closes at the end. No feedback until it is over, then a **debrief**: hire signal, ratings per focus area with evidence, strengths, top fixes, and the coach's view of every question, exportable to Markdown. |
| **Practice (typed)** | Answer a question yourself, with a live word count and an answer timer, then get feedback that quotes your own words, what the interviewer is testing, a strong answer and a delivery comment. Try again passes your last answer to the coach so it can say what changed; follow-ups become the next question. |
| **Library (Revisit)** | Every question you learn and every answer you practise is kept, so you can go back to it: filter by Learned or Practised and by question type, search (including your own words), sort, reopen the full answer with your feedback, remove an entry. |
| **Tailor to my resume** | Turn any general answer into one built from your own experience, on demand. |
| **Answer length control** | Short, Medium, Long or a custom word count. By default the model aims for the same range the screen shows. |
| **Interviewer-style phrasing** | Technical questions are short and direct ("What's the difference between checked and unchecked exceptions?"), not essay prompts. |
| **Three providers** | Anthropic (default), any OpenAI-compatible endpoint, or **OpenRouter**: one key for hundreds of models, with an in-app model browser showing prices. |
| **Demo mode** | Runs the whole UI on canned data with no keys and no network. |
| **Light and dark** | Follows the Windows theme. |

## Screenshots

Rendered from the real views by the repository's own snapshot test, in Demo mode (so the text is sample data, not model output).

**Home: profile, resume, and the Start card with By technology and Other**

| Light | Dark |
|---|---|
| <img src="docs/images/home-light.png" alt="Home, light"> | <img src="docs/images/home-dark.png" alt="Home, dark"> |

**Learn mode: a general technical answer with placeholders, shape, and follow-ups**

| Light | Dark |
|---|---|
| <img src="docs/images/learn-light.png" alt="Learn, light"> | <img src="docs/images/learn-dark.png" alt="Learn, dark"> |

**Practice: type your answer, then read feedback that quotes it**

| Answering (light) | Feedback (dark) |
|---|---|
| <img src="docs/images/practice-answer-light.png" alt="Practice, answering"> | <img src="docs/images/practice-feedback-dark.png" alt="Practice, feedback"> |

With a microphone: **Speak** (or F2) dictates into the box, the words being heard show in grey, **Repeat** says the question again.

<img src="docs/images/practice-listening-light.png" alt="Practice, listening" width="560">

**Library: go back to every question and answer you have seen**

| Light | Dark |
|---|---|
| <img src="docs/images/library-light.png" alt="Library, light"> | <img src="docs/images/library-dark.png" alt="Library, dark"> |

**Settings** (Anthropic, and OpenRouter with the model browser)

<img src="docs/images/settings-light.png" alt="Settings, light" width="400"> <img src="docs/images/settings-openrouter-dark.png" alt="Settings with OpenRouter, dark" width="400">

To regenerate them, see [Testing](#testing).

## Status and roadmap

| Milestone | State |
|---|---|
| 1. Skeleton: solution, DI host, encrypted settings, Demo mode, prompt files, LLM service, Test connection | Done |
| 2. Profiles: create, edit, delete; paste or load JD and resume; SQLite with migrations | Done |
| 3. **Learn mode**: question generator, coach, coach cards, follow-ups | Done |
| 4. **Practice (typed)**: answer first, then get feedback; try again with comparison; follow-ups | Done |
| 5. **Voice**: dictate answers, hear questions, read answers aloud (Azure, OpenAI, Windows voices) | Done (the real services are untested against live keys; see HANDOFF section 22) |
| 6. **Mock Interview**: planned round, live interviewer who speaks and listens, time pacing, debrief, Markdown export | Done (the voice parts are untested against live services; see HANDOFF section 24) |
| 7. **History and polish**: History of mock interviews, resuming a round you left, a bar for problems and a missing key, keyboard shortcuts | Done (what you learned and practised is in the Library) |

Beyond the original spec, these were added during the build: scenario questions, answer-length control, technology bank,
By technology and Other, prompt caching and prefetching, thinking-effort setting, full-time or contract role type, an OpenRouter provider with a model browser, a new icon and
a full visual redesign. [SPEC.md](SPEC.md) section 14 lists every change and **overrides the spec where they differ**.

## Getting started

### Requirements

- Windows 10 (version 2004 or later) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An [Anthropic API key](https://console.anthropic.com/) (or an [OpenRouter](https://openrouter.ai/) key, or an OpenAI-compatible endpoint and key). Not needed for Demo mode.

### Run

```bash
git clone <repository-url>
cd interview-coach
dotnet run --project src/InterviewCoach.App
```

Or open `InterviewCoach.sln` in Visual Studio and run `InterviewCoach.App`.

### First run

1. Open **Settings**. Paste your API key (or set `ANTHROPIC_API_KEY` in your environment) and click **Test connection**.
   No key yet? Tick **Demo mode** (at the bottom of Settings) to try everything with sample data.
2. For a good cost/quality mix, set the **Question generator** model to Haiku, keep the **Coach** on Sonnet, and set
   **Thinking effort** to **Low**. See [Speed and cost](#speed-and-cost).
3. Go to **Home**, click **New**, fill in role, seniority, job description and resume, and **Save**.
4. Choose **Learn**, pick a role type and question types, and **Start**.

### Optional: use OpenRouter instead of Anthropic

OpenRouter gives you **one key for many models** and you choose the model yourself, which makes it easy to try cheaper ones.

1. Create a key at [openrouter.ai/keys](https://openrouter.ai/keys) and add credit.
2. In **Settings**, set **Provider** to **OpenRouter** and paste the key (or set `OPENROUTER_API_KEY`).
3. Click **Use this setup** under **Recommended** (see below), or choose models yourself: under **Choose a model**, click **Load models**. Search by name (for example `claude`, `gemini flash`, `deepseek`), tick
   **Cheapest first** if you like, select a model, and click **Question generator**, **Coach** or **Every role**.
   Each row shows the model name, its id, the price per million tokens and the context size.
4. Click **Test connection**, then **Save**.

OpenRouter model ids look like `anthropic/claude-sonnet-5.5`, so OpenRouter keeps its **own set of models**, separate from the
Anthropic ones; switching provider swaps the boxes and never loses either set. **Thinking effort** is sent to OpenRouter in its
own `reasoning` field. **Prompt caching** is not used through OpenRouter.

**Recommended setups.** Settings shows ready-made choices for each provider; each row is one role with its model (and, for OpenRouter,
the live price once you click **Load models**), and **Use this setup** fills in the boxes and sets Thinking effort to Low. Nothing is
saved until **Save**.

| Role | Recommended | Why |
|---|---|---|
| Question generator | `claude-haiku-4-5-20251001` (OpenRouter: `anthropic/claude-haiku-4.5`) | One short question per call, so a small, fast model is enough |
| Coach | `claude-sonnet-5-5` (OpenRouter: `anthropic/claude-sonnet-5.5`) | Writes the answer you practise from, so quality matters most |
| Planner, Debrief | the strong model | Used by Mock Interview: the plan of the round and the debrief |
| Interviewer | the fast model | Speaks live in Mock Interview, so speed matters: it answers after every answer you give |

These are the models the prompts were written and measured against. For OpenRouter there is also a **Lower cost** setup:
Haiku still writes the questions and `openai/gpt-5-mini` does the coaching. It was tried on seven C# questions (5 to 9 seconds per
answer, about as fast as Sonnet at Low, for roughly a fifth of the cost), so read a few answers yourself before relying on it. A first
version of this setup used `google/gemini-3.5-flash-lite` for questions; the debug log showed it was slower and dearer there (it spent
about 500 of 550 output tokens thinking), so it was dropped.

Why Low thinking effort matters: on OpenRouter, Claude Sonnet 5.5 always thinks and defaults to *high* effort. In earlier
measurements this app's coach call took about 21 seconds at the default and about 10 at Low.

<img src="docs/images/settings-openrouter-light.png" alt="Settings with OpenRouter selected, light" width="560">

### Optional: speech keys

Voice is optional: Practice works fully by typing. **Hear questions** needs nothing (the Windows voice is the default). **Dictation** needs an Azure Speech key and region (live transcript) or an OpenAI key (records, then transcribes), set in Settings, Speech, where **Load voices**, **Test voice** and **Test microphone** check them (they use the saved settings, so click Save first). In Practice, **Speak** or **F2** starts and stops the microphone, the words go into the answer box at the cursor so you can type and talk in the same box, **Repeat** says the question again, and **Read aloud** on the model answer reads it. With an Azure for Students
subscription, some regions are blocked by policy; if creating a Speech resource fails with `RequestDisallowedByAzure`, pick one of the
regions your subscription allows (the error lists them).

## Using the app

### Profiles

- A profile is a job you are preparing for: name, role, seniority, job description, resume.
- **Start** is disabled until the profile is saved and has a role, a job description and a resume, because a session uses the
  saved copy. Switching profile or clicking New with unsaved edits asks first.
- Loading a file over existing text asks first. Scanned PDFs with no text, old `.doc` files, corrupt files and files over 20 MB
  produce a plain message instead of a crash. Text over 40,000 characters shows a warning and a **Trim** button.

### Role type

**Full-time** adds *Motivation and fit* questions (why this company and role, where you want to grow, how you like to work) and a
broader loop: fundamentals, design and trade-offs, ownership, culture fit. **Contract** adds *Availability and engagement* questions
(start date, notice, rate, contract length, location, how you would be productive in the first week) and focuses on hands-on depth in the
exact stack, speed to productivity and delivering to a deadline. Engagement answers are kept short and direct, and the coach never
invents availability or rates; it uses placeholders such as `[your earliest start date]`. The choice is remembered between runs.

### Question types

Tick any combination, or **Any**. Each ticked type counts as one share, so with three types ticked each is picked about one time in three.
**Technical concept** and **By technology** together count as one share.

### By technology

Choose **By technology** to see the technologies found in the job description (one cheap call per distinct job description,
remembered), tick the ones you want, or tick **Other** and type names yourself (comma, semicolon or newline separated, up to eight).
Questions are then about those technologies. On its own, By technology serves only technology questions; if one cannot be produced
you see the error with **Retry** rather than a silent substitute.

### Technology concepts (Concepts page)

For practising a technology by its concepts rather than by your resume. Open **Concepts** in the sidebar:

1. **Job role.** Type a role (it starts as your selected profile's role, and the last one you used is remembered) and click **Show technologies**. The model lists the main technologies that role uses, **once**: the list is saved in the database against the role (spelling and spacing of the role do not matter), so the same role is instant next time, after a restart, with no model call. **Ask again** replaces the saved list if it looks wrong or old. Opening the page only ever reads the saved list; the model is called only when you click.
2. **Technologies.** Tick the ones you want (**Select all** and **Clear** help), and/or tick **Other** and type names yourself (comma, semicolon or newline separated). Typing works with no role at all.
3. **Difficulty.** **Beginner** (fundamentals), **Medium** (working knowledge, trade-offs) or **Advanced** (internals, performance, failure cases). It is the level the saved questions are kept at: Beginner is Junior, Medium is Mid and Advanced is Senior, so a question saved for a technology at one difficulty is reused at that difficulty and never at another.
4. **How to practise.** **Learn** (see the question and how to answer it) or **Practice** (answer first, then get feedback), and the answer length. **Back to Home** in the session returns to Concepts.

Every question is a technical concept question about one of the ticked technologies, taken from the saved question bank (ten common ones are written in one call when a technology and difficulty run out) with a general answer. No resume or job description is sent anywhere, and there is no **Tailor to my resume** button because there is nothing to tailor to. These sessions need the saved question bank (Settings, Saved technical questions); if it is switched off the page says so. Clearing the saved questions in Settings keeps the saved role lists.

<img src="docs/images/concepts-light.png" alt="Concepts page" width="560">

### Library (Revisit)

Every question and answer Learn mode shows, and every answer you give in Practice with its feedback, is kept, so you can come back to it. Open it from the **Revisit** card next to Practice on Home,
or from **Library** in the sidebar.

- **Filter by kind** (once you have practised): All, Learned or Practised, with a count on each.
- **Filter by question type.** Chips for All and for each type you have seen, with a count on each.
- **Search** the question, the technology, the question type and the answer text (every word must match).
- **Sort** by newest, oldest, question (A to Z) or question type.
- **Open an entry** to read it again: what they are testing, the answer, its shape and the follow-ups. For a practice answer you also see what you wrote and the feedback on it (what worked, what to fix, what was missing, with your quoted words). A follow-up you opened
  before opens its own saved answer; one you never opened says so.
- **Remove from library** deletes an entry (it is added back if you see that question again).
- A learned question seen again is one entry (it moves to the top and counts how many times you saw it). Every practice answer is its own entry, so you can see how a retry improved. A general answer and one tailored to
  your resume are separate entries.
- Questions you already had saved as technical questions are carried over the first time the new version starts, so the library is not empty.
  Anything seen before that and not saved cannot be recovered.
- It is stored locally in `app.db` (table `LearnHistory`). In Demo mode it is kept in memory only.

### Practice

Choose **Practice** on Home (the same role type, question types, technologies and answer length apply), then **Start Practice**. You can also
click **Try it myself** in Learn mode to practise the question you are looking at, without its model answer.

1. A question appears and the answer box opens. **Nothing is coached until you submit**: the app enforces that in the engine, not just on screen.
2. Type your answer. The box shows a live word count and a timer that starts at your first keystroke (for your own pacing: the coach is not told the
   typing time, because it is not speaking time). For behavioral and project questions the
   timer turns amber past 2:30 and red past 3:30. **Ctrl+Enter** or **Submit** sends it; an empty answer is not sent ("I don't know" is fine).
3. The coach replies with **how your answer landed** (what worked, what to fix, what was missing, quoting your words), what the interviewer was testing,
   a strong answer, its shape, a delivery comment and likely follow-ups.
4. Then **Try again** (same question; your last answer is passed to the coach, so the feedback starts with what changed; **Start from my last answer**
   puts it back in the box), click a **follow-up** to answer it (the coach sees the earlier questions and answers), or **Next question**.
5. If the coach call fails, your answer stays on screen: **Retry** sends it again, or **Edit my answer** reopens the box.

Every answer you submit is kept in the Library with its feedback (see below), including each try and each follow-up.

### Mock Interview

Choose **Mock Interview** on Home, then the **round**, its **length** (15, 30, 45 or 60 minutes) and whether to **show the interviewer's words** (turn it off for audio only; it is shown anyway when no voice works). The last round and length are remembered. Start needs a saved, complete profile.

1. The app **plans the round** from your resume and the job description (focus areas, resume claims worth probing, phases with minutes, an opening line), then the interviewer speaks first.
2. A **status pill** says who has the floor: Speaking, Thinking, Your turn or Listening, next to the clock (elapsed / length). Answer by typing or by voice: **Speak** or **F2** dictates into the box, and **talking over the interviewer** stops them (barge-in). With auto-listen on, the microphone opens by itself when they finish. **Ctrl+Enter** or **Send** sends the answer; **Repeat** says the last line again. Silence auto-submit works as in Practice.
3. The interviewer reacts to what you actually said: usually one or two follow-ups per main question, a hint if you are stuck, your own questions near the end. Nothing here is graded or hinted; **no feedback of any kind appears until the round is over**.
4. The round ends when the interviewer closes it, when you click **End interview** (after a confirmation; no closing line is spoken), or when it is five minutes over its length (the interviewer is then told to wrap up). **Conversation so far** (collapsed) shows the dialogue.
5. The **debrief** opens as soon as it is written: how the round went with a hire-signal badge, ratings of 1 to 4 per focus area with what you said as evidence (or "Not covered"), strengths, top fixes with a drill each, and what to practise next. Below it is a card for every question you were asked, with the exchange and the coach's view of it (what they were testing, what landed, a strong answer, follow-ups). The cards fill in as their coaching finishes (three at a time); one that fails has **Try again**.
6. **Practise this question** opens a card's question in Practice (leaving Practice returns to the debrief), and a follow-up from the coaching opens as a follow-up. **Export to Markdown** saves the whole debrief. Interviews are kept in the database (`MockSessions`) as they go, and shown on the **History** screen (see below).

If the interviewer or the debrief fails, the screen says why and **Retry** repeats just that step; the conversation so far is kept. Opening another page during an interview ends its voice and microphone, but what was said is kept: the interview waits on History and Home to be picked up again.

<img src="docs/images/mock-interview-light.png" alt="Mock interview" width="460"> <img src="docs/images/debrief-light.png" alt="Debrief" width="460">

### History and resuming

**History** (sidebar, Ctrl+4) lists every mock interview, newest first: the role, the profile, the date, the round and length, the time spent, and its state.

- **Finished** (with the hire signal): **Open debrief** shows the stored debrief, with the coaching of each question and **Back to History**. Questions can be practised from it as from a fresh debrief, as long as the profile still exists (it is used as saved now).
- **Unfinished** (you left the round midway): **Resume** carries on where it stopped. The interviewer says the line it was on again (or answers the last thing you said, if its reply never arrived), the whole earlier conversation goes back to the model as it was, and the clock continues from where it was left: the time away does not count.
- **No debrief yet** (the round ended but the debrief failed, or the app was closed): **Write the debrief** writes it now, and the questions are coached again.
- The trash button deletes an interview after a confirmation. **Open the Library** goes to the questions you learned and the answers you practised.

Home shows the newest unfinished round (or one without a debrief) as a notice with **Resume** and **Discard**. A round whose profile has been deleted cannot be resumed, and says so. The interview is written after every turn, so even a crash loses at most the last answer.

<img src="docs/images/history-light.png" alt="History" width="460"> <img src="docs/images/home-resume-light.png" alt="Home with a round to resume" width="460">

### Notices and keyboard shortcuts

A bar above the page tells you when there is something to know: **no key yet** for the selected provider (with **Open Settings**; it is not shown on Settings itself, in Demo mode, or for an OpenAI-compatible server with an address), or **a problem nobody expected**, shown there instead of a dialog so the work on screen is not lost. Both can be dismissed.

<img src="docs/images/no-key-light.png" alt="The no-key bar" width="560">

| Keys | Does |
|---|---|
| Ctrl+1 to Ctrl+5 | Home, Concepts, Library, History, Settings |
| F2 | Start or stop the microphone (Practice, Mock Interview) |
| Ctrl+Enter | Send your answer |
| Ctrl+N | Next question (Learn, Practice) |
| Ctrl+R | Say the question or the interviewer's last line again (Practice, Mock Interview) |
| F1 / Esc | Show / close the list of shortcuts |

### In a session

- **Next question** moves on; the following question is prepared in the background, so it usually appears instantly.
- **Back** returns to the main question after you open a follow-up.
- **Tailor to my resume** rewrites a general answer from your own experience.
- A general answer shows a note and highlighted placeholders. Replace them with your real numbers and names, or leave the claim out.

## How it works

```mermaid
flowchart LR
    subgraph UI [WPF app]
        H[Home view model] -->|LearnSessionRequest| L[Learn view model]
    end
    L --> E[LearnEngine]
    E -->|technology share| B[TechBank]
    E -->|model share| Q
    B -->|saved question + general answer| R[(SQLite)]
    B -->|bank miss| Q
    Q[ILlmService] --> P[PromptRenderer + prompt files]
    Q --> C[IChatClient<br/>Anthropic, OpenAI-compatible or OpenRouter]
    E -->|coach call| Q
```

**A question's journey.** Home builds a request (saved profile, ticked types, answer length, technologies, role type). The engine decides
whether this question comes from the **technology source** or the **model source**:

- *Technology source:* a saved general technical-concept question for the chosen technology and seniority, or one the model writes if
  every saved one has been seen this session. Its answer is a **general answer**, written without your resume or job description,
  saved, and reused at no token cost.
- *Model source:* the model writes the question from your resume and job description, and the coach answers it from the same.

Every call takes an operation id and a cancellation token; a result that arrives after you moved on is discarded. When a main
question is ready, the engine prepares the next one in the background.

**Structured output.** The model replies in JSON. The parser takes the first balanced JSON value (strings and escapes respected, so
a stray brace or commentary after the JSON is ignored) and makes **one** repair call if the reply still is not valid.

## Architecture

```
InterviewCoach.sln
  src/InterviewCoach.Core             net10.0                       engines, models, interfaces, prompt rendering. No UI, no SDKs.
  src/InterviewCoach.Infrastructure   net10.0-windows10.0.19041.0   LLM, settings, SQLite, documents, fakes. References Core.
  src/InterviewCoach.App              net10.0-windows10.0.19041.0   WPF, MVVM, Generic Host DI. References both.
  tests/                              xUnit: Core, Infrastructure, App (real views)
  tools/IconGenerator                 draws the app icon from vector shapes (not in the solution)
```

| Layer | Responsibility | Notable pieces |
|---|---|---|
| **Core** | The rules of the product, independent of any UI or vendor | `LearnEngine` (state machine, shares, prefetch), `TechBank`, `PromptRenderer`, `AnswerLength`, `QuestionTypes`, `EmploymentType` |
| **Infrastructure** | Talking to the outside world | `LlmService` on `Microsoft.Extensions.AI`'s `IChatClient`, `ChatClientFactory`, `JsonResponseParser`, `SettingsStore` (DPAPI), EF Core + SQLite repositories, `ResumeTextExtractor` (PdfPig, OpenXml), `PromptLibrary` |
| **App** | The Windows UI | MVVM with CommunityToolkit.Mvvm, `HomeViewModel`, `LearnViewModel`, `SettingsViewModel`, the design system in `App.xaml`, controls for placeholder highlighting, bindable password box, and page-first mouse-wheel scrolling |

Design choices worth knowing (the full list with reasons is in [`docs/HANDOFF.md`](docs/HANDOFF.md) section 7):

- **Core has no SDK or UI references**, so WPF or Anthropic could be swapped out without touching the engines.
- **Demo mode is a pair of decorators** (`RoutingLlmService`, `RoutingTechBankRepository`) that swap in fakes, so the real
  database and API are never touched.
- **Views are recreated on every navigation**, so view state lives in view models. For the same reason the app avoids
  `RadioButton` groups, whose state is shared between view instances.
- **No `ConfigureAwait(false)` in Core**, on purpose: engine events must arrive on the UI thread.
- **Theme colours are Fluent `DynamicResource` keys, never hard-coded**, so dark mode is correct everywhere.

## Prompts

Prompts are Markdown files in [`src/InterviewCoach.App/Prompts/`](src/InterviewCoach.App/Prompts), copied next to the executable at
build time and also embedded in Infrastructure as a fallback. You can edit the copy next to the exe to tweak wording without a rebuild.

| File | Used by | Purpose |
|---|---|---|
| `question_generator.md` | Learn; the bank when it writes a question | Writes one question; per-type length rules; role-type rule; optional focus technology |
| `coach.md` | Learn, Practice and the debrief of Mock Interview | Writes the answer, the shape, and the follow-ups; in Practice also feedback on your answer |
| `tech_tags.md` | By technology | Extracts up to eight technologies from a job description |
| `role_technologies.md` | Concepts page | Lists the main technologies a job role uses (up to 24), once per role, saved |
| `question_batch.md` | The technology bank | Writes ten common questions about one technology in one call, for the bank |
| `resume_topics.md` | Resume questions | Lists the employers and projects on a resume with what was done on each, once per resume, so questions can be spread over them |
| `planner.md` | Mock Interview | Plans the round: focus areas, resume claims to probe, phases with minutes, the opening line |
| `interviewer.md` | Mock Interview | The live interviewer: one short spoken turn at a time, reacting to what you said, pacing itself against the plan |
| `debrief.md` | Mock Interview | The honest debrief after the round: summary, hire signal, ratings with evidence, fixes, what to practise |

`PromptRenderer` fills `{{VARIABLES}}` in a single pass, throws on a missing key, and renders a blank value as `(none)`.

**The prompts are cache-sensitive.** In `coach.md` the text before the `=== THE CANDIDATE AND THE QUESTION ===` header contains no
variables and the header appears exactly once; anything that varies per call comes after `</candidate_resume>`. Tests guard both.
Read [`docs/HANDOFF.md`](docs/HANDOFF.md) section 4 before editing a prompt.

## Speed and cost

The first version spent about **$0.067 and 20+ seconds per question**. These changes brought a warm saved question to about
**$0.017 and a reused one to about $0**, and answers from about 21 s to about 9 s:

| Lever | Effect |
|---|---|
| **Thinking effort** (Model default, Low, Medium, High) | Sonnet's default thinking produced about 2,400 output tokens for about 760 tokens of visible text. At *Low* it is about 1,000 tokens and 9-11 s. Sent for Anthropic non-Haiku models only. |
| **Prompt caching** (on by default) | The system prompt is split into cached blocks: the coach's fixed guidance, then the job description and resume. Repeat calls read them at about a tenth of the input price. |
| **Technology bank** | General answers need no resume tokens and are saved per technology and seniority, so repeats are free and edits to your resume or job description do not invalidate them. |
| **Prefetch** | The next question is prepared while you read the current one. |
| **Answer length** | Shorter requested answers mean fewer output tokens, which are most of the cost. |
| **Haiku for the question generator** | A good low-cost choice for short replies. |

Measurements came from the app's own debug log (Settings, **Debug logging**), which records seconds, tokens, cache reads and writes
for each call. Prices in the estimates are assumptions (Sonnet $3 in / $15 out, Haiku $1 in / $5 out per million tokens, cache read
about 0.1x, cache write about 1.25x). Haiku was not faster than Sonnet in practice: it wrote more and once returned invalid JSON.
Details are in [`docs/HANDOFF.md`](docs/HANDOFF.md) section 8.

### Answers start with the point

Spoken answers never open with a warm-up such as "Sure. Short version:" or "Yeah, so". The coach prompt tells the model to start with the
substance, and as a safety net the app also strips such an opening from any answer it shows, including answers saved earlier.

## Data, settings and privacy

Everything is stored locally under `%LOCALAPPDATA%\InterviewCoach\`:

| Path | Contents |
|---|---|
| `settings.json` | Settings. API and speech keys are encrypted with **DPAPI** for the current Windows user (`dpapi:<base64>`); a test guards against plaintext keys. A corrupt file falls back to defaults. |
| `app.db` | SQLite (WAL mode): profiles, saved technical questions and answers, remembered technologies per job description and per job role. Migrations apply at startup. |
| `logs\llm-yyyymmdd.log` | Only when **Debug logging** is on. Contains full prompts and replies, **including your resume and job description**. Turn it off afterwards. |

**What leaves your machine:** your resume, job description, and the question text go to the LLM provider you configure, and nothing
else. With OpenRouter they go to OpenRouter and on to whichever company runs the model you picked, so check that model's data policy
on its OpenRouter page if that matters to you. The model list for the OpenRouter browser is a public download that carries nothing about you. There is no telemetry. In Demo mode nothing is sent anywhere.

**Key fallbacks** when a Settings field is empty: `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `OPENROUTER_API_KEY`, `AZURE_SPEECH_KEY`, `AZURE_SPEECH_REGION`.

**Database tables:** `Profiles`, `TechQuestions`, `TechAnswers`, `JdTechnologies`, `RoleTechnologies` (the technologies of a job role, fetched once), `ResumeTopics` (employers and projects read from a resume), `LearnHistory` and `PracticeAttempts` (the Library), `MockSessions` (mock interviews, written as they go: plan, conversation with the interview's own clock, debrief and the coaching of each question as JSON, the profile and role type).

## Testing

```bash
dotnet test                                     # all 1040 tests
dotnet test tests/InterviewCoach.Core.Tests     # one project
dotnet build InterviewCoach.sln -c Release      # use this if the Debug exe is running (it locks its DLLs)
```

| Project | Tests | Covers |
|---|---|---|
| `InterviewCoach.Core.Tests` | 437 | Prompt rendering, engine behaviour, the bank, answer lengths, question types, text helpers, role type |
| `InterviewCoach.Infrastructure.Tests` | 218 | JSON parsing, LLM service (retry, cache split, options), settings (DPAPI round trip, no plaintext), SQLite repositories on real files, file extractors, prompt guards, Demo-mode isolation |
| `InterviewCoach.App.Tests` | 385 | View models and **real WPF views** on a shared STA dispatcher; fails on any binding error |

Test names are sentences that describe behaviour. Scripted test doubles (`ScriptedLlmService`, `BankScript`) let tests control exactly
what the model "says", including delays and failures.

**Regenerate the screenshots** (both themes, every screen):

```bash
ICOACH_SNAPSHOTS=docs/images dotnet test tests/InterviewCoach.App.Tests --filter VisualSnapshots
```

With the variable unset the test does nothing.

## Repository layout

```
.
├── README.md                this file
├── SPEC.md                  original build spec; section 14 lists later changes and overrides it
├── CLAUDE.md                briefing for AI-assisted development in this repo
├── InterviewCoach.sln
├── docs/
│   ├── HANDOFF.md           status, architecture, decisions, measurements, traps, next steps
│   └── images/              README screenshots
├── src/
│   ├── InterviewCoach.Core/
│   ├── InterviewCoach.Infrastructure/
│   └── InterviewCoach.App/  (Prompts/, Views/, ViewModels/, Controls/, Assets/)
├── tests/
└── tools/IconGenerator/
```

## Development guide

- **Tests must pass**, and behaviour changes come with tests.
- **Migrations:** from `src/InterviewCoach.Infrastructure` run
  `dotnet ef migrations add <Name> --startup-project ../InterviewCoach.App --output-dir Persistence/Migrations`
  (the App project is the startup project because `dotnet ef` cannot load the Windows-targeted class library alone).
- **Icon:** `dotnet run --project tools/IconGenerator src/InterviewCoach.App/Assets`, then rebuild.
- **Styles** on themed controls need `BasedOn`; custom control templates need an explicit `Foreground`. A missing `DynamicResource` key
  fails silently, so probe keys in a test.
- **Keep user-visible wording plain**: no milestones or internals on screen.
- **Update** `SPEC.md` section 14 and `docs/HANDOFF.md` when behaviour or status changes.

## Documentation

| Document | What is in it |
|---|---|
| [`SPEC.md`](SPEC.md) | The original specification, plus section 14, "Changes made after the first build" |
| [`docs/HANDOFF.md`](docs/HANDOFF.md) | Current status, architecture, LLM layer and prompts, data, behaviour reference, 18 design decisions, measurements, traps and lessons, tests, open items |
| [`CLAUDE.md`](CLAUDE.md) | Short briefing and working rules for AI-assisted development |

A fuller set (high-level and low-level design, data model, prompt catalogue, cost model, security, test strategy, operations guide, ADRs
and a user guide) is planned; the plan is in `docs/HANDOFF.md` section 13.

## Commit conventions

History is written to be read. Each commit is one coherent change, with a subject in the imperative mood (about 60 characters, no
trailing period) and a body that says **what** changed and **why**, including measurements where there are any.

```
<area>: <what changed>

<why it was needed and how it works; numbers if measured>
```

Areas used so far: `build`, `core`, `infra`, `app`, `prompts`, `data`, `design`, `test`, `docs`.

## License

No license has been chosen yet; until one is added, all rights are reserved by the author.
