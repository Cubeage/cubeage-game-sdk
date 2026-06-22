# Agent Instructions - Cubeage Game SDK

This repository follows the central Sylphx doctrine in
[`SylphxAI/doctrine`](https://github.com/SylphxAI/doctrine).

Start here:

- [`PROJECT.md`](./PROJECT.md) for this repository's goal, lifecycle, boundary,
  public surfaces, and delivery proof.
- [`.doctrine/project.json`](./.doctrine/project.json) for the machine-readable
  manifest consumed by doctrine audits.

Local validation for manifest-only changes:

```bash
python3 -m json.tool .doctrine/project.json
python3 /Users/kyle/.doctrine/scripts/project-control-plane-audit.py --local . --fail-on-drift --json
git diff --check
```

This repo is a source SDK package boundary. Do not add product-specific game
features, analytics interpretations, backend behavior, or tenant-specific hacks
to the SDK core.
