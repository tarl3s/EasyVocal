@AGENTS.md

# Claude Code specifics

Everything in `AGENTS.md` applies.

- **Language**: answer the owner in Brazilian Portuguese; write code, commits and docs in English.
- **Shells**: Windows. The Bash tool is Git Bash (use it for `sed`/`grep` edits). In the PowerShell tool, never pipe
  source files through `Get-Content | Set-Content` (UTF-8 damage, see AGENTS.md). Python isn't installed.
- Never push, tag or publish a release without the owner's word.
