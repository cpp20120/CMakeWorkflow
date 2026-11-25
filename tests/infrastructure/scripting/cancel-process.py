"""Signal a running authoring application and check supervised child termination."""
import os
import re
import selectors
import signal
import subprocess
import sys
from pathlib import Path

cli, script = sys.argv[1:]
process = subprocess.Popen(
    [cli, "script", "compose", script, "--", "timeout"],
    stdout=subprocess.DEVNULL,
    stderr=subprocess.PIPE,
    start_new_session=True,
)
try:
    with selectors.DefaultSelector() as selector:
        selector.register(process.stderr, selectors.EVENT_READ)
        assert selector.select(30), "authoring process did not start"
        line = process.stderr.readline()
    pid_match = re.fullmatch(rb"author child pid (\d+)\r?\n", line)
    assert pid_match, line
    pid = int(pid_match.group(1))
    process.send_signal(signal.SIGINT)
    output = process.communicate(timeout=20)[1]
    assert process.returncode == 130, (process.returncode, output)
    state = Path(f"/proc/{pid}/stat")
    try:
        # An adopted zombie has terminated even if its new parent has yet to reap it.
        assert state.read_text().split(") ", 1)[1][0] == "Z", f"authoring child {pid} is still running"
    except FileNotFoundError:
        pass
    print("PASS script Ctrl+C and child termination")
finally:
    if process.poll() is None:
        os.killpg(process.pid, signal.SIGKILL)
        process.wait()
