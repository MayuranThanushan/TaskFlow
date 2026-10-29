"""Bounded, read-only NDJSON export for the platform operations security-audit identity."""
import argparse
import json
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def query(schema, after, limit):
    if not re.fullmatch(r'[A-Za-z0-9_]{1,64}', schema) or not 0 <= after <= 9223372036854775807 or not 1 <= limit <= 1000:
        raise ValueError('Invalid schema, cursor or limit (maximum 1000).')
    return f"""SELECT JSON_OBJECT('id',CAST(Id AS CHAR),'schemaVersion',SchemaVersion,
        'action',Action,'outcome',Outcome,'actorUserId',CAST(ActorUserId AS CHAR),
        'subjectUserId',CAST(SubjectUserId AS CHAR),'subjectType',SubjectType,
        'correlationId',CorrelationId,'routeTemplate',RouteTemplate,'statusCode',StatusCode,
        'coalesced',IF(Coalesced,CAST('true' AS JSON),CAST('false' AS JSON)),
        'createdAtUtc',DATE_FORMAT(CreatedAtUtc,'%Y-%m-%dT%H:%i:%s.%fZ'))
        FROM `{schema}`.`SecurityEvents` WHERE Id > {after} ORDER BY Id LIMIT {limit}"""


def destination(path):
    path = Path(path).resolve()
    if any(part.lower() in {'wwwroot', 'dist', 'public'} for part in path.parts):
        raise ValueError('Use a private destination outside web roots.')
    if path.is_relative_to(ROOT) and not path.is_relative_to(ROOT / '.local'):
        raise ValueError('Never put security exports in source-controlled or frontend directories.')
    if path.exists() or not path.parent.is_dir():
        raise ValueError('Provide a new file in an existing private directory.')
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--mysql', required=True)
    parser.add_argument('--defaults-file', required=True, type=Path, help='Private operations SELECT-only credentials; never runtime credentials')
    parser.add_argument('--schema', required=True)
    parser.add_argument('--after-id', type=int, default=0)
    parser.add_argument('--limit', type=int, default=100)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    try:
        sql = query(args.schema, args.after_id, args.limit)
        output = destination(args.output)
        if not args.defaults_file.is_file():
            raise ValueError('Private credential file is unavailable.')
        result = subprocess.run([args.mysql, '--defaults-extra-file=' + str(args.defaults_file.resolve()),
                                 '--batch', '--skip-column-names', '--raw', '-e', sql], capture_output=True, check=False)
        if result.returncode:
            raise ValueError('Security export query failed; private server diagnostics withheld.')
        rows = [json.loads(line) for line in result.stdout.decode('utf-8').splitlines() if line.strip()]
        if len(rows) > args.limit:
            raise ValueError('Unexpected export row count.')
        with output.open('x', encoding='utf-8') as stream:
            for row in rows:
                stream.write(json.dumps(row, separators=(',', ':')) + '\n')
        print(f'Exported {len(rows)} security events. Protect the file as restricted account data.')
    except (ValueError, OSError, UnicodeError) as error:
        parser.exit(1, f'Security export failed: {error}\n')


if __name__ == '__main__':
    main()
