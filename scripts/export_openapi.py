"""Export the Development route/schema snapshot without credentials or TLS bypasses."""
import argparse
import json
from pathlib import Path
import ssl
from urllib.parse import urlsplit
from urllib.request import urlopen

EXPECTED = {
    "/api/v2/workspaces": {"get"},
    "/api/v2/workspaces/{workspaceId}/projects": {"get"},
    "/api/v2/workspaces/{workspaceId}/projects/{projectId}": {"get"},
    "/api/v2/workspaces/{workspaceId}/projects/{projectId}/tasks": {"get"},
    "/api/v2/workspaces/{workspaceId}/tasks/{taskId}": {"get"},
    "/api/v2/workspaces/{workspaceId}/projects/{projectId}/members": {"get"},
    "/api/v2/workspaces/{workspaceId}/projects/{projectId}/activity": {"get"},
    "/api/v2/workspaces/{workspaceId}/activity": {"get"},
    "/api/v2/workspaces/{workspaceId}/dashboard": {"get"},
    "/api/v1/auth/csrf": {"get"},
    "/api/v1/auth/register": {"post"}, "/api/v1/auth/login": {"post"},
    "/api/v1/auth/me": {"get"}, "/api/v1/auth/logout": {"post"},
    "/api/v1/users/me": {"patch"}, "/api/v1/dashboard": {"get"},
    "/api/v1/health/live": {"get"}, "/api/v1/health/ready": {"get"},
    "/api/v1/projects": {"get", "post"},
    "/api/v1/projects/{projectId}": {"get", "put", "delete"},
    "/api/v1/projects/{projectId}/members": {"get", "post"},
    "/api/v1/projects/{projectId}/members/{userId}": {"delete"},
    "/api/v1/projects/{projectId}/tasks": {"get", "post"},
    "/api/v1/tasks/{taskId}": {"get", "patch", "delete"},
    "/api/v1/tasks/{taskId}/status": {"patch"},
    "/api/v1/projects/{projectId}/activity": {"get"},
}
METHODS = {"get", "post", "put", "patch", "delete", "head", "options", "trace"}

def validate(document):
    actual = {p: set(v) & METHODS for p, v in document["paths"].items()}
    if actual != EXPECTED or not document.get("openapi", "").startswith("3."):
        raise ValueError("Unexpected OpenAPI inventory; review controller changes and update EXPECTED")


def normalize(document):
    document = json.loads(json.dumps(document))
    document["servers"] = [{"url": "/"}]
    return document


def check_snapshot(expected, actual):
    validate(actual)
    if normalize(expected) != normalize(actual):
        raise ValueError("OpenAPI drift detected; regenerate and review the snapshot and frontend types")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", required=True)
    parser.add_argument("--ca", help="Public CA PEM; TLS verification remains enabled")
    parser.add_argument("--output", type=Path, default=Path(__file__).resolve().parents[1] / "docs/contracts/openapi.json")
    parser.add_argument("--check", action="store_true", help="Compare to the snapshot without overwriting it")
    args = parser.parse_args()
    target = urlsplit(args.url)
    if target.username or target.password or not (target.scheme == "https" or
            (target.scheme == "http" and target.hostname in {"localhost", "127.0.0.1", "::1"})):
        parser.error("Use HTTPS or loopback HTTP without credentials in the URL")
    context = ssl.create_default_context(cafile=args.ca)
    with urlopen(args.url, context=context, timeout=20) as response:
        document = json.load(response)
    validate(document)
    document = normalize(document)
    if args.check:
        check_snapshot(json.loads(args.output.read_text(encoding="utf-8")), document)
        print("OpenAPI snapshot matches live API.")
    else:
        args.output.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
        print("Exported 17 paths / 24 operations to", args.output)

if __name__ == "__main__":
    main()
