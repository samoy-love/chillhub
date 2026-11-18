#!/usr/bin/env python3
"""Validate the release's single source of truth before any upload."""
import json
from pathlib import Path
import re
import sys
import tomllib
from urllib.error import HTTPError
from urllib.request import urlopen


def version_tuple(value):
    if not isinstance(value, str) or not re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", value):
        raise ValueError(f"Invalid release version: {value!r}")
    return tuple(map(int, value.split('.')))


def validate_local(root):
    project = root / 'launcher/ChillHub.Native'
    declared = tomllib.loads((project / 'Cargo.toml').read_text())['package']['version']
    version_tuple(declared)
    lock = tomllib.loads((project / 'Cargo.lock').read_text())
    package = [p for p in lock['package'] if p['name'] == 'chillhub-native']
    if len(package) != 1 or package[0]['version'] != declared:
        raise ValueError('Cargo.toml and Cargo.lock versions differ')
    history = json.loads((project / 'Data/changelog.json').read_text(encoding='utf-8-sig'))
    if not history or history[0]['version'] != declared:
        raise ValueError('Newest changelog entry must describe the declared release')
    return declared


def main():
    root = Path(__file__).resolve().parents[2]
    declared = validate_local(root)
    endpoint = 'https://launcher.samoy.love/manifests/launcher/'
    with urlopen(endpoint + 'latest.json', timeout=20) as response:
        live = json.load(response)['version']
    if version_tuple(declared) <= version_tuple(live):
        raise ValueError(f'Release {declared} must be newer than production {live}')
    try:
        with urlopen(endpoint + declared + '.json', timeout=20):
            raise ValueError(f'Version {declared} is already uploaded; choose a new version')
    except HTTPError as error:
        if error.code != 404:
            raise
    print(f'Native {declared} > production {live}; version is available')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(f'::error::{error}', file=sys.stderr)
        sys.exit(1)
