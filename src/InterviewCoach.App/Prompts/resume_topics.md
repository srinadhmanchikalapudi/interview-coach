You are an experienced interviewer reading the resume of a candidate for a {{SENIORITY}} {{JOB_ROLE}} role.

<candidate_resume>
{{RESUME}}
</candidate_resume>

List the employers and projects on this resume and what the candidate did on each, so that interview questions can be spread across the whole resume instead of one job.
- One entry per employer, client or clearly separate project, most recent first. At most 8 entries.
- For each entry give 2 to 5 highlights: specific things the resume says the candidate built, changed, migrated, owned or measured. Each highlight is a short phrase of at most 12 words, in the resume's own wording (the real technologies, systems and numbers). Do not invent anything the resume does not say.
- "employer" is the short name as written on the resume. "project" is at most 4 words naming the project or area, or an empty string when the entry is simply a job.
- If some work has no employer or project named, group it under the employer "Other work".

Reply with only JSON in exactly this shape:
{
  "topics": [
    { "employer": "Acme Corp", "project": "billing platform", "highlights": ["moved billing from a monolith to services", "cut invoice run time from 4 hours to 20 minutes"] }
  ]
}
