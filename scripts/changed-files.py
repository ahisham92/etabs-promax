#!/usr/bin/env python3
"""What an update has to download: the file list of a release, and a zip of only the files that changed.

    changed-files.py <new dist folder> <previous dist folder, or ''> <files.json to write> <changed zip to write>

files.json lists every file of the new package with its SHA-256 and size, and says which of them are in the changed
zip (the ones the previous release did not have with the same content). The updater builds the new version from the
files it already has and downloads only the changed zip; when that is not enough it takes the whole package as before.
"""
import hashlib, json, os, sys, zipfile


def sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def files(folder):
    found = {}
    for root, _, names in os.walk(folder):
        for name in names:
            full = os.path.join(root, name)
            found[os.path.relpath(full, folder).replace(os.sep, '/')] = full
    return found


def main(new, previous, manifest, changed_zip):
    now = files(new)
    before = {rel: sha(full) for rel, full in files(previous).items()} if previous and os.path.isdir(previous) else {}
    entries = []
    with zipfile.ZipFile(changed_zip, 'w', zipfile.ZIP_DEFLATED) as z:
        for rel in sorted(now):
            digest = sha(now[rel])
            changed = before.get(rel) != digest
            if changed:
                z.write(now[rel], 'dist/' + rel)
            entries.append({'path': rel, 'sha256': digest, 'size': os.path.getsize(now[rel]), 'changed': changed})
    with open(manifest, 'w') as f:
        json.dump({'format': 1, 'files': entries}, f, indent=1)
    print(f"{sum(e['changed'] for e in entries)} of {len(entries)} files changed; "
          f"changed zip {os.path.getsize(changed_zip)} bytes")


if __name__ == '__main__':
    if len(sys.argv) != 5:
        sys.exit(__doc__)
    main(*sys.argv[1:])
