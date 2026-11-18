# Code of Conduct — Requirements

**Depth**: Minimal (documentation only; no code, no user-visible app change)
**Branch**: `docs/code-of-conduct`, cut from `master` at `e792e1a`
**Requested**: 2026-11-17T23:37:32Z — "A Code of Conduct should be added to CalCrony. Make it match GitHub's recommended standards."
**Approved**: 2026-11-17T23:43:57Z — "Recommended. Continue." (Q1–Q4 all A)

## Intent analysis

GitHub's community profile for `JJWren/CalCrony` stands at 85 percent. Its `code_of_conduct` and `code_of_conduct_file` entries are empty; README, CONTRIBUTING, LICENSE and the pull request template are present. GitHub's own guidance ("Adding a code of conduct to your project") is a `CODE_OF_CONDUCT.md` in the repository root, `docs/` or `.github/`, and the template it offers is the Contributor Covenant (`GET /codes_of_conduct` lists `contributor_covenant` and `django`). GitHub's template is version 2.0 and leaves one blank: `[INSERT CONTACT METHOD]`.

## Functional requirements

- **CC1** A `CODE_OF_CONDUCT.md` in the repository root, with the Contributor Covenant text unchanged apart from the contact method (CC2) and one sentence naming CalCrony's spaces (CC3), so GitHub recognises it and lists it in the community profile.
- **CC2** The contact method for reports is filled in (Q2).
- **CC3** It says where it applies: the GitHub repository and the official CalCrony Discord server (Q3).
- **CC4** It is linked from the README's "Contributing, releases & security" paragraph and from `CONTRIBUTING.md` (Q4).
- **CC5** The attribution section of the Covenant is kept, as its licence (CC BY 4.0) asks.

## Non-functional requirements

- **NF1** The change is `docs:` and cuts no release (repository-only material, per `CONTRIBUTING.md`).
- **NF2** The tests that pin the README (`DocsPageTests`, `RepositoryFilesTests`) stay green.
- **NF3** No hidden characters in the new file.

## Questions

Recommendations are marked. Answer each with a letter after `[Answer]:`.

**Q1. Which version of the Contributor Covenant?**
- A) 2.1 — the latest of the 2.x line GitHub's template comes from, recognised by GitHub's detection, with 2.1's wording updates and fixed links **(recommended)**
- B) 2.0 — exactly GitHub's built-in template
- C) 3.0 — the newest (2025) rewrite; GitHub may list it as "Other" rather than recognising it
- D) Citizen Code of Conduct
- E) Other

[Answer]: A

**Q2. Where should reports go?**
- A) The email already public in `SECURITY.md`, with "CalCrony conduct" in the subject **(recommended)**
- B) A direct message to a moderator in the CalCrony Discord server
- C) Both A and B
- D) A new address you will provide
- E) Other

[Answer]: A

**Q3. Where should it apply?**
- A) The GitHub repository and the official CalCrony Discord server **(recommended)**
- B) The GitHub repository only
- C) Other

[Answer]: A

**Q4. Where should it be linked?**
- A) README's "Contributing, releases & security" paragraph and `CONTRIBUTING.md` **(recommended)**
- B) A as well as the issue forms' contact links
- C) The file only, no links
- D) Other

[Answer]: A

## Stages

- User stories: skipped — documentation only, single maintainer.
- Application design, units, functional and NFR design: skipped — no code.
- Code generation: one unit — the file, two links, records.
- Build and test: run the full suite (README-pinning tests), then a pull request on your go-ahead.
