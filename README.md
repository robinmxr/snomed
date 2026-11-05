# SNOMED CT search

A .NET and PostgreSQL service for searching the SNOMED CT clinical terminology.
A full README with setup, architecture and measured results is added in a later phase.

## Licensing

The code in this repository is MIT licensed (see `LICENSE`).

**SNOMED CT content is licensed separately and is not included here.** You must obtain
the release files yourself from your national release centre or from SNOMED International,
under the terms that apply to you. This repository never contains SNOMED data; it loads
the release folder that you point it at. Test fixtures are synthetic, with invented ids and terms.

## Configuration

Copy `.env.example` to `.env` and fill in the values. The app stops at startup and names any
missing variable. There are no built-in defaults for host, port, database, user or password.
