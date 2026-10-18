"""Create or verify a local release receipt; hashes detect drift, not publisher authenticity."""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import subprocess
import sys
from backup_recovery import RecoveryError, digest, private_write, safe_file

MANIFEST = 'release-manifest.json'

def file_map(root):
    result = {}
    for path in sorted(root.rglob('*')):
        if path.is_symlink() or path.is_junction(): raise RecoveryError('Linked release payloads are not supported.')
        if path.is_file() and path != root / MANIFEST:
            result[path.relative_to(root).as_posix()] = digest(path)
    if not result: raise RecoveryError('The release is empty.')
    return result

def create(root, repository):
    root = Path(root).resolve(strict=True); repository = Path(repository).resolve(strict=True)
    revision = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repository, stderr=subprocess.DEVNULL).decode().strip()
    if not re.fullmatch('[a-f0-9]{40}', revision): raise RecoveryError('A source commit is required.')
    dirty = bool(subprocess.check_output(['git', 'status', '--porcelain'], cwd=repository))
    migrations = sorted(p.stem for p in (repository / 'TaskFlow-BE/TaskFlow.Infrastructure/Persistence/Migrations').glob('*.cs') if re.fullmatch(r'[0-9]{14}_[A-Za-z0-9_]+', p.stem))
    manifest = {'formatVersion': 1, 'createdAtUtc': datetime.now(timezone.utc).isoformat(), 'sourceRevision': revision, 'sourceDirty': dirty, 'migrations': migrations, 'files': file_map(root)}
    private_write(root / MANIFEST, (json.dumps(manifest, indent=2) + '\n').encode('utf-8'))
    return manifest

def verify(root):
    root = Path(root).resolve(strict=True)
    manifest = json.loads((root / MANIFEST).read_text(encoding='utf-8'))
    if manifest.get('formatVersion') != 1 or not isinstance(manifest.get('files'), dict) or not manifest['files']:
        raise RecoveryError('Unsupported release manifest.')
    for name, expected in manifest['files'].items():
        if not isinstance(expected, str) or not re.fullmatch('[a-f0-9]{64}', expected) or digest(safe_file(root, name)) != expected:
            raise RecoveryError('Release checksum verification failed.')
    if file_map(root) != manifest['files']:
        raise RecoveryError('Release payload contains missing or extra files.')
    return manifest

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['create', 'verify']); parser.add_argument('directory')
    parser.add_argument('--repository', default=str(Path(__file__).resolve().parent.parent))
    args = parser.parse_args()
    try:
        if args.command == 'create': create(args.directory, args.repository)
        result = verify(args.directory)
        print(f"Release receipt verified: {len(result['files'])} files; source {result['sourceRevision']}; dirty={result['sourceDirty']}.")
    except (RecoveryError, OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
        print('Release verification failed: invalid metadata, changed payload or unavailable source repository.', file=sys.stderr)
        return 1
    return 0

if __name__ == '__main__': sys.exit(main())
