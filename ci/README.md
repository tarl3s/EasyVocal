# CI (GitHub Actions) — parked here

`github-actions-ci.yml` is the CI workflow (build and tests on Windows, macOS and Linux). It lives here, outside
`.github/workflows/`, because pushing a workflow file needs a GitHub token with the `workflow` scope.

To enable CI:

1. `gh auth refresh -h github.com -s workflow`
2. Move this file: `git mv ci/github-actions-ci.yml .github/workflows/ci.yml`
3. Commit and push.
