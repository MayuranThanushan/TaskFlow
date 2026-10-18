"""Fail CI if the backend suite was empty, failed, or skipped required tests."""

import argparse
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def verify(path: Path) -> None:
    root = ET.parse(path).getroot()
    namespace = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    results = root.findall(".//t:UnitTestResult", namespace)
    if not results:
        raise ValueError("No executed backend tests were reported.")
    rejected = [result for result in results if result.get("outcome") != "Passed"]
    if rejected:
        raise ValueError(f"{len(rejected)} backend test(s) failed or were skipped.")
    print(f"Verified {len(results)} passing backend tests with no skips.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    args = parser.parse_args()
    try:
        verify(args.report)
    except (OSError, ET.ParseError, ValueError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
