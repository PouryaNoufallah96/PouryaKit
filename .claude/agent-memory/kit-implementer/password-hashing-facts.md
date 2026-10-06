---
name: password-hashing-facts
description: Ticket #40 facts - PBKDF2 empty or short stored key verifies without a length floor, base64 decoder skips white space, 600k iterations are fast enough for tests, citing OWASP by commit, testing an internal singleton without the DI package
metadata:
  type: project
---

Verified in ticket #40 (2026-10-07), .NET 10 on macOS arm64:

- **A stored PBKDF2 key needs a length floor.** Verify derives `stored.Key.Length` bytes, and PBKDF2's shorter output is a prefix of its longer one. So a truncated key verifies, and an **empty** key verifies too, because `FixedTimeEquals` of two empty spans is true. Mutation proof: dropping the 32-byte minimum turned the "empty key" and "key truncated to 31 bytes" cases green.
- `Convert.TryFromBase64String` skips white space. Strict parsing means re-encoding the bytes and comparing the text ordinally. A `stackalloc` buffer of the maximum length rejects longer input on its own, because the call returns false.
- **`int.TryParse(text, NumberStyles.None, InvariantCulture, …)` accepts trailing NUL characters** (`"600000\0"` parses; review finding F2) and leading zeros. It refuses a sign, white space and non-ASCII digits. For strict parsing, check the length bound, no leading `'0'` and `!text.AsSpan().ContainsAnyExceptInRange('0', '9')` before calling it.
- **A clean consumer that exercises a packed API:** since #40, `validate.py` copies `scripts/packaging/consumers/<consumer name>.cs` to Program.cs for any consumer that has one; per-package consumers are named by package id, so `PouryaKit.Security.cs`. Consumers are only built, never run. This leaves `inventory.json` alone, which parallel tickets edit. A full `validate.py --no-fetch` took ~6 min: 15 packages, 17 consumers, a Release runtime suite of 479.
- `Rfc2898DeriveBytes.Pbkdf2(string/char span, …)` throws `EncoderFallbackException` for a lone surrogate. A Verify that never throws has to catch it.
- **Speed:** PBKDF2-HMAC-SHA256 at 600,000 iterations takes about 65 ms. 53 tests run in about 4 s, so tests keep the OWASP minimum and do not need a lowered floor.
- **Citing OWASP:** the cheat sheet page has no date. Pin it with `gh api "repos/OWASP/CheatSheetSeries/commits?path=cheatsheets/Password_Storage_Cheat_Sheet.md&per_page=3"` and read the content at that ref. The ref was `3c07d4a` on 2026-10-04: SHA256 at 600k, SHA512 at 220k, SHA1 legacy only.
- **Testing an internal singleton** from `PouryaKit.Security.Tests` without `Microsoft.Extensions.DependencyInjection`: register an instance and read `services.Single(d => d.ServiceType == …).ImplementationInstance`.
- **Constant-time red-once** is structural. Try every byte offset of each method's IL for `call`/`callvirt` plus a token, `Module.ResolveMethod` in a try/catch, then assert that `FixedTimeEquals` is present and `SequenceEqual` is absent. `ArchitectureTests/Il.cs` was outside allowed_files.
- **The wave branch moved again mid-package** (#56 edited `security.md`, `acceptance.md`, `architecture/README.md` and the I.3 Security row). Commit, `git rebase feat/wave-2`, then resolve with a scratchpad python script that keeps the tip's text and re-applies the addition.
- The worktree guard refused `cd … && cat > a <<EOF … cat > b <<EOF` (several heredocs) as "too complex". Write each file with Write instead.

**Why:** each one decided a #40 design point or cost a retry.
**How to apply:** for any ticket that parses stored credentials or tokens, adds Security API, or cites external guidance. Related: [[security-permission-facts]], [[worktree-and-check-gotchas]].
