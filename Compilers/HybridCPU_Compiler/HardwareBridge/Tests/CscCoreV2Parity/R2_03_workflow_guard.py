"""R2-03 targeted regression guard for detached Actions suffix and SDK block.

This is not a general YAML parser. The GitHub Actions service must discover
real jobs and both runner operating systems must execute for CI acceptance.
"""
import argparse
from pathlib import Path
import re
import sys

FOOTER = '# R2-03 synthetic transport-only checks; no Golden source or Core consumer access.'
PIN = '11.0.100-rc.2.26504.105'


def check(source):
    if not source.endswith(FOOTER + '\n') or source.count(FOOTER) != 1:
        raise ValueError('R203_ORPHAN_SUFFIX')
    if '\t' in source or '\r' in source or '\x00' in source:
        raise ValueError('R203_BAD_YAML_FRAMING')
    for target in ('jobs:', '  windows-prod-codec:',
                   '  r203-release-stager-binary-parity:'):
        if source.count('\n' + target + '\n') != 1:
            raise ValueError('R203_JOB_STRUCTURE:' + target)
    if (source.count('        shell: pwsh\n') != 2 or
            source.count('        run: |\n') != 2 or
            source.count("          $env:PYTHONDONTWRITEBYTECODE = '1'") != 2):
        raise ValueError('R203_SHELL_STRUCTURE')
    if ('    runs-on: windows-2025\n' not in source or
            '    runs-on: ' + '$' + '{{ matrix.os }}\n' not in source or
            '        os: [ubuntu-24.04, windows-2025]\n' not in source):
        raise ValueError('R203_MISSING_RUNNERS')
    if (source.count('dotnet-version: ' + PIN) != 2 or
            source.count("-cne '" + PIN + "'") != 2):
        raise ValueError('R203_WRONG_SDK_PIN')
    matches = re.findall(r"^\s*\$sdkMatch = \[regex\]::Match\(\$sdkLine, '([^']+)'\)$",
                         source, re.MULTILINE)
    if len(matches) != 1:
        raise ValueError('R203_BROKEN_SDK_REGEX')
    expression = matches[0].replace('(?<parent>', '(?P<parent>')
    pattern = re.compile(expression)
    good = PIN + ' [/opt/hostedtoolcache/dotnet/sdk]'
    match = pattern.fullmatch(good)
    if not match or match.group('parent') != '/opt/hostedtoolcache/dotnet/sdk':
        raise ValueError('R203_INVALID_SDK_PARENT_REGEX')
    for invalid in (PIN + ' /opt/dotnet/sdk', PIN + ' [/tmp/sdk]\nBAD'):
        if pattern.fullmatch(invalid):
            raise ValueError('R203_SDK_REGEX_UNANCHORED')
    for phrase in (
        "$sdkMatch.Groups[1].Value -cne $version",
        "$sdkParent = $sdkMatch.Groups['parent'].Value",
        "'Roslyn/bincore/csc.dll'",
        "Invoke-R203 'wire-limits'",
        "Invoke-R203 'release-parity'",
        "function Invoke-R203(",
        "R203_EXEC step=$Step",
    ):
        if source.count(phrase) != 1:
            raise ValueError('R203_MISSING_OR_DUPLICATED_COMMAND:' + phrase)
    if (source.count('R2_03_checkout_inventory.py') != 2 or
            source.count('& python $audit --root') != 4):
        raise ValueError('R203_INVENTORY_NOT_WIRED')
    if source.count('--wire-limits') != 1 or source.count(').Groups[1].Value'):
        raise ValueError('R203_DETACHED_BLOCK')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('workflow', type=Path)
    args = parser.parse_args()
    source = args.workflow.read_text(encoding='utf-8')
    check(source)
    negatives = (
        ('orphan-suffix', source + ').Groups[1].Value\n'),
        ('duplicate-suffix', source + FOOTER + '\n'),
        ('broken-sdk-regex', source.replace('(?<parent>', '(?<parent', 1)),
        ('wrong-sdk-version', source.replace(PIN, '11.0.100', 1)),
        ('missing-windows-job', source.replace('  windows-prod-codec:',
                                               '  disabled-windows-prod-codec:', 1)),
        ('missing-linux-matrix', source.replace('ubuntu-24.04', 'ubuntu-22.04', 1)),
        ('duplicate-invocation', source.replace("Invoke-R203 'release-parity'",
                         "Invoke-R203 'release-parity'\\nInvoke-R203 'release-parity'", 1)),
    )
    for label, mutated in negatives:
        try:
            check(mutated)
        except (ValueError, re.error):
            print('R203_WORKFLOW_NEGATIVE_REFUSED ' + label)
        else:
            raise ValueError('R203_WORKFLOW_NEGATIVE_ACCEPTED ' + label)
    print('R203_WORKFLOW_STATIC_GUARD_PASS; GitHub YAML and job discovery separately required')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, re.error) as ex:
        print('R203_WORKFLOW_STATIC_GUARD_FAIL ' + str(ex), file=sys.stderr)
        sys.exit(1)
