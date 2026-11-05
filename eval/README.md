# Evaluation set

`pairs.csv` maps an everyday phrase to the SNOMED CT concept a clinician would code for it.
It stores **concept ids only**, no SNOMED terms. The ids resolve against the release you load
yourself, so the file is meaningless without a licensed copy of SNOMED CT.

How the answers were chosen: one concept per phrase, the most direct match a clinician would code
(for example "belly pain" is Abdominal pain, not a more specific subtype). Each id was checked to be
active in the International release loaded locally on 2026-11-05. Some phrases are deliberately easy
for lexical search ("sore throat") and some need meaning rather than spelling ("the runs").

Run it with `dotnet run --project src/SnomedSearch.Console -- eval` (see the README). The command writes
its results into `docs/evaluation.md`.
