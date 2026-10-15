@AGENTS.md

## Claude Code specifics
- Installed as a plugin, the skills are namespaced (`/npctalk:make-npcs-talk`), the fal MCP server comes from
  `.mcp.json` (needs `FAL_KEY` in the environment), and a SessionStart hook puts `npctalk` on PATH.
- Without `FAL_KEY`, `.mcp.json` sends the literal `${FAL_KEY}` and fal answers 401. Start Claude Code with the key
  set, or add fal's OAuth endpoint as a second server
  (`claude mcp add --transport http fal-oauth https://mcp.fal.ai/mcp-relay`) and sign in through `/mcp`.
- In a clone, `.claude/settings.json` adds the same PATH hook, and `.claude/skills` is a copy of `skills/`.
