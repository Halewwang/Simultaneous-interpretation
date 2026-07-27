# Windows four-endpoint audio lab evidence

Status: pending physical lab execution and Windows PowerShell script TDD. No
hosted-CI result is lab, installed driver, endpoint, meeting, or crash-silence
evidence.

Before installation, run `EMKE.AudioSmoke.exe --scenario enumerate`; the
expected controlled nonzero output is `discovery=driverMissing`. After install,
run `enumerate`, the three normal routes, underrun, the two external failure
observations, and `crash-after-mic-open` from the Task 7 brief. The two
failure scenarios set the public fail-safe route; a genuine stream failure must
be induced and observed separately because the production C ABI deliberately
does not export a realtime failure-injection hook.

The pending Windows script must run as a behavior-tested, elevated,
confirmation-gated operation: verify every package member SHA-256 and a valid
test/Microsoft catalog before `pnputil`, print driver version/hardware ID, and
verify the devnode plus all four stable role properties. Its matching collector
must write only source commit, UTC time, OS build, driver ABI/hash/signature,
four anonymized role hashes, scenario counters/results, and an optional SHA-256
of an off-git recording bundle. Do not commit recordings or opaque endpoint
IDs. Record observed evidence bundles outside git and cite their SHA-256 in the
release gate.
