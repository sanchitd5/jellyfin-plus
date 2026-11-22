# Plan: noop-probe

The brief is only the label `noop-probe`. It has no bead, no `## Acceptance`, no `## Do not` and no `## Where`. A search of the repo for `noop-probe` found nothing, and `beads_list` with that query returned `[]`.

I treat this as a probe of the pipeline and change nothing. The diff stays empty, so no PR body is needed. If a real change was intended, the brief needs a bead id and acceptance criteria.

```yaml
{
  "goal": "No-op probe: no change requested; leave the worktree unchanged.",
  "files": [],
  "test_commands": [],
  "tdd": "n/a: no files changed",
  "fast_path": true,
  "reuse_check": "n/a: docs-only",
  "acceptance": [
    "The worktree diff is empty.",
    "No commit, push or PR description is produced."
  ],
  "premises": [
    {
      "id": "P1",
      "claim": "The brief names no bead and no work, so there is nothing to implement.",
      "label": "VERIFIED",
      "check": "Grep for 'noop-probe' in the repo returned no files, and beads_list query 'noop-probe' returned [].",
      "load_bearing": true
    },
    {
      "id": "P2",
      "claim": "No pre-existing .workbench plan or brief exists in this checkout.",
      "label": "VERIFIED",
      "check": "Glob .workbench/* returned no files before this plan was written.",
      "load_bearing": false
    }
  ]
}
```
