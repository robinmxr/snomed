# Purging the credentials file from git history

An environment file with database credentials was committed earlier. Removing it from the
latest commit is not enough, because it stays in older commits. This guide is for the repository
owner to run by hand. It rewrites history, so it was deliberately not done automatically.

## 1. Rotate the database password first

Treat the old password as public. Change it on the database server before anything else.
Do this even if the repository was never public, since clones and backups may exist.

## 2. Purge on a fresh clone

Use a fresh clone so your working copy is untouched. Install `git-filter-repo` first
(`brew install git-filter-repo`).

```sh
git clone <your-remote-url> snomed-purge
cd snomed-purge

# Remove the file from every commit. Add one --path line per file to purge.
git filter-repo --invert-paths --path src/SnomedSearch.Console/.env

# Also drop build output and macOS files that were tracked by mistake
git filter-repo --invert-paths --path-glob '**/bin/*' --path-glob '**/obj/*' --path-glob '**/.DS_Store' --path .DS_Store
```

`git filter-repo` removes the `origin` remote on purpose. Add it back and check the result:

```sh
git remote add origin <your-remote-url>
git log --all --oneline -- src/SnomedSearch.Console/.env   # should print nothing
git rev-list --all | wc -l                                   # sanity check the history is still there
```

## 3. Force push (required)

Rewritten history has new commit ids, so a normal push is rejected.

```sh
git push --force --all origin
git push --force --tags origin
```

Everyone with an existing clone must delete it and clone again, or they may push the old history back.

## 4. Afterwards

- If the repository was ever public or shared, assume the old password was seen. Rotation (step 1) is the real fix.
- Ask the hosting provider to run garbage collection or clear cached views if the old commits stay reachable by id.
- The client-named database and user defaults also appear in old commits. The purge above does not remove
  them from old `Program.cs` revisions. If that matters, add `--replace-text` rules with a file you write
  yourself, listing the strings to replace.
