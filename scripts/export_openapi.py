"""Export the Development route/schema snapshot without credentials or TLS bypasses."""
import argparse
import json
from pathlib import Path
import ssl
from urllib.parse import urlsplit
from urllib.request import urlopen

EXPECTED = {
    "/api/v2/workspaces/{workspaceId}/invitations": {"get", "post"},
    "/api/v2/workspaces/{workspaceId}/invitations/{invitationId}/resend": {"post"},
    "/api/v2/workspaces/{workspaceId}/invitations/{invitationId}/revoke": {"post"},
    "/api/v2/workspaces/{workspaceId}/invitations/{invitationId}/preview": {"post"},
    "/api/v2/workspaces/{workspaceId}/invitations/{invitationId}/accept": {"post"},
    "/api/v2/workspaces/{workspaceId}/policy": {"get", "put"},
    "/api/v2/workspaces/{workspaceId}/audit/console": {"get"},
    "/api/v2/workspaces/{workspaceId}/audit/console/exports": {"post"},
    "/api/v2/workspaces/{workspaceId}/audit/console/exports/download": {"post"},
    "/api/v2/workspaces/{workspaceId}/members": {"get"},
    "/api/v2/workspaces/{workspaceId}/audit": {"get"},
    "/api/v2/workspaces/{workspaceId}/members/{userId}/role": {"patch"},
    "/api/v2/workspaces/{workspaceId}/members/{userId}/state": {"patch"},
    "/api/v2/workspaces/{workspaceId}/leave": {"post"},
    "/api/v2/workspaces/{workspaceId}/ownership": {"post"},
    "/api/v2/workspaces/{workspaceId}/projects/{projectId}/ownership": {"post"},
    "/api/v2/workspaces/{workspaceId}": {"get"},
    "/api/v2/workspaces": {"get", "post"},
    "/api/v2/workspaces/{workspaceId}/tasks/{taskId}/status": {"patch"},
    "/api/v2/workspaces/{workspaceId}/projects/{projectId}/members/{userId}": {"delete", "put"},
    "/api/v2/workspaces/{workspaceId}/projects": {"get", "post"},
    "/api/v2/workspaces/{workspaceId}/projects/{projectId}": {"get", "put", "delete"},
    "/api/v2/workspaces/{workspaceId}/projects/{projectId}/tasks": {"get", "post"},
    "/api/v2/workspaces/{workspaceId}/tasks/{taskId}": {"get", "patch", "delete"},
    "/api/v2/workspaces/{workspaceId}/projects/{projectId}/members": {"get", "post"},
    "/api/v2/workspaces/{workspaceId}/projects/{projectId}/activity": {"get"},
    "/api/v2/workspaces/{workspaceId}/activity": {"get"},
    "/api/v2/workspaces/{workspaceId}/dashboard": {"get"},
    "/api/v1/auth/capabilities": {"get"},
    "/api/v1/auth/security": {"get"},
    "/api/v1/auth/forgot-password": {"post"},
    "/api/v1/auth/request-verification": {"post"},
    "/api/v1/auth/confirm-email": {"post"},
    "/api/v1/auth/reset-password": {"post"},
    "/api/v1/auth/change-password": {"post"},
    "/api/v1/auth/reauthenticate": {"post"},
    "/api/v1/auth/mfa": {"get"},
    "/api/v1/auth/mfa/setup": {"post"},
    "/api/v1/auth/mfa/confirm": {"post"},
    "/api/v1/auth/mfa/recovery-codes": {"post"},
    "/api/v1/auth/mfa/disable": {"post"},
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
        print(f"Exported {len(EXPECTED)} paths / {sum(len(methods) for methods in EXPECTED.values())} operations to", args.output)

if __name__ == "__main__":
    main()
