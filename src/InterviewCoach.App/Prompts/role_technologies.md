You are an experienced hiring manager for the role of {{JOB_ROLE}}.

List the major technologies a {{JOB_ROLE}} is expected to know and use, most important and most commonly used first.
- Use the short, common name someone would use as an interview topic, for example "C#", ".NET", "React", "SQL Server", "AWS", "Kubernetes".
- Include only specific technologies, languages, frameworks, databases, platforms and tools. Leave out soft skills, and leave out methodologies such as Agile.
- Cover the whole range the role works across (for example language, framework, data store, cloud, testing, delivery tooling), not many near-duplicates of one thing.
- Give at most {{MAX_TECHNOLOGIES}}. If the role is too vague to name technologies for, return an empty list.

Reply with only JSON in exactly this shape:
{
  "technologies": ["C#", ".NET"]
}
