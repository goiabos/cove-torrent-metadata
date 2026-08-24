# Security policy

This extension parses untrusted files with a hand-written bencode reader and makes outbound HTTP
requests from the server on the operator's behalf. It also runs **in the host's process**, so a
vulnerability here is a vulnerability in the operator's Cove instance. That is why this file exists
for something so small.

## Reporting

Please report privately, not as a public issue: use **Report a vulnerability** on this repository's
**Security** tab (GitHub private vulnerability reporting).

Include what you need to reproduce it — the input, the configuration, and what you observed. A
proof-of-concept `.torrent` should be described rather than attached: give the structure and the
field values, not a file from a tracker. (No `.torrent` is ever committed to this repository; see
[CONTRIBUTING.md](CONTRIBUTING.md).)

This is a volunteer project with no paid staff and no bug bounty. Expect a first response in days
rather than hours. Please give a fix a reasonable window before disclosing publicly.

Only the latest release is supported. Fixes ship in a new version rather than as backports.

## In scope

- **Torrent parsing.** `BencodeReader` is hand-written and reads attacker-supplied bytes: crashes,
  hangs, unbounded allocation, or reads outside the buffer.
- **The upload endpoint.** It accepts `.torrent` only, caps the size, writes by **base name only** so
  nothing can escape the watched folder, and parse-checks after writing, deleting what it cannot
  read. Any path traversal, any way past the extension or size checks, or any way to leave a file
  behind that should have been deleted.
- **The server-side cover fetch — the largest surface here.** A cover URL arrives *inside* the
  torrent, so an unchecked fetch is a request the file's author chose, made from inside the host's
  network. Requests go only to hosts the operator has named, the list ships empty, redirects are
  followed by hand so **every hop is re-checked**, only `http`/`https` is accepted, an `image/*`
  content type is required, and the size cap is enforced **while streaming** rather than trusting
  `Content-Length`. Anything that reaches a host the operator did not name — a redirect that skips
  the check, a URL parse disagreement, DNS behaviour — is in scope.
- **The cover endpoint.** It takes a URL as a parameter, which makes "is this an open proxy?" a fair
  question. It should not be: it runs the same allowlist and redirect checks an import does, from
  shared code rather than a second copy. Any divergence between the two is in scope.
- **Endpoint authorization.** Every endpoint carries Cove permission metadata. The host treats
  *missing* metadata as allow, so an endpoint that lost its declaration would be served anonymously
  with only a log warning — a library write open to anyone. A missing or wrong declaration is a
  security bug, not a tidiness bug.
- **Anything that writes to the library without review**, since the whole premise is that a torrent
  is a suggestion the user approves.

## Out of scope

- Vulnerabilities in **Cove itself**. Report those to the Cove project; if it turns out to be a host
  defect reached through this extension, tell us anyway so we can guard the path.
- The cover-host allowlist **shipping empty** and covers therefore not working until configured.
  That is the intended fail-safe default, not a bug.
- Consequences the operator explicitly opted into — notably allowlisting a host they do not trust.
- Anything requiring an attacker who already has admin access to the Cove instance or write access
  to the watched folder's parent.
- Rate-limit or pacing values being conservative. They are deliberate commitments, not an oversight.
