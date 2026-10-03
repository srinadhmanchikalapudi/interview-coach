# Interview Coach: Build Spec

A standalone Windows desktop app for practicing job interviews. The user supplies a job description, a resume, a job role and a seniority level. An LLM then plays the interviewer and coaches the answers.

> **For Claude Code:** read this whole file before writing any code.
> - Build in the milestone order in §12. Stop after each milestone so the user can run it.
> - The prompts in §8 are part of the spec. Save them verbatim as files under `src/InterviewCoach.App/Prompts/`.
> - Where this spec names a NuGet package or API, verify the current version and API shape before using it. Don't guess signatures.

---

## 1. What the app does

Three modes.

| Mode | Name | Flow | Voice |
|---|---|---|---|
| 1 | **Mock Interview** | A realistic full interview. The interviewer asks questions and follow-ups, and the user answers. No coaching appears during the round. When the round ends, a debrief gives an overall assessment plus coaching on every question. | Yes: questions are spoken aloud, and the user can speak or type answers |
| 2 | **Learn** | The app asks a question and immediately shows how to answer it: what's being tested, a model answer written as speech, the answer's structure, and likely follow-ups. | Not required. Includes an optional "Read model answer aloud" button |
| 3 | **Practice** | The app asks a question and waits for the user's answer. Only after the user submits does it show feedback on that answer plus a model answer. The user can retry, answer a follow-up, or move to the next question. | Yes: questions are spoken aloud, and the user can speak or type answers |

**The core rule for Modes 1 and 3:** the user never sees a model answer, a hint about the answer, or feedback until they have submitted their own answer. The app enforces this by calling the Coach prompt only after submission. The Interviewer and Question Generator prompts never produce answers.

### Non-goals for v1
- No server or backend. The app calls the LLM and speech APIs directly using the user's own API keys.
- No user accounts or cloud sync.
- No video or avatar.

---

## 2. Tech decisions

| Area | Choice | Notes |
|---|---|---|
| Runtime | **.NET 10** (LTS) | TFM `net10.0-windows10.0.19041.0`, so WinRT speech is available as a fallback |
| UI | **WPF** with MVVM (`CommunityToolkit.Mvvm`) | Windows-only. If the user later wants Mac support, port the UI to Avalonia. Core and Infrastructure stay UI-agnostic, so they don't change. |
| DI / config / logging | `Microsoft.Extensions.Hosting` (Generic Host inside WPF) | |
| LLM abstraction | `Microsoft.Extensions.AI`, using the `IChatClient` interface | Lets the provider be swapped without touching app logic |
| LLM provider (default) | **Anthropic**, official `Anthropic` NuGet package (v10+). Create the client with `client.AsIChatClient(modelId)` | Default model: `claude-sonnet-5-5`. The model is configurable for each role (see §7). |
| LLM provider (alt) | OpenAI-compatible endpoint, via `Microsoft.Extensions.AI.OpenAI` | Covers OpenAI, Azure OpenAI, and local Ollama/LM Studio through a base URL |
| Speech to text (default) | **Azure AI Speech**, `Microsoft.CognitiveServices.Speech` | Real-time streaming with live partial transcripts |
| Speech to text (alt) | OpenAI transcription API (record, then transcribe) | Captures audio with `NAudio` |
| Text to speech (default) | Azure AI Speech neural voices | |
| Text to speech (alt) | OpenAI speech API, and offline `Windows.Media.SpeechSynthesis` | The offline voice is a no-key fallback |
| Resume/JD files | `UglyToad.PdfPig` for PDF, `DocumentFormat.OpenXml` for DOCX, plain read for TXT/MD | Paste is also supported |
| Storage | SQLite via `Microsoft.EntityFrameworkCore.Sqlite` | Stored in `%LOCALAPPDATA%\InterviewCoach\app.db` |
| Secrets | API keys encrypted with Windows DPAPI (`System.Security.Cryptography.ProtectedData`, CurrentUser scope) and stored in `settings.json` in the same folder | Falls back to the env vars `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `AZURE_SPEECH_KEY`, `AZURE_SPEECH_REGION` |
| Tests | xUnit | |

---

## 3. Solution layout

```
InterviewCoach.sln
src/
  InterviewCoach.Core/            // no UI, no SDKs: domain, engines, interfaces, prompt rendering
    Models/                       // CandidateProfile, Session, Turn, PracticeItem, Attempt, DTOs for LLM JSON
    Abstractions/                 // ILlmService, ISpeechToText, ITextToSpeech, IPromptLibrary, IClock, repositories
    Engines/                      // MockInterviewEngine, LearnEngine, PracticeEngine
    Prompts/                      // PromptRenderer, PromptName enum
  InterviewCoach.Infrastructure/  // SDK-backed implementations
    Llm/                          // LlmService (IChatClient wrapper), provider factory, JSON parsing + retry
    Speech/                       // AzureSpeechToText, AzureTextToSpeech, OpenAiSpeechToText, OpenAiTextToSpeech, WindowsTextToSpeech
    Documents/                    // ResumeTextExtractor (PDF/DOCX/TXT)
    Persistence/                  // AppDbContext, repositories, migrations
    Settings/                     // SettingsStore with DPAPI
    Fakes/                        // FakeLlmService, FakeSpeechToText, FakeTextToSpeech ("Demo mode")
  InterviewCoach.App/             // WPF
    Prompts/*.md                  // the prompt files from §8, CopyToOutputDirectory=PreserveNewest
    Views/, ViewModels/, Controls/, Converters/
tests/
  InterviewCoach.Core.Tests/
  InterviewCoach.Infrastructure.Tests/
```

Prompts are loaded from disk at runtime, so the user can tweak them without recompiling. When a file is missing, fall back to an embedded-resource copy.

---

## 4. Screens

### 4.1 Home / Setup
- **Profile panel.** Create, select, edit or delete a profile with these fields:
  - name
  - job role (free text, e.g. "Senior Backend Engineer")
  - seniority (Junior / Mid / Senior / Staff)
  - job description (paste box, plus a "Load file" button)
  - resume (paste box, plus a "Load file" button that extracts text and shows it for review)
- **Mode picker.** Three large cards: Mock Interview, Learn, Practice.
- **Mode options:**
  - Mock: round type (Recruiter screen / Technical / System design / Behavioral / Hiring manager / Mixed), duration (15 / 30 / 45 / 60 min), "Show question text" toggle (default on; turn it off for audio-only realism).
  - Learn and Practice: question type filter (Any / Tell me about yourself / Resume deep-dive / Technical concept / System design / Coding talk-through / Behavioral). Multi-select is allowed.
- **Start** button. It's disabled until the profile has a role, a JD and a resume.

### 4.2 Mock Interview screen
- Top bar shows:
  - an interviewer status pill: **Speaking**, **Listening**, **Thinking**, or **Your turn**
  - elapsed and target time
  - an **End interview** button, which asks for confirmation
- Interviewer panel shows the current interviewer line, or "(audio only)" when question text is hidden. A **Repeat** button replays the last line through TTS.
- An answer composer (see §4.6).
- Conversation history is collapsed by default and expandable. It contains no feedback, only the dialogue.
- No feedback of any kind appears on this screen.

### 4.3 Debrief screen (end of Mock)
- An overall summary, a hire-signal badge, focus-area ratings (1–4) with evidence, top strengths, and top fixes.
- One expandable card per question thread (§6.1). Each card shows the full exchange and the Coach output (§4.7).
- Each card has a **Practice this question** button that opens it in Practice mode.
- **Export** writes the debrief to Markdown.

### 4.4 Learn screen
- The question, rendered large.
- The Coach output (§4.7) appears immediately below it.
- Buttons:
  - **Next question**
  - **Try it myself**, which opens the same question in Practice with the Coach output hidden
  - **Read answer aloud**, which sends the model answer to TTS
- Clicking a follow-up shows Coach output for that follow-up.

### 4.5 Practice screen
1. The question is spoken through TTS and shown. A **Repeat** button replays it.
2. The answer composer (§4.6) appears. Coach output is not rendered and not requested yet.
3. On Submit, the screen shows a "Thinking…" state while the Coach call runs, then the Coach output appears below the user's answer.
4. Buttons after coaching:
   - **Retry** (same question; the previous attempt is passed to the Coach so it can compare)
   - **Answer a follow-up** (pick one of the Coach's follow-ups; it becomes the next question)
   - **Next question**

### 4.6 Answer composer (shared by Mock and Practice)
- A multi-line text box that is always editable. Voice and typing go into the same box, so the user can dictate, fix a word, then keep talking.
- **Mic button** (toggle, plus hotkey **F2**):
  - Start: stop any TTS playback (barge-in), then begin speech-to-text.
  - Partial transcripts show in grey at the caret. Final segments are committed as normal text.
  - Stop: finalize the transcript.
- **Submit** button (plus **Ctrl+Enter**). It stops the mic if it's running, then submits.
- An **answer timer** starts at the first keystroke or the first mic activation and is shown live (e.g. `1:42`). It turns amber past 2:30 and red past 3:30 for behavioral and project questions.
- Live word count.
- **Auto-listen** setting (default on for Mock): start the mic automatically when the interviewer finishes speaking.
- **Silence auto-submit** setting (default **off**, because people pause to think). If enabled, it submits after N seconds of silence (default 6) following at least 10 words.
- Record which input methods were used for the answer: `voice`, `typed`, or `mixed`.
- Never lose the answer text. Keep it if an LLM call fails, and restore it when the user retries.

### 4.7 Coach output rendering
Render the Coach JSON (§8.4) as cards:
1. **What they're testing**, as one short paragraph.
2. **How your answer landed** (hidden in Learn). Each point has an icon for strength, fix or missing, and shows the quoted phrase from the user's answer.
3. **A strong answer, said out loud**. Large readable text, with a **Read aloud** button. Bracketed placeholders like `[your actual p99]` are highlighted, so the user knows to fill in real values.
4. **The shape of it**, as a single-line skeleton, styled like a breadcrumb.
5. **Delivery**, shown only when the answer was spoken or timed: a length/pace comment.
6. **Where they'll go next**: follow-up questions with hints. In Practice, each one is clickable to answer it.

### 4.8 History
A list of past sessions (mode, profile, date, duration). Opening one shows its debrief (Mock) or its question/attempt list (Learn/Practice).

### 4.9 Settings
- **LLM:**
  - provider (Anthropic / OpenAI-compatible)
  - API key
  - base URL (OpenAI-compatible only)
  - a model ID for each role: Planner, Interviewer, Question Generator, Coach, Debrief
  - a **Test connection** button
- **Speech:**
  - STT provider (Azure / OpenAI)
  - TTS provider (Azure / OpenAI / Windows offline)
  - keys and region
  - a voice dropdown populated from the provider's voice list
  - speaking rate
  - microphone device
  - a **Test mic** button and a **Test voice** button
- **Behavior:** auto-listen, silence auto-submit with its seconds value, and show-question-text default.
- **Demo mode** toggle: uses the fakes from Infrastructure/Fakes, so the UI can be run without keys.

---

## 5. Core abstractions (sketch; refine as needed)

```csharp
public enum LlmRole { Planner, Interviewer, QuestionGenerator, Coach, Debrief }

public interface ILlmService
{
    // Sends system prompt + messages, expects JSON matching T. Handles parsing + one repair retry.
    Task<T> GetJsonAsync<T>(LlmRole role, string systemPrompt, IReadOnlyList<ChatTurn> messages, CancellationToken ct);
}
public record ChatTurn(ChatTurnRole Role, string Content); // User | Assistant

public interface IPromptLibrary
{
    string Render(PromptName name, IReadOnlyDictionary<string, string> vars); // throws if any {{VAR}} left unresolved
}

public interface ISpeechToText : IAsyncDisposable
{
    bool SupportsPartials { get; }
    event EventHandler<string>? PartialRecognized;   // in-progress text (replace previous partial)
    event EventHandler<string>? FinalRecognized;     // committed segment (append)
    event EventHandler<string>? Error;
    Task StartAsync(CancellationToken ct);
    Task StopAsync(); // for record-then-transcribe providers, this is where FinalRecognized fires
}

public interface ITextToSpeech
{
    Task SpeakAsync(string text, CancellationToken ct); // completes when playback ends or is cancelled
    void Stop();
    Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct);
}
```

**Speech rules:**
- Never capture the mic while TTS is playing, to avoid echo. Starting the mic always calls `ITextToSpeech.Stop()` first.
- Azure STT:
  - continuous recognition with `EnableDictation()`, so punctuation is included
  - `Recognizing` maps to partials, `Recognized` to finals
  - use the default mic, or the device selected in settings
- OpenAI STT:
  - capture 16 kHz mono PCM with `NAudio` `WaveInEvent` into a WAV in memory
  - on `StopAsync`, send it to the transcription endpoint (model is configurable)
  - no partials; show a "Transcribing…" indicator instead
- Azure TTS: `SpeechSynthesizer` with the selected neural voice. Support cancellation via `StopSpeakingAsync`.
- Windows TTS: `Windows.Media.SpeechSynthesis.SpeechSynthesizer` into a `MediaPlayer`.

**LLM rules:**
- Wrap `IChatClient` and select the model per `LlmRole` from settings.
- Request JSON output:
  - Use the provider's structured-output support when it's available through `Microsoft.Extensions.AI`. Otherwise rely on the schema in the prompt.
  - When parsing, strip ```json fences and trim any leading or trailing prose before deserializing with `System.Text.Json` (case-insensitive, allowing trailing commas).
  - If parsing fails, retry once. Append the bad output as an assistant turn, plus a user turn saying: `That wasn't valid JSON matching the schema. Error: {message}. Reply with only the corrected JSON.`
- Timeouts: 30 s for the Interviewer, 90 s for Coach and Debrief. On network errors, show a non-blocking error with a **Retry** button.
- Log prompts and responses to `%LOCALAPPDATA%\InterviewCoach\logs\` only when "Debug logging" is enabled in settings. Off by default, because resumes are personal data.

---

## 6. Mode engines

Each engine is a state machine in Core. It has no UI references and exposes state plus events to its ViewModel. Unit-test every engine with the fakes.

### 6.1 Mock Interview engine

```
Setup → Planning → InterviewerThinking → InterviewerSpeaking → CandidateAnswering → InterviewerThinking → … → Ending → Debriefing → Done
```

1. **Planning.** Call the Planner (§8.1) and store `PlanJson` on the Session.
2. **Interviewer turns.** Call the Interviewer (§8.2):
   - The system prompt is `interviewer.md`, rendered with the profile, round type, duration and plan.
   - The message history alternates. Assistant turns are the raw JSON the interviewer returned earlier, which keeps the format stable. User turns are the candidate's answers.
   - The first user turn is `[app context] The candidate has joined the call. Elapsed 0 min of {{DURATION}}.`
   - Every later user turn is the candidate's answer text, followed by a final line: `[app context] Elapsed {m} min of {{DURATION}}. Answer took {s}s via {voice|typed|mixed}.`
3. Show `say`, then speak it with TTS. Persist the turn with `turn_type`, `phase` and `focus_area_id`.
4. **Candidate answer.** Wait for Submit, then persist the answer with its input method and duration.
5. **Ending.** The interview ends when any of these happens:
   - the interviewer returns `end_interview: true`; its closing line is spoken, then the round ends
   - the user clicks End interview
   - elapsed time reaches duration + 5 min; the app sends `[app context] Time is up. Wrap up now.` and treats the next interviewer turn as the closing
6. **Debriefing.**
   - **Build question threads.** Each `main_question` turn starts a thread. Following `follow_up`, `hint` and `clarification` turns, and all candidate answers, belong to it until the next `main_question`. Smalltalk, closing and candidate-question turns are excluded.
   - **Coach each thread** with §8.4, in parallel with at most 3 concurrent calls:
     - `QUESTION` is the main question.
     - `CANDIDATE_ANSWER` is the thread transcript, formatted as `Interviewer: … / You: …` lines.
     - `MODE` is `mock`.
   - **Run the Debrief prompt** (§8.5) once on the whole transcript plus the plan.
   - Show the Debrief screen as soon as the Debrief returns, and fill in the thread cards as their Coach calls finish.

### 6.2 Learn engine
```
Generating → Showing (question + coach output) → [Next → Generating] | [Follow-up click → Coaching follow-up] | [Try it myself → hand off to Practice]
```
1. Call the Question Generator (§8.3). Pass the type filter and the list of questions already asked this session, so it doesn't repeat.
2. Immediately call the Coach (§8.4) with an empty `CANDIDATE_ANSWER` and `MODE = learn`.
3. Show the question and the Coach output together. Showing the question first while the Coach loads is fine, since the user isn't answering in this mode.

### 6.3 Practice engine
```
Generating → AskingQuestion (TTS) → Answering → Coaching → ShowingFeedback → [Retry → Answering] | [Follow-up → AskingQuestion] | [Next → Generating]
```
1. Generate the question with §8.3. Speak it and show it.
2. **Answering.** No Coach call may be made in this state. Enforce this in the engine, not only in the UI.
3. On Submit, call the Coach with `MODE = practice`. Include the answer, input method, duration and word count.
4. **Retry.** Pass the previous attempt's text as `PREVIOUS_ATTEMPT`, so the Coach comments on what improved.
5. **Follow-up.** The chosen follow-up becomes the new question. Include the original question and answer in `TRANSCRIPT`, so the context carries over.

---

## 7. Data model (EF Core, SQLite)

```
CandidateProfile  Id, Name, JobRole, Seniority, JobDescription, ResumeText, CreatedAt, UpdatedAt
Session           Id, ProfileId, Mode (Mock|Learn|Practice), RoundType?, DurationMinutes?, StartedAt, EndedAt?, PlanJson?, DebriefJson?
Turn              Id, SessionId, Index, Speaker (Interviewer|Candidate), Text, RawJson?, TurnType?, Phase?, FocusAreaId?,
                  InputMethod?, DurationSeconds?, CreatedAt
QuestionThread    Id, SessionId, MainTurnId, CoachJson?            // Mock debrief cards
PracticeItem      Id, SessionId, Question, QuestionType, Source, Focus, ParentPracticeItemId? (for follow-ups)
Attempt           Id, PracticeItemId, AnswerText (empty for Learn), InputMethod?, DurationSeconds?, WordCount?, CoachJson, CreatedAt
```

Settings live in `settings.json`, not the DB:
- the provider, model and key settings from §4.9
- the behavior toggles

Model defaults:
- every role uses `claude-sonnet-5-5`
- suggest a faster model for the Interviewer if latency feels slow
- suggest the strongest available model for Coach and Debrief if quality matters more than cost

---

## 8. Prompts

Save each block below as the named file in `src/InterviewCoach.App/Prompts/`.

**Rendering:**
- `{{VARIABLE}}` placeholders are filled by `PromptRenderer`.
- Large user-provided data (JD, resume, transcripts) is wrapped in XML-style tags inside the prompt. Insert it raw, with no escaping needed.
- Unresolved placeholders are a bug: throw.

**Variables available everywhere:**
- `JOB_ROLE`, `SENIORITY`
- `JOB_DESCRIPTION`, `RESUME`

**Variables available where relevant:**
- round setup: `ROUND_TYPE`, `DURATION`
- generated data: `PLAN_JSON`, `QUESTION_TYPES`, `ALREADY_ASKED`
- coaching inputs: `MODE`, `QUESTION`, `TRANSCRIPT`, `CANDIDATE_ANSWER`, `PREVIOUS_ATTEMPT`
- answer stats: `INPUT_METHOD`, `DURATION_SECONDS`, `WORD_COUNT`

When a value is absent, render it as `(none)`.

**Each call's user message:**

| Prompt | Single user message |
|---|---|
| Planner | `Create the interview plan.` |
| Interviewer | (no single message; see §6.1) |
| Question Generator | `Give me the next question.` |
| Coach | `Coach this.` |
| Debrief | `Write the debrief.` |

### 8.1 `planner.md`

~~~text
You are an experienced {{JOB_ROLE}} hiring manager preparing to interview a candidate for a {{SENIORITY}} role. The round is a {{ROUND_TYPE}} round lasting {{DURATION}} minutes. Plan it the way a strong interviewer would before walking into the room.

<job_description>
{{JOB_DESCRIPTION}}
</job_description>

<candidate_resume>
{{RESUME}}
</candidate_resume>

Decide:
1. FOCUS AREAS. Pick the 4–6 things this round must find out. Draw them from three places:
   - the JD's must-have skills
   - the resume claims that matter most for this role, especially any that look vague, inflated, or surprisingly strong
   - the fundamentals anyone at this level in this role must know
   Prioritize. A real round can't cover everything.
2. RESUME CLAIMS TO PROBE. Pick 2–4 specific resume lines worth digging into. For each one, write a probe that only someone who actually did the work could answer well.
3. PHASES. Lay out the round as phases with target minutes that add up to {{DURATION}}. Use only the phases that fit the round type:
   - opener
   - tell_me_about_yourself
   - resume_deep_dive
   - technical
   - system_design
   - coding_talkthrough
   - behavioral
   - candidate_questions
   Recruiter screens are mostly background, motivation and logistics-style questions. Hiring manager rounds lean on ownership, judgment and behavioral questions. Technical and system design rounds still start with a short opener.
4. OPENING LINE. Write the first thing the interviewer says: a brief, natural greeting and a one-line intro of themselves and the format. Don't ask a question yet unless it's "How's your day going?"-level small talk.

Calibrate to {{SENIORITY}}:
- Junior: fundamentals, learning, finishing things.
- Mid: owning features end to end, trade-offs, debugging, collaboration.
- Senior: ambiguity, design decisions and their costs, influence, mentoring, business impact.
- Staff and above: cross-team direction, long-term bets, org-level impact.

Reply with only JSON in exactly this shape:
{
  "focus_areas": [
    { "id": "fa1", "name": "short label", "why": "one sentence on why it matters for this role", "source": "jd | resume | fundamentals | behavioral" }
  ],
  "resume_claims_to_probe": [
    { "claim": "the resume line, paraphrased", "probe": "the question or angle that tests it" }
  ],
  "phases": [
    { "phase": "opener", "target_minutes": 2, "topics": ["short topic labels"] }
  ],
  "opening_line": "what the interviewer says first"
}
~~~

### 8.2 `interviewer.md` (Mode 1 only)

~~~text
You are an experienced {{JOB_ROLE}} interviewer running a live {{ROUND_TYPE}} round for a {{SENIORITY}} position. The round lasts {{DURATION}} minutes. You've interviewed hundreds of engineers. You're friendly but not a cheerleader, and your job is to find out what this person actually knows and has actually done. Everything you say is spoken aloud to the candidate by text-to-speech, so talk the way a person talks on a call.

<job_description>
{{JOB_DESCRIPTION}}
</job_description>

<candidate_resume>
{{RESUME}}
</candidate_resume>

<your_private_plan>
{{PLAN_JSON}}
</your_private_plan>

Follow your plan, but react to what the candidate actually says. Start with the plan's opening_line.

HOW YOU ASK
- Ask one question per turn. Never stack two questions.
- Keep questions short and spoken. Write "Walk me through how the ranking service worked," not a paragraph of setup.
- Follow up on the candidate's actual words before moving on. Most real signal comes from the second and third question on the same topic. Typical follow-ups:
  - "Why that over [alternative]?"
  - "What was your part specifically, versus the team's?"
  - "How did you know it worked? What did you measure?"
  - "What broke along the way? What would you do differently?"
  - "What happens if traffic goes up 10x, or that dependency goes down?"
  Usually ask one or two follow-ups per main question. Ask more if the answer was vague, and ask fewer if time is short.
- When an answer is vague or full of buzzwords, ask for the concrete version: "Can you give me a specific example?" or "What did that look like in the code?"
- Test big resume claims gently, using the probes in your plan.
- If the candidate is stuck, rephrase or give one small hint, then move on without making it awkward.
- If they say "I don't know," accept it and move on. Don't teach.
- If they ask a clarifying question ("Can I assume…?"), answer it briefly, the way an interviewer would.
- If they ask you to repeat or rephrase, do it.
- In the candidate_questions phase, answer their questions briefly and honestly as someone on the team. If the JD doesn't tell you, say you'd have to check rather than inventing company facts.

HOW YOU REACT
- Start your turns with brief, neutral acknowledgments when natural: "Okay." "Got it." "Makes sense." "Interesting, okay." Then ask your next question in the same turn.
- Never praise or grade answers, never say what a good answer would be, never hint at the answer to your own question, and never break character. Feedback happens after the interview, not by you.
- Keep small talk to the opener and the close.

TIME
The candidate's turns end with a line that starts "[app context]". It tells you the elapsed time and how long the answer took. It is not the candidate speaking, so never mention it. Use it to pace yourself against the plan's phases:
- If a phase runs long, transition naturally: "Let's switch gears a bit."
- When about 5 minutes remain, move to candidate_questions.
- When time is up, close warmly and briefly ("Thanks, this was great to chat. The team will be in touch with next steps.") and set end_interview to true.
Also set end_interview to true if the candidate clearly asks to end.

Reply with only JSON in exactly this shape:
{
  "say": "exactly what you say out loud this turn",
  "turn_type": "smalltalk | main_question | follow_up | hint | clarification | candidate_questions | closing",
  "phase": "one of the phase names from your plan",
  "focus_area_id": "fa1 (or null if none)",
  "end_interview": false
}

Rules for "say":
- Plain spoken English. No markdown, no lists, no stage directions, no emojis.
- Usually 1–3 sentences. The opener and close can be a bit longer.
~~~

### 8.3 `question_generator.md` (Modes 2 and 3)

~~~text
You are an experienced {{JOB_ROLE}} interviewer choosing the next question for a candidate interviewing for a {{SENIORITY}} role.

<job_description>
{{JOB_DESCRIPTION}}
</job_description>

<candidate_resume>
{{RESUME}}
</candidate_resume>

<allowed_question_types>
{{QUESTION_TYPES}}
</allowed_question_types>

<already_asked_this_session>
{{ALREADY_ASKED}}
</already_asked_this_session>

Pick one question that a real interviewer for this role would plausibly ask this candidate.
- Use only the allowed question types. "Any" means any type.
- Don't repeat or closely rephrase anything already asked. Vary the type and the topic across the session.
- Ground it:
  - Resume questions name a specific project, system or claim from the resume.
  - Technical and design questions target the JD's actual requirements.
  - Behavioral questions target what this level is judged on.
- Phrase it exactly as an interviewer would say it out loud: short, natural, and with one question only.
- The question must not hint at its own answer.
- Difficulty must match {{SENIORITY}}.

Question types:
- tell_me_about_yourself
- resume_deep_dive
- technical_concept
- system_design
- coding_talkthrough: describe a small problem verbally, and ask how they'd approach it
- behavioral

Reply with only JSON in exactly this shape:
{
  "question": "the question as spoken",
  "question_type": "one of the types above",
  "source": "resume | jd | fundamentals | behavioral",
  "focus": "a 2-6 word label of what it probes"
}
~~~

### 8.4 `coach.md` (Modes 2 and 3, plus each Mode 1 debrief thread)

~~~text
You are a senior {{JOB_ROLE}} who has sat on many hiring loops and now coaches engineers for interviews. You can tell an answer that sounds rehearsed from one that sounds like an engineer who did the work and is talking about it.

<job_description>
{{JOB_DESCRIPTION}}
</job_description>

<candidate_resume>
{{RESUME}}
</candidate_resume>

<context>
Mode: {{MODE}}
Seniority: {{SENIORITY}}
Earlier in this session: {{TRANSCRIPT}}
</context>

<question>
{{QUESTION}}
</question>

<candidate_answer input_method="{{INPUT_METHOD}}" duration_seconds="{{DURATION_SECONDS}}" word_count="{{WORD_COUNT}}">
{{CANDIDATE_ANSWER}}
</candidate_answer>

<previous_attempt>
{{PREVIOUS_ATTEMPT}}
</previous_attempt>

Modes:
- learn: there is no candidate answer. The candidate wants to see how to answer before trying.
- practice: they answered one question.
- mock: candidate_answer holds the whole exchange for one question from a live mock interview, including the interviewer's follow-ups. Coach the exchange as a whole, and note how they handled the follow-ups.

If the answer came from speech-to-text, ignore transcription glitches and missing punctuation. Do comment on rambling, filler, or answers that run far too long or short.

Your job is to show the candidate three things: what the interviewer was really testing, how their answer would land, and what a strong answer sounds like when they say it out loud.

=== WHAT INTERVIEWERS LISTEN FOR ===

By question type:

- Tell me about yourself (60–90 seconds): where they are now, the one or two things from their past that matter for this job, and why this role is the next step. It should not be a walk through the resume.

- Resume / project deep-dive:
  - what the system did and why it mattered
  - what they personally built or decided
  - the hardest problem and how they got through it
  - what they'd change
  The interviewer is checking whether they really did it.

- Technical concept: open with the direct answer in a sentence or two. Then explain how it works, then the trade-off or when not to use it, then where they've used it, if they have. Then stop and let the interviewer ask for more depth.

- Design / problem-solving: clarify what's needed before solving, and state assumptions out loud. Start simple, then improve, naming trade-offs along the way. It's a conversation, not a monologue.

- Coding talk-through, in this order:
  1. clarify inputs and edge cases
  2. the brute-force idea
  3. the better approach and why
  4. complexity
  5. how you'd test it

- Behavioral:
  - a real situation, with just enough context to follow
  - what THEY did and why (most of the answer goes here)
  - the outcome, with a number if one exists
  - what they learned or would do differently
  That's STAR, but it should never sound like STAR.

Across all question types:
- Specifics beat adjectives, and trade-offs show judgment.
- Say "I" for what they did and "we" for what the team did.
- Be honest about what went wrong.
- Keep the scope right for {{SENIORITY}}.

=== HOW THE MODEL ANSWER MUST SOUND ===

Write it as speech: what a thoughtful engineer says in the room, not text written for a page.

- Open the way people actually open: "Yeah, so..." "Sure. Short version is..." "It depends a bit on X, but..." Vary the openings across answers.
- Use contractions. Keep most sentences short, with longer ones only where the thought needs it.
- Use plain verbs: built, fixed, broke, chose, moved, cut, shipped, measured.
- Use concrete nouns: the actual tool, table, service, metric, team, number. One real detail beats three adjectives.
- Reason out loud: "The reason we didn't just X was..." "The tricky part was..." "In hindsight..."
- Leave honest edges in: a mistake, a constraint, something they'd do differently, or "I haven't run X in production, but here's how I'd think about it."
- Put no labels or formatting inside the answer: no "Situation:", no "Firstly / Secondly / Lastly", no bullets, no markdown. The structure lives in the order of the ideas and in natural signposts like "so", "the problem was", "what I ended up doing", "the result was".
- Length targets:
  - Behavioral and project answers: 150–280 words (about 1–2 minutes spoken)
  - Concept questions: 60–150 words
  - Tell me about yourself: 130–200 words
  - Design and coding: only the first 1–2 minutes, ending with a question back to the interviewer or "Want me to go deeper on X?"
- In mock mode, the model answer is the strong answer to the main question. Weave in what a good response to the most important follow-up would add.

Never use these words or phrases: leveraged, utilized, spearheaded, robust, seamless, synergy, cutting-edge, delve, streamlined, holistic, "passionate about", "fast-paced environment", "plays a crucial role", "this experience taught me the importance of". Don't start sentences with Furthermore, Moreover or Additionally. If a sentence could appear in anyone's answer, cut it or make it specific.

=== STAY TRUE TO THE CANDIDATE ===

- Build the model answer from their resume and, if they gave one, their own answer. Keep their story, their examples and their way of putting things; make it tighter, more specific and better ordered. An answer memorized in someone else's voice sounds memorized.
- Never invent experience, employers, tools or results. If the answer needs a detail the resume doesn't have (a metric, a team size, a tool), use a bracketed placeholder such as [your actual p99 before/after]. Interviewers ask follow-ups, and a made-up detail falls apart on the second question.
- If the resume has nothing relevant to the question, write an honest answer instead: adjacent experience, or how they would approach it.

=== FEEDBACK RULES ===

- Learn mode: "feedback" is an empty array and "delivery" is null.
- Otherwise, give 2–4 points. Each point is specific and quotes the candidate's own words where possible:
  - what worked
  - what an interviewer would doubt or probe
  - what was missing
- Be direct and kind, with no generic praise.
- If previous_attempt is not "(none)", make the first feedback point about what changed since that attempt: what got better, and what's still missing.
- "delivery" is null unless the answer has a duration or came from voice. When it applies, give one or two sentences on length and pace against the targets above (e.g. "3:40 is long for this; cut the setup to two sentences").

=== OUTPUT ===

Reply with only JSON in exactly this shape:
{
  "what_theyre_testing": "1-2 sentences on the real signal behind the question at this level",
  "feedback": [
    { "kind": "strength | fix | missing", "point": "the feedback", "quote": "the candidate's words this refers to, or null" }
  ],
  "model_answer": "the strong answer as plain spoken prose, no markdown",
  "shape": "the skeleton in one line, e.g. Direct answer → the constraint → what you chose and why → result with a number → what you'd change",
  "delivery": "length/pace comment, or null",
  "follow_ups": [
    { "question": "a likely follow-up as the interviewer would say it", "hint": "one line on how to handle it" }
  ]
}
Give 2–3 follow-ups.

=== EXAMPLE OF THE DIFFERENCE ===

Question: "Tell me about a production issue you debugged."

ROBOTIC (never write like this):
"In my previous role, I encountered a critical production issue that significantly impacted system performance. Leveraging a systematic approach, I utilized monitoring tools to identify the root cause. Subsequently, I implemented a robust solution that resulted in a 40% performance improvement. This experience taught me the importance of proactive monitoring and cross-functional collaboration."

HUMAN (write like this):
"Yeah, so at [Company] our checkout API started timing out every evening around peak. p99 went from roughly [300ms] to over [4s], and we were losing orders. I was on call that week, so it was mine. First thing I checked was whether it lined up with a deploy. It didn't, it lined up with traffic. So I pulled the slow query log and found one query doing a full scan on the orders table. Someone had added a filter on a status column that wasn't indexed. Fine at low traffic, but at peak it was holding locks and everything backed up behind it. The quick fix was a composite index. I built it on a replica first, because the table was big and I didn't want to lock prod during the build. p99 was back under [400ms] that night. The bigger fix was process. I added a check to our migration review for new queries on large tables without an index, and we haven't had that kind of issue since. Honestly, what I'd change is the alerting. We had a latency alert, but the threshold was so loose it didn't fire until customers were already complaining."

Why the second one works:
- It opens on the actual problem and says whose it was.
- It walks through the reasoning in order.
- It names real things: p99, slow query log, replica, composite index.
- It gives a result and a lasting fix, and admits what was missed.
- It sounds like a person remembering something that happened.
The brackets are placeholders: use the candidate's real values from the resume, or keep the placeholder.
~~~

### 8.5 `debrief.md` (end of Mode 1)

~~~text
You are the {{JOB_ROLE}} interviewer who just finished this {{ROUND_TYPE}} round with a {{SENIORITY}} candidate. Write the honest debrief you'd give in a hiring loop, but address it to the candidate, as a coach would.

<job_description>
{{JOB_DESCRIPTION}}
</job_description>

<candidate_resume>
{{RESUME}}
</candidate_resume>

<interview_plan>
{{PLAN_JSON}}
</interview_plan>

<transcript>
{{TRANSCRIPT}}
</transcript>

Rules:
- Judge against the plan's focus areas and the {{SENIORITY}} bar, not against perfection.
- Every rating and claim must point to something the candidate actually said. Quote short phrases as evidence.
- If a focus area never came up, rate it null and say it wasn't covered.
- Be specific and direct. Don't pad praise and don't pile on. A candidate should finish reading this knowing exactly what to practice next.
- Write in plain, spoken-style English. No buzzwords.

Rating scale:
- 1 = no signal or a clear gap
- 2 = below the bar
- 3 = meets the bar
- 4 = strong

Reply with only JSON in exactly this shape:
{
  "overall_summary": "3-5 sentences: how the round went and the single biggest thing holding them back",
  "hire_signal": "strong_no | no | lean_no | lean_yes | yes | strong_yes",
  "focus_area_ratings": [
    { "focus_area_id": "fa1", "name": "label", "rating": 3, "evidence": "what they said that supports the rating" }
  ],
  "strengths": ["2-3 specific strengths with evidence"],
  "top_fixes": [
    { "fix": "what to change", "example": "where it showed up in the transcript", "how_to_practice": "one concrete drill" }
  ],
  "practice_next": ["2-4 specific questions or topics to drill in Practice mode"]
}
~~~

---

## 9. Transcript formatting

When a prompt needs a transcript (`TRANSCRIPT`, or a Mock thread passed as `CANDIDATE_ANSWER`), format it as plain lines with one turn per line. Don't include `[app context]` lines.

```
Interviewer: Walk me through how the ranking service worked.
You: Sure, so it took the candidate list from search...
Interviewer: Why Redis there instead of keeping it in-process?
You: ...
```

For `ALREADY_ASKED`, use a numbered list of the question strings asked this session, or `(none)`.

---

## 10. Error handling and edge cases

- **No mic, or mic permission denied:** show a banner and disable the mic button. Typing still works.
- **STT provider error mid-answer:** keep any text already captured, show a toast, and leave the text box editable.
- **TTS failure:** show the text, and continue without audio.
- **Empty submit:** block it with the inline message "Say or type something first. 'I don't know' is a fine answer too."
- **LLM failure:** keep all state and offer **Retry**. Never discard the candidate's answer.
- **Very long resume or JD (over ~40k characters):** warn the user and offer to trim.
- **Mock window closed mid-round:**
  - The transcript is saved after every turn.
  - Offer **Resume interview** (only when unfinished and less than 24 hours old) or **End and debrief** from History.

---

## 11. Testing

- `PromptRenderer`:
  - every prompt file renders with a full variable set and leaves no `{{…}}` behind
  - a missing variable throws
- JSON parsing:
  - sample outputs for each schema deserialize correctly
  - fenced JSON and leading prose are tolerated
  - the repair retry path fires on bad JSON
- Engines, using `FakeLlmService` and the fake speech services:
  - Practice never calls the Coach before Submit
  - Learn calls the Coach with an empty answer
  - Mock ends on `end_interview` and on the time limit
  - Mock thread building groups follow-ups correctly
  - Retry passes `PREVIOUS_ATTEMPT`
- Composer:
  - partial-then-final text handling
  - starting the mic stops TTS
  - text survives a failed submit

---

## 12. Milestones

Build in this order. Stop at the end of each milestone, summarize what was built, and explain how to run it.

1. **Skeleton.**
   - Solution and projects, DI host in WPF, SettingsStore with DPAPI, Settings screen, Demo mode with fakes.
   - `PromptRenderer`, with all five prompt files saved.
   - `LlmService` over `IChatClient` (Anthropic first), with **Test connection**.
   - *Done when:* the app launches, saves a key, and Test connection succeeds.
2. **Profiles.**
   - Profile CRUD, paste or load of JD and resume (PDF/DOCX/TXT extraction), SQLite and migrations.
   - *Done when:* a profile with an extracted resume persists across restarts.
3. **Learn mode (text).**
   - Question Generator, then Coach with an empty answer, and the Coach output cards.
   - *Done when:* Next keeps producing non-repeating, grounded questions, each with a spoken-style model answer.
4. **Practice mode (typed).**
   - The composer without a mic, Submit, Coach with the answer, Retry with comparison, follow-ups.
   - *Done when:* feedback quotes the user's words, and Retry comments on changes.
5. **Voice.**
   - `ISpeechToText` and `ITextToSpeech` with Azure, then OpenAI, then Windows TTS.
   - Mic in the composer, TTS for questions, barge-in, answer timer, auto-listen, voice settings and tests.
   - *Done when:* in Practice, the question is spoken, the user can dictate and edit an answer, and delivery feedback appears.
6. **Mock Interview.**
   - Planner, the Interviewer loop with TTS and auto-listen, time pacing, end conditions.
   - Thread building, parallel Coach calls, Debrief screen, Markdown export.
   - *Done when:* a 15-minute mock runs end to end by voice and produces a full debrief.
7. **History and polish.**
   - History screen, resume of unfinished mocks, error banners, keyboard shortcuts, the test suite from §11 green.

---

## 13. Setup the user needs to do

- Install the .NET 10 SDK and Visual Studio (or VS Code with C# Dev Kit).
- Get an Anthropic API key from console.anthropic.com, or use an OpenAI-compatible key.
- For voice, set up one of these:
  - an Azure AI Speech resource (key and region; recommended for live transcripts and natural voices)
  - an OpenAI API key
  - the offline Windows voice for TTS, which needs no key, though speech-to-text still needs Azure or OpenAI
- Note on privacy: the resume and JD are sent to the chosen LLM provider on each call. Everything else stays on this machine.

---

## 14. Changes made after the first build

The sections above are the original spec. These later decisions override them where they differ.

- **Scenario question type (`scenario`, label "Scenario-based").** Added to the question types (§4.1, §8.3) as a seventh type. `question_generator.md` defines it (a realistic work situation in one to three spoken sentences, then one question about what they would do) and lets a scenario use a short setup. `coach.md` has a "Scenario" coaching entry and a 120–220 word target. "Any" now means all seven types.
- **Answer length.** `coach.md` has a new variable `{{ANSWER_LENGTH}}`, shown in `<context>` as "Requested model answer length". When it holds a word count, the model answer is written to about that length for every question type; when it is `(none)`, the per-type targets apply. Learn mode lets the user choose Interviewer norm, Short (80), Medium (150), Long (250) or a custom 30–600 words. The model answer card shows its word count, spoken time at 130 words per minute, and the typical interviewer expectation for the question type.
- **Learn mode ignores invented feedback.** If the Coach returns feedback or delivery text in Learn mode, it is dropped, since there is no candidate answer to give feedback on.
- **JSON extraction.** The parser takes the first complete JSON value instead of cutting at the last brace, so a stray brace or commentary after the JSON does not cost a repair call.
- **Thinking effort.** Settings has a "Thinking effort" option (Model default, Low, Medium, High) sent to Anthropic models other than Haiku, because newer models think by default and that can be most of the wait.
- **Prefetching.** Learn mode prepares the next question and its answer in the background while the current one is read, so Next is instant. It costs one extra pair of calls that is discarded if the session ends first.
- **Debug log.** Each logged call records seconds taken, token counts (including thinking and cache reads/writes), reply length and whether a repair retry happened.
- **Prompt caching.** For Anthropic, the system prompt is split just after `</candidate_resume>` and the first part (role, job description, resume) carries a cache marker, so repeat calls within about five minutes pay roughly a tenth of the input price for it. All prompts keep the resume and job description before anything that changes per call, which is why prompt text before `</candidate_resume>` must stay stable. A setting turns it off.
- **Answer length with no word count ("Interviewer norm").** `coach.md` has a new context line `Question type: {{QUESTION_TYPE}}` and `{{ANSWER_LENGTH}}` now carries a range when no word count was requested: the same range the screen shows beside the answer (for example "between 60 and 150 words (roughly 30 sec to 1 min 10 sec spoken)" for a technical concept), taken from one table in code (`AnswerLength.Expectation`). It is `(none)` only for a question type the app does not know. The length rule says to stay inside a range and aim for its middle, and never to go more than 10 percent over a word count. This was added after the log showed 250 to 300 word answers to one-line concept questions, because the Coach was never told the type or a range. General answers saved under the old "no length" key are not reused (the key for "interviewer norm" is now -1, previously 0).
- **Question length.** `question_generator.md` gives per-type length rules instead of just "short": a technical-concept question is one direct sentence of about 6 to 18 words (for example "What's the difference between checked and unchecked exceptions?"), behavioral and tell-me-about-yourself are one sentence, resume deep-dives one or two, system design one, coding and scenario up to three sentences of setup, and every question asks one thing with no follow-on clause. Questions saved in the technology bank before this change keep their old wording until cleared.
- **Coach prompt order.** `coach.md` now puts all fixed guidance first and the request's details last, after a `=== THE CANDIDATE AND THE QUESTION ===` header: job description, resume, then `<context>` (which now also carries `Role: {{JOB_ROLE}}`), question, answer and previous attempt, ending with a one-line reminder to reply with the JSON under OUTPUT. The fixed part contains no variables, so it is byte-identical for every call and is cached once for everyone; the job description and resume are a second cached block. The role and seniority wording in the fixed text now refers to the context instead of naming them. The header text must appear exactly once in the fixed part, because the cache split looks for its first occurrence.
- **By technology (a question-type option).** Next to the question types on Home there is a "By technology…" checkbox. Ticking it reads the technologies from the saved profile's job description (the same `tech_tags.md` extraction, remembered per job description) and lists them as checkboxes; Start needs at least one ticked. Every question is then about the ticked technologies, drawn from the technology bank (general questions and answers that avoid your resume) and never repeating the same technology twice in a row when there is a choice. With other types ticked as well, the technology option counts as one share alongside them (Technical concept and By technology together are still one share). If only By technology is picked and a question cannot be produced, the session shows the error with Retry instead of asking about something else. With the saved bank switched off in Settings, the model writes the technology question (using `{{FOCUS_TECHNOLOGY}}`) tailored to the resume. Unsaved edits block Start, since a session uses the saved profile.
- **Full-time or contract.** Under "Start a session" there is a Role type choice (Full-time or Contract), remembered between runs in `settings.json` (`EmploymentType`, default Full-time). It is sent to the prompts as `{{EMPLOYMENT_TYPE}}` ("Full-time" or "Contract"): in `question_generator.md` a new `<employment_type>` block and a rule describing what each kind of interviewer asks, and in `coach.md` an `Employment type:` context line plus a "FULL-TIME AND CONTRACT INTERVIEWS" section on what each kind listens for. Two question types were added, each belonging to one kind: `motivation_fit` ("Motivation and fit", full-time: why this company and role, growth, strengths and weaknesses) and `engagement` ("Availability and engagement", contract: availability, notice, rate, contract length, location). Only the types that apply to the chosen kind are offered; switching swaps that one type and keeps the other ticks. The engine never offers the other kind's type when it lists types explicitly, and the generator prompt forbids it for "Any". The technology bank is the exception: its saved questions and answers are general, are written with `(none)` for the employment type, and are shared by both kinds. Planner, interviewer and debrief prompts for Mock mode will take the same variable when that mode is built.
- **Other technologies.** Under By technology there is an "Other" chip with a text box for technologies the job description did not mention or that were not detected (comma, semicolon or new-line separated; at most 8, each shortened to 60 characters). Typing a name ticks Other; ticked detected technologies and typed ones are combined without duplicates. Other works even when detection found nothing or failed. Typed names are not part of any job description, so they stay when you switch profile.
- **App icon and look.** The app has an icon (a speech bubble holding a check mark on an indigo-to-violet tile) drawn by `tools/IconGenerator` into `src/InterviewCoach.App/Assets` (`app.ico` with sizes 16 to 256, plus `logo-64.png` and `logo-256.png`); it is the exe icon, the window icon and the sidebar logo. The interface uses one shared set of styles in `App.xaml`: a branded sidebar with icon navigation and an accent bar on the current page, card-based pages with a title and subtitle, selectable chips for question types and technologies, mode cards with selected and disabled states, a sticky action bar and a danger style for destructive actions. Surfaces, text and accents come from the Fluent theme (`DynamicResource`), so light and dark mode both follow the system setting. Custom controls must set their own text color and any style on a themed control must inherit from it with `BasedOn`, or dark mode shows classic white boxes. The Learn card on Home is bound to the view model (always selected for now) rather than a radio group, whose state is shared between view instances.
- **Wheel scrolling on Home.** The job description and resume boxes pass the mouse wheel to the page unless the box has keyboard focus and still has text to scroll in that direction, so the page scrolls to the Start button with the pointer anywhere over it.
- **Technology bank (saved technical questions and general answers).**
  - New prompt `tech_tags.md` (user message "List the technologies.") extracts up to 8 technologies from a job description. The result is stored under a fingerprint of the job description text (whitespace-insensitive), so editing the resume costs nothing and re-pasting the same job description costs nothing.
  - `question_generator.md` has a new variable `{{FOCUS_TECHNOLOGY}}` (`(none)` normally). When set, the question must be about that technology and must not depend on the resume.
  - Technical-concept questions are saved per technology and seniority (SQLite tables `TechQuestions`, `TechAnswers`, `JdTechnologies`). A saved question is reused until every saved one for that technology and level has been seen this session; only then is a new one written. Nothing in the bank is sent a resume or job description (role "software engineer"), so it stays valid across edits.
  - The general answer is written by `coach.md` with no job description and a note in place of the resume asking for a general answer with bracketed placeholders for personal experience. Answers are saved per question and per requested length. Each general answer has a "Tailor to my resume" button, which writes a normal answer from the resume (not saved).
  - With several question types allowed, the bank supplies its fair share (1 in N) of the technical-concept questions and the model writes the rest without being offered the technical type. If no technologies are found, or any bank step fails, the model writes the question as before.
  - Demo mode uses an in-memory bank so made-up questions never reach the real database. Settings can turn the bank off and clear it.
- **OpenRouter provider.** Settings has a third provider, "OpenRouter (one key, hundreds of models)": one OpenRouter key (stored encrypted like the others, or `OPENROUTER_API_KEY`) works for every model, and the model is chosen by its id (for example `anthropic/claude-sonnet-5.5`). OpenRouter keeps its own five per-role models (`OpenRouter*Model`, default `anthropic/claude-sonnet-5.5`) separate from the Anthropic and OpenAI-compatible ones, because the ids differ; switching provider on the Settings screen swaps the boxes and keeps both sets. Under "Choose a model" the user clicks "Load models" to download OpenRouter's public model list (text models only; discounted batch copies are left out), searches it by name or id, optionally sorts cheapest first, sees each model's price per million tokens and context size, and applies the selected model to the Question generator, the Coach or every role; nothing is saved until Save. The list is only fetched when asked. Thinking effort is sent through OpenRouter as its own `reasoning` request field (the OpenAI `reasoning_effort` field is not used); prompt caching is not used through OpenRouter.
- **Recommended setups.** Settings shows ready-made model choices above the per-role boxes: Anthropic has one ("Recommended"), OpenRouter has two ("Recommended" and "Lower cost"), OpenAI-compatible has none. Each is a list of the five roles with the model, plus a "Use this setup" button that fills in the boxes and sets Thinking effort to Low (nothing is saved until Save). Rows show why on hover; for OpenRouter they show the live price once Load models has been clicked, or say the model is no longer in OpenRouter's list. The Recommended setups use the question generator on the small model (Haiku) and the coach, planner and debrief on the strong one (Sonnet), with the interviewer on the small one. The Lower cost setup keeps Haiku for the question generator and uses GPT-5 Mini for the other roles; it was tried on seven questions (5 to 9 seconds per answer, roughly a fifth of the cost of Sonnet at Low), and a first version with Gemini 3.5 Flash Lite as the question writer was dropped after the debug log showed it spent most of its output tokens thinking.
- **Answers start with the point.** `coach.md` no longer tells the model to "open the way people actually open", and its example answer no longer starts "Yeah, so": the model copied both (30 of 32 logged answers began "Sure. Short version:", "Yeah, so" or "Short answer:"). The rule now says to start with the substance and lists the openers not to use. In code, `CoachText.WithoutOpeningFiller` removes a warm-up from the start of a model answer (a filler word only when punctuation follows it, so "Right now", "Well-known" and "So far" are safe, and "Yes" and "No" are kept), and `CoachOutput.ForLearning()` applies it, together with the dropped feedback, to every answer Learn mode shows: fresh replies, general answers written for the bank, and answers saved before the change.
- **No thinking effort for the fast roles on OpenRouter.** On OpenRouter the question generator and the interviewer send no thinking effort even when one is chosen, because an effort makes models that otherwise do not think start thinking (a logged Gemini 3.5 Flash Lite call spent about 500 of 550 output tokens thinking about a 25-token question). The coach, planner and debrief still send it. Anthropic and OpenAI-compatible are unchanged.
- **Already-asked list.** The list of questions the generator is told to avoid no longer repeats a question that is both saved in the technology bank and asked in this session.
- **Question quality: batches, examples, variety.** When every saved technical question for a technology and level has been seen, the technology bank asks the model for 10 new ones in a single call (`question_batch.md`, user message "Write the questions."): most commonly asked first, no more than two from one area, at most three starting "What's the difference between", at least three situational ("What happens if...", "How would you track down..."), 6 to 18 words each, none repeating what is saved or asked. All are saved, the first is shown and the rest come from the bank with no call. If the batch fails or adds nothing new, one question is written the old way. `question_generator.md` now gives an example of the right length for resume deep-dives (12 to 25 words), system design (up to 15 words) and engagement (one ask), says that a second request joined by "and" is a second question, asks the model to prefer commonly asked questions over obscure corners and to change the form when most earlier questions start the same way. The engagement example that itself contained two asks was replaced.
- **Library (Revisit).** Home has a fourth mode card, "Revisit", beside Practice, and the sidebar has a "Library" item. Every question and answer Learn mode shows is kept (table `LearnHistory`), recorded once the answer is on screen: fresh questions, questions prepared in the background (only when shown), saved bank questions with their saved answers, follow-ups with their parent question, and tailored answers. An entry is the question plus the kind of answer (general, or for a profile) plus the parent question, so seeing it again updates it (last seen, times seen, newest answer) rather than adding a copy, and a general answer and a tailored one stay separate. The Library screen has a list with a search box (question, technology, type, answer text), question-type chips with counts (All plus the types present), a sort (newest, oldest, question A to Z, type), and a detail pane showing the full answer with the same cards as Learn mode; "Remove from library" deletes an entry after confirmation. Opening a follow-up inside an answer shows its own saved answer if there is one. Recording is best effort and never interrupts a session. The first run after upgrading copies the technical questions already saved in the bank (those with a saved answer) into the library. Demo mode uses an in-memory library. Answers saved with an opening warm-up are shown cleaned.
- **Resume questions are spread across the resume.** A debug log showed 10 of 13 resume questions began "At CPF, you...", all about the current client, and the model repeated one topic in other words even with the earlier question in its avoid list. The choice is now made in code. The first time a resume is used the app reads it once (`resume_topics.md`, user message "List the topics.", up to 8 employers or projects with 2 to 5 highlights each, in the resume's own words), and remembers the result by a fingerprint of the resume text (table `ResumeTopics`; editing only the job description does not re-read it, editing the resume does). For a question that may be a resume deep-dive (that type ticked, alone or among others, or Any with the saved bank in use and the model writing freely) the app picks the employer used least so far in the session (ties random, never the same employer twice in a row when another is due), then the least used highlight at that employer, plus a first word that changes every time (Why, How, What, When, Which), and passes them as `{{RESUME_FOCUS}}`: "Ask about this part of the resume: EDF, monolith migration: applied the strangler fig pattern. Begin the question with the word 'Why'..." The prompt rule says to follow it exactly when writing a resume deep-dive. A part of the resume is used up only when the model actually wrote a resume deep-dive question. If reading the resume fails, the question is written with no focus.
- **Near-duplicate questions.** Model-written questions are compared with everything already asked in the session by topic words (filler such as "what", "how", "would", "between", "difference" ignored): two questions are the same when they share at least four topic words that make up at least half of the shorter one's. A repeat is sent back once with the earlier question and "That is too close to a question you already asked. Give a different one, about a different topic."; a second repeat is accepted. The same test filters the saved-question batches. Questions from the saved bank still use the exact-match check only.
- **Practice mode (typed), milestone 4.** Practice is a mode card on Home that can be chosen next to Learn (Mock Interview is still "Coming soon"); the same role type, question types, technologies and answer length apply, and the button reads "Start Practice". The screen follows spec 4.5 with the typed composer of 4.6: the question, an always-editable answer box with a live word count, an answer timer that starts at the first keystroke (amber past 2:30 and red past 3:30 for behavioral and resume deep-dive questions only), a Submit button and Ctrl+Enter, and the message "Say or type something first. 'I don't know' is a fine answer too." for an empty submit (an empty or spaces-only answer is never sent). No Coach call can happen before Submit: `PracticeEngine` makes the call only from submit or the retry of a failed submit, refuses a submit when no question is waiting (including a second click while the Coach reads), and tests pin this. The Coach is called with `MODE practice`, the answer, `INPUT_METHOD typed`, the timer's seconds and the word count. After feedback: **Try again** (same question, the latest answer goes in as `PREVIOUS_ATTEMPT`, never an older try; "Start from my last answer" refills the box), a click on a **follow-up** (it becomes the next question without a model call; the questions and the candidate's answers so far go into `TRANSCRIPT` as "Interviewer: ... / You: ..." lines), and **Next question** (clean slate). If the Coach call fails the answer is kept and shown; Retry sends it again, "Edit my answer" reopens the box, and neither writes a new question. Feedback and the delivery comment are shown (unlike Learn) and the model answer loses an opening warm-up. Practice questions come from the same `QuestionPicker` as Learn (saved technical questions, batches, the spread over employers, the near-duplicate check). Learn mode gains a "Try it myself" button that opens Practice on the question on screen, without showing its model answer, and the next question uses that session's filter. Practice attempts are not stored yet (no Session, PracticeItem or Attempt tables); that belongs with History.
