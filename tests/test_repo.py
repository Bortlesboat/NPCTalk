"""Repo layout rules: skills are copied where each agent looks, and every plugin manifest says the same thing."""
import filecmp
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def tree(base):
    return sorted(p.relative_to(base).as_posix() for p in base.rglob("*") if p.is_file())


def test_skill_copies_match_skills():
    source = tree(ROOT / "skills")
    assert source, "skills/ is empty"
    for copy in (".claude/skills", ".agents/skills"):
        assert tree(ROOT / copy) == source, f"{copy} differs from skills/: refresh the copy (see CONTRIBUTING.md)"
        for rel in source:
            assert filecmp.cmp(ROOT / "skills" / rel, ROOT / copy / rel, shallow=False), f"{copy}/{rel} is stale"


def test_every_skill_has_frontmatter():
    for skill in (ROOT / "skills").glob("*/SKILL.md"):
        head = skill.read_text(encoding="utf-8").split("---")
        assert len(head) >= 3, f"{skill}: no frontmatter"
        meta = dict(line.split(":", 1) for line in head[1].strip().splitlines())
        assert meta["name"].strip() == skill.parent.name, f"{skill}: name must match the folder"
        assert len(meta["description"].strip()) > 80, f"{skill}: description too short to trigger on"


def test_manifests_agree():
    manifests = {
        "plugin.json": json.loads((ROOT / "plugin.json").read_text()),
        ".claude-plugin/plugin.json": json.loads((ROOT / ".claude-plugin/plugin.json").read_text()),
        ".codex-plugin/plugin.json": json.loads((ROOT / ".codex-plugin/plugin.json").read_text()),
        ".cursor-plugin/plugin.json": json.loads((ROOT / ".cursor-plugin/plugin.json").read_text()),
        "gemini-extension.json": json.loads((ROOT / "gemini-extension.json").read_text()),
    }
    names = {k: v["name"] for k, v in manifests.items()}
    assert set(names.values()) == {"npctalk"}, names
    descriptions = {v["description"] for v in manifests.values()}
    assert len(descriptions) == 1, "plugin descriptions differ"
    versions = {k: v["version"] for k, v in manifests.items() if "version" in v}
    pyproject = re.search(r'^version = "([^"]+)"', (ROOT / "pyproject.toml").read_text(), re.M).group(1)
    assert set(versions.values()) == {pyproject}, versions


def test_instructions_point_at_agents_md():
    assert (ROOT / "CLAUDE.md").read_text().startswith("@AGENTS.md")
    assert (ROOT / "GEMINI.md").read_text().strip() == "@AGENTS.md"
