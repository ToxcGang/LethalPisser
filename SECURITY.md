# Security Policy

## Supported Versions

Security fixes are provided for the latest release of LethalPisser and the current development branch.

| Version | Supported |
|---------|-----------|
| latest  | Yes       |
| older   | No        |

## Reporting a Vulnerability

If you believe you have found a security issue in LethalPisser, please report it privately to the maintainer instead of opening a public issue.

Private contact method:
- joeurcino@proton.me

Please include:
- a clear description of the issue
- steps to reproduce it
- your game version
- your mod loader / framework version
- the LethalPisser version
- any relevant screenshots, logs, or sample code

Please avoid sharing exploit details publicly until the issue has been reviewed and addressed.

## Scope

LethalPisser is a Lethal Company gameplay mod that adds a simple player action triggered by keyboard or controller input.

The mod is intended to run locally inside the game and should not collect personal data.

## Security Principles

LethalPisser follows these principles:
- minimal data storage
- least-privilege behavior where practical
- no remote code execution
- no intentional data collection
- no transmission of user data to external services

## What to Report

Please report issues such as:
- unauthorized data access
- unexpected network activity
- privilege escalation
- code execution vulnerabilities
- injection issues
- crashes caused by malformed input if they expose a security weakness
- persistence or storage issues that could leak local data

## Out of Scope

The following are generally not considered security vulnerabilities:
- feature requests
- cosmetic bugs
- gameplay balance concerns
- input mapping preferences
- compatibility issues caused by changes in the base game or other mods
- normal crashes that do not expose data or enable abuse

## Disclosure Policy

Confirmed vulnerabilities will be addressed in a future release and disclosed after a reasonable remediation period.
