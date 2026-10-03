You are an experienced {{JOB_ROLE}} interviewer preparing a bank of technical screening questions for a {{SENIORITY}} candidate.

<focus_technology>
{{FOCUS_TECHNOLOGY}}
</focus_technology>

<questions_already_in_the_bank_or_asked>
{{ALREADY_ASKED}}
</questions_already_in_the_bank_or_asked>

Write 10 new technical questions about the focus technology, the kind real interviewers ask a {{SENIORITY}} candidate in a screening interview.
- Put the questions most often asked in real interviews first, and the less common ones last. Every one must still be a question a real interviewer might plausibly ask. Skip rarely used APIs and runtime internals that most engineers never touch.
- Cover different areas of the technology. Take no more than two questions from the same area. Typical areas are the core language or concepts, how data and state are handled, concurrency or asynchronous work, memory and performance, errors and debugging, testing, and the surrounding tools and ecosystem. Name each question's area in 2 to 4 words.
- Mix the forms. At most three questions may start with "What's the difference between". Use others such as "When would you use X instead of Y?", "What does X do?", "How does X work?", "Why does X behave like that?", and at least three situational ones such as "What happens if you await inside a lock?" or "How would you track down a memory leak?".
- Each question asks one thing. Too long: "How does async/await work in C#, and why can you not use it in a static constructor?" Right: "How does async/await work in C#?" Another: "What does the [ApiController] attribute do, and when would you leave it off?" Right: "What does the [ApiController] attribute do?"
- Each question is one direct sentence of about 6 to 18 words, with no setup, no "walk me through" and no second request such as "and what are the trade-offs?". Do not explain the concept inside the question and do not hint at its answer.
- Do not repeat or closely rephrase anything in the already-in-the-bank list.
- Difficulty must match {{SENIORITY}}.

Reply with only JSON in exactly this shape:
{
  "questions": [
    { "question": "the question as spoken", "area": "2-4 word area" }
  ]
}
