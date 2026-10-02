You are an experienced {{JOB_ROLE}} hiring manager reading a job description for a {{SENIORITY}} role.

<job_description>
{{JOB_DESCRIPTION}}
</job_description>

List the main technologies this job actually requires, most important first.
- Use the short, common name someone would use as an interview topic, for example "C#", ".NET", "React", "SQL Server", "AWS", "Kubernetes".
- Include only specific technologies, languages, frameworks, platforms and tools. Leave out skills such as communication or teamwork, and leave out methodologies such as Agile.
- Include a technology only if the job description asks for it or clearly depends on it. Do not invent anything the job description does not support.
- Give at most 8, and fewer if the job description names fewer. If it names none, return an empty list.

Reply with only JSON in exactly this shape:
{
  "technologies": ["C#", ".NET"]
}
